using System.Globalization;
using FastDbExplorer.Application.Abstractions;
using FastDbExplorer.Domain;

namespace FastDbExplorer.Infrastructure.Monitoring;

/// <summary>
/// One check = "which records appeared since the last check, and which of them match the user's conditions?".
/// Steps: read MAX(watermark) once, query only the window (last, upper] with the conditions on the server,
/// page through scalar matches (OFFSET, ordered by watermark + primary key); proximity rules are then checked on each
/// explicit-column page before applying the report cap.
/// The first check only records the starting point, so old data never floods the user with alerts.
/// For date watermarks the newest <see cref="MonitorDefinition.SettleSeconds"/> are held back (see <see cref="SettleUpper"/>).
///
/// NULL and unusable coordinates: a record whose latitude or longitude is NULL, missing, not a number or out of range
/// is never a proximity match (it cannot be near a layer), but it does not stop the check. When the live map option is
/// on, such a record is still reported and only its map point is skipped (counted in
/// <see cref="MonitorCheckResult.MapSkippedRows"/>). A database or configuration error still fails the check.
/// </summary>
public sealed class MonitorCheckService(
    IDatabaseMetadataService metadata, IWatermarkReader watermarks, IReportWriter reports, ICoordinateLayerStore coordinateLayers)
    : IMonitorCheckService
{
    public const int PageSize = 1000;
    public const int MaxRows = 10_000;
    private const string NearestPointColumn = "Nearest point name";
    private const string CoordinateLayerColumn = "Coordinate layer";
    private const string DistanceColumn = "Distance (m)";
    private const int MaxDetailColumns = 8;
    private const int MaxDetailValueLength = 240;
    private const int MaxNameLength = 256;

    public async Task<MonitorCheckResult> CheckAsync(ConnectionSettings settings, MonitorDefinition monitor, CancellationToken ct)
    {
        var checkedAt = DateTime.UtcNow;
        var layers = await LoadProximityLayersAsync(monitor, ct);
        var columns = await metadata.GetColumnsAsync(settings, monitor.Database, monitor.Schema, monitor.Table, ct);

        var watermark = columns.FirstOrDefault(c => c.Name == monitor.WatermarkColumn)
                        ?? throw new ArgumentException($"Column '{monitor.WatermarkColumn}' no longer exists in {monitor.TargetText}.");
        if (!WatermarkTypes.IsSupported(watermark.TypeName))
            throw new NotSupportedException($"Column '{watermark.Name}' ({watermark.TypeName}) cannot be used to detect new records.");

        // Only supported types are read (no blobs / xml), always by explicit name: never SELECT *.
        var selected = columns.Where(c => c.IsFilterable).Select(c => c.Name).ToList();

        // The live map needs coordinates on the matching rows, and the same columns feed the proximity rule.
        var liveMap = monitor.LiveMapEnabled && monitor.HasCoordinateColumns;
        // Display-only: with a radius, the map may also show records that conditions match but the radius excludes.
        var liveAll = liveMap && monitor.LiveMapShowAllMatches && layers.Count > 0;
        var needCoordinates = layers.Count > 0 || liveMap;
        var latitudeIndex = -1;
        var longitudeIndex = -1;
        if (needCoordinates)
        {
            latitudeIndex = AddCoordinateColumn(monitor.LatitudeColumn!, columns, selected);
            longitudeIndex = AddCoordinateColumn(monitor.LongitudeColumn!, columns, selected);
        }
        if (selected.Count == 0) throw new ArgumentException("The table has no readable columns.");

        // Identity of a record on the map: its primary key values; without a usable key, every selected value.
        var keyIndexes = liveMap ? KeyIndexes(columns, selected) : [];

        var reading = await watermarks.ReadAsync(settings, monitor.Database, monitor.Schema, monitor.Table, watermark.Name, ct);
        var upper = SettleUpper(reading, watermark.TypeName, monitor.SettleSeconds);

        // First successful check: remember where we are, do not alert for existing data.
        if (monitor.LastWatermark is null && monitor.LastCheckedUtc is null)
            return new MonitorCheckResult(monitor.Id, checkedAt, selected, [], false, upper, null, true);

        var nothingNew = upper is null
                         || (monitor.LastWatermark is not null
                             && WatermarkComparer.Compare(upper, monitor.LastWatermark, watermark.TypeName) <= 0);
        if (nothingNew)
            return new MonitorCheckResult(monitor.Id, checkedAt, selected, [], false, monitor.LastWatermark, null, false);

        // (last, upper]. No lower bound only when the table was empty at the first check.
        var required = new List<FilterCondition>();
        if (monitor.LastWatermark is not null)
            required.Add(new FilterCondition(watermark.Name, FilterOperator.GreaterThan, monitor.LastWatermark));
        required.Add(new FilterCondition(watermark.Name, FilterOperator.LessOrEqual, upper));

        var order = new List<string> { watermark.Name };
        order.AddRange(columns.Where(c => c.IsPrimaryKey && c.IsFilterable && c.Name != watermark.Name)
            .OrderBy(c => c.KeyOrdinal).Select(c => c.Name));

        var rows = new List<object?[]>();
        var mapPoints = new List<LiveMapPoint>();
        var mapSkipped = 0;
        var truncated = false;
        var stopPaging = false;
        var watermarkIndex = selected.IndexOf(watermark.Name);
        var capReached = false;
        string? capWatermark = null;
        for (long offset = 0; ; offset += PageSize)
        {
            ct.ThrowIfCancellationRequested();
            var request = new PageRequest(
                monitor.Database, monitor.Schema, monitor.Table, selected, monitor.Conditions, monitor.Logic,
                order, false, PageSize, offset, null, required);
            var page = await metadata.GetPageAsync(settings, request, ct);

            for (var i = 0; i < page.Rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = page.Rows[i];
                var rowNumber = offset + i + 1;
                var candidateWatermark = candidate[watermarkIndex] is null or DBNull
                    ? throw new InvalidOperationException($"Monitor '{monitor.Name}' found a candidate without a watermark at row {rowNumber}.")
                    : WatermarkTypes.Format(candidate[watermarkIndex]!);

                if (capReached)
                {
                    var comparison = WatermarkComparer.Compare(candidateWatermark, capWatermark!, watermark.TypeName);
                    if (comparison > 0)
                    {
                        // Resume after a complete watermark group; a strict > lower bound must not split ties.
                        truncated = true;
                        stopPaging = true;
                        break;
                    }
                    if (comparison < 0)
                        throw new InvalidOperationException($"Monitor '{monitor.Name}' received rows out of watermark order while checking a capped result.");
                }

                // Read once per row. A row without usable coordinates is simply not located; it never ends the check.
                // Declared before the call: out variables inside '&&' are not definitely assigned for the code below.
                double latitude = 0, longitude = 0;
                var located = needCoordinates
                              && TryReadCoordinates(candidate, latitudeIndex, longitudeIndex, out latitude, out longitude);

                object?[]? matched = null;
                if (layers.Count == 0)
                {
                    matched = candidate;
                }
                else if (located)
                {
                    // The nearest point is taken across every selected layer, so one row matches once
                    // (a record close to two layers is reported with the closest of them).
                    var nearest = FindNearest(layers, latitude, longitude, ct);
                    if (nearest.DistanceMeters <= monitor.DistanceMeters!.Value)
                    {
                        matched = new object?[candidate.Length + 3];
                        Array.Copy(candidate, matched, candidate.Length);
                        matched[^3] = nearest.Point.Name;
                        matched[^2] = nearest.LayerName;
                        matched[^1] = nearest.DistanceMeters;
                    }
                }

                if (matched is null)
                {
                    // Not a report row. It may still be a map point when the user asked for all matching records.
                    if (liveAll && located)
                        mapPoints.Add(BuildLivePoint(candidate, selected, keyIndexes, latitude, longitude));
                    continue;
                }
                if (capReached)
                {
                    throw new InvalidOperationException($"Monitor '{monitor.Name}' matched more than {MaxRows:N0} rows with the same watermark value. It cannot safely resume without skipping records; choose a watermark column with fewer ties or split the monitor conditions.");
                }

                rows.Add(matched);
                if (liveMap)
                {
                    if (located) mapPoints.Add(BuildLivePoint(candidate, selected, keyIndexes, latitude, longitude));
                    else mapSkipped++;
                }

                if (rows.Count == MaxRows)
                {
                    capReached = true;
                    capWatermark = candidateWatermark;
                }
            }

            if (stopPaging || !page.HasMore) break;
        }

        var next = truncated ? capWatermark : upper;

        var reportColumns = layers.Count == 0
            ? selected
            : selected.Concat([NearestPointColumn, CoordinateLayerColumn, DistanceColumn]).ToList();
        string? reportPath = null;
        if (rows.Count > 0)
            reportPath = await reports.WriteAsync(monitor.Name, checkedAt, reportColumns, rows, monitor.OutputFolder, ct);

        return new MonitorCheckResult(monitor.Id, checkedAt, reportColumns, rows, truncated, next, reportPath, false,
            liveMap ? mapPoints : null, mapSkipped);
    }

    /// <summary>
    /// Every layer the proximity rule watches, in the order the user selected them. The rule is independent of the
    /// user conditions: it can be the only rule of a monitor (see <see cref="MonitorDefinition.HasEffectiveRule"/>).
    /// </summary>
    private async Task<IReadOnlyList<MapLayer>> LoadProximityLayersAsync(MonitorDefinition monitor, CancellationToken ct)
    {
        var ids = monitor.SelectedCoordinateLayerIds;
        if (ids.Count == 0 && !monitor.HasProximityRule) return [];
        if (ids.Count == 0)
            throw new InvalidOperationException($"Monitor '{monitor.Name}' has a proximity rule without a saved coordinate layer. Reopen it and select at least one layer.");

        if (!monitor.HasCoordinateColumns
            || monitor.DistanceMeters is not { } distance || !double.IsFinite(distance) || distance <= 0
            || distance > CoordinateRules.MaxRadiusMeters)
            throw new InvalidOperationException($"Monitor '{monitor.Name}' has an incomplete or invalid proximity rule. Reopen it and select distinct numeric coordinate columns, and a distance greater than 0 and no greater than {CoordinateRules.MaxRadiusMeters} metres.");

        var all = await coordinateLayers.LoadAllAsync(ct);
        var layers = new List<MapLayer>(ids.Count);
        foreach (var layerId in ids)
        {
            var layer = all.FirstOrDefault(item => item.Id == layerId);
            if (layer is null)
                throw new InvalidOperationException($"Monitor '{monitor.Name}' refers to a missing or unreadable imported coordinate layer ({layerId}). Choose an available layer in the monitor editor.");
            if (layer.Points.Count == 0)
                throw new InvalidOperationException($"The selected coordinate layer '{layer.Name}' contains no points and cannot be used by monitor '{monitor.Name}'.");
            if (layer.Points.Any(point => !CoordinateRules.IsValidLatitude(point.Latitude) || !CoordinateRules.IsValidLongitude(point.Longitude)))
                throw new InvalidOperationException($"The selected coordinate layer '{layer.Name}' contains invalid WGS84 coordinates.");
            layers.Add(layer);
        }
        return layers;
    }

    /// <summary>A coordinate column that does not exist (or has a non-numeric type) is a configuration error and fails the check.</summary>
    private static int AddCoordinateColumn(string name, IReadOnlyList<ColumnInfo> columns, List<string> selected)
    {
        var column = columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (column is null || !MonitorCoordinateTypes.IsSupported(column.TypeName))
            throw new InvalidOperationException($"Monitor coordinate column '{name}' is missing or cannot contain numeric coordinate values.");

        var index = selected.FindIndex(c => string.Equals(c, column.Name, StringComparison.Ordinal));
        if (index < 0)
        {
            selected.Add(column.Name);
            index = selected.Count - 1;
        }
        return index;
    }

    /// <summary>Positions (in <paramref name="selected"/>) of the primary key columns, or every column when the table has none.</summary>
    private static int[] KeyIndexes(IReadOnlyList<ColumnInfo> columns, List<string> selected)
    {
        var keyNames = columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.KeyOrdinal).Select(c => c.Name).ToList();
        var indexes = keyNames
            .Select(name => selected.IndexOf(name))
            .Where(index => index >= 0)
            .ToArray();
        return indexes.Length > 0 ? indexes : Enumerable.Range(0, selected.Count).ToArray();
    }

    /// <summary>Reads both coordinates of a row. False (never an exception) when either one is NULL, unusable or out of range.</summary>
    private static bool TryReadCoordinates(object?[] row, int latitudeIndex, int longitudeIndex, out double latitude, out double longitude)
    {
        // Both are always parsed (no short-circuit), so both out parameters are assigned on every path.
        var latitudeOk = TryParseCoordinate(row[latitudeIndex], isLatitude: true, out latitude);
        var longitudeOk = TryParseCoordinate(row[longitudeIndex], isLatitude: false, out longitude);
        return latitudeOk && longitudeOk;
    }

    private static bool TryParseCoordinate(object? value, bool isLatitude, out double parsed)
    {
        parsed = 0;
        if (value is null or DBNull) return false;

        try
        {
            parsed = value is string text
                ? double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)
                : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return false; // not a number: a data problem of this row, not of the check
        }

        return isLatitude ? CoordinateRules.IsValidLatitude(parsed) : CoordinateRules.IsValidLongitude(parsed);
    }

    private static LiveMapPoint BuildLivePoint(object?[] row, IReadOnlyList<string> selected, int[] keyIndexes, double latitude, double longitude)
    {
        var key = string.Join('\u001f', keyIndexes.Select(index => FormatPart(row[index])));

        var name = row.Select(value => value as string).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))?.Trim();
        if (name is null) name = key;
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];

        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < selected.Count && details.Count < MaxDetailColumns; i++)
        {
            var text = FormatPart(row[i]);
            if (text.Length > MaxDetailValueLength) text = text[..MaxDetailValueLength];
            details[selected[i]] = text;
        }

        return new LiveMapPoint(key, name, latitude, longitude, details);
    }

    private static string FormatPart(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        DateTime date => WatermarkTypes.Format(date),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    private static (MapPoint Point, string LayerName, double DistanceMeters) FindNearest(
        IReadOnlyList<MapLayer> layers, double latitude, double longitude, CancellationToken ct)
    {
        MapPoint? nearest = null;
        var nearestLayer = "";
        var distance = double.PositiveInfinity;
        foreach (var layer in layers)
        {
            for (var i = 0; i < layer.Points.Count; i++)
            {
                if ((i & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                var point = layer.Points[i];
                var candidate = MonitorProximity.HaversineMeters(latitude, longitude, point.Latitude, point.Longitude);
                if (candidate >= distance) continue;
                nearest = point;
                nearestLayer = layer.Name;
                distance = candidate;
            }
        }
        return (nearest!, nearestLayer, distance);
    }

    /// <summary>
    /// Upper end of the checked window. A row with a slightly older date can be committed a moment after a newer one
    /// (parallel transactions); if the window ended exactly at MAX such a row would be skipped forever. So for date
    /// columns the end is min(MAX, server time - settle). It can only delay a row (never lose it): an idle table is
    /// still reported once the settle time has passed, and a column in another time zone just waits longer.
    /// Number columns (identity) have no clock, so they are used as they are.
    /// </summary>
    public static string? SettleUpper(WatermarkReading reading, string typeName, int settleSeconds)
    {
        if (reading.Max is null || settleSeconds <= 0 || reading.ServerNow is not { } now || !WatermarkTypes.IsDate(typeName))
            return reading.Max;

        var limit = WatermarkTypes.Format(now.AddSeconds(-settleSeconds));
        return WatermarkComparer.Compare(reading.Max, limit, typeName) > 0 ? limit : reading.Max;
    }
}
