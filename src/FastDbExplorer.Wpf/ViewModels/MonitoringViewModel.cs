using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDbExplorer.Application.Abstractions;
using FastDbExplorer.Domain;
using FastDbExplorer.Infrastructure.Monitoring;
using FastDbExplorer.Wpf.Localization;
using FastDbExplorer.Wpf.Services;

namespace FastDbExplorer.Wpf.ViewModels;

public sealed record AlertItem(
    string MonitorName,
    string TimeText,
    string Summary,
    string? ReportPath,
    MonitorCheckResult? Result = null,
    MonitorDefinition? Definition = null,
    int? TotalRowCount = null,
    bool PreviewLimited = false)
{
    public bool HasReport => ReportPath is not null;
    public bool HasResult => Result is { Rows.Count: > 0 };

    public void Deconstruct(out string monitorName, out string timeText, out string summary, out string? reportPath)
    {
        monitorName = MonitorName;
        timeText = TimeText;
        summary = Summary;
        reportPath = ReportPath;
    }
}

/// <summary>Display text is preformatted, while Values keeps the original database types for coordinates and map details.</summary>
public sealed class MonitoringResultRow(object?[] values)
{
    public IReadOnlyList<object?> Values { get; } = values;
    public IReadOnlyList<string> Cells { get; } = values.Select(FormatValue).ToArray();

    private static string FormatValue(object? value) => value switch
    {
        null or DBNull => MonitoringStrings.NullValue,
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
        byte[] bytes => $"({bytes.Length:N0} bytes)",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };
}

public sealed partial class MonitoringMapDetailColumnItem(
    string name, bool isSelected, Action<MonitoringMapDetailColumnItem, bool> onChanged) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private bool _isSelected = isSelected;

    partial void OnIsSelectedChanged(bool value) => onChanged(this, value);
}

/// <summary>
/// Monitor list + alert history + the in-process scheduler for one connected session. It lives as long as the
/// connection (the password stays in memory only) and keeps checking while the user works in other screens.
/// </summary>
public sealed partial class MonitoringViewModel : ObservableObject, IDisposable
{
    private const int MaxAlerts = 100;
    private const int MaxAlertPreviewRows = 50;
    private const int MaxAlertPreviewCellLength = 256;
    private const int MaxAlertPreviewTextCharacters = 16 * 1024;
    private const int MaxAlertPreviewBinaryBytes = 16 * 1024;
    private const int RepeatFailureAlertEvery = 10;
    private const int MaxMapDetailColumns = 8;
    private const int MaxMapDetailValueLength = 240;
    private const int MaxMapDetailTotalLength = 1_200;

    private readonly IDatabaseMetadataService _metadata;
    private readonly IMonitorStore _store;
    private readonly IMonitorCheckService _checks;
    private readonly ICoordinateLayerStore _coordinateLayers;
    private readonly IFolderPickerService _folders;
    private readonly IAlertNotifier _notifier;
    private readonly ConnectionSettings _settings;
    private readonly IReadOnlyList<DatabaseInfo> _databases;
    private readonly List<MonitorDefinition> _otherServers = []; // monitors of other servers are kept untouched in the file
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private volatile IReadOnlyList<MonitorDefinition> _snapshot = []; // what the scheduler thread reads
    private MonitorScheduler? _scheduler;
    private IReadOnlyList<MonitoringResultRow> _selectedResultRows = [];

    public MonitoringViewModel(
        IDatabaseMetadataService metadata, IMonitorStore store, IMonitorCheckService checks, IFolderPickerService folders,
        IAlertNotifier notifier, ICoordinateLayerStore coordinateLayers, ConnectionSettings settings, IReadOnlyList<DatabaseInfo> databases)
    {
        _metadata = metadata;
        _store = store;
        _checks = checks;
        _coordinateLayers = coordinateLayers;
        _folders = folders;
        _notifier = notifier;
        _settings = settings;
        _databases = databases;
        Monitors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoMonitors));
        Alerts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoAlerts));
    }

    public event Action? BackRequested;
    public event Action<string, IReadOnlyList<QueryMapPoint>>? ShowSelectedRowsOnMapRequested;

    /// <summary>Matching records of a live-map monitor (monitor id, monitor name, display minutes, points) for the map.</summary>
    public event Action<Guid, string, int, IReadOnlyList<LiveMapPoint>>? LiveMapPointsRequested;

    /// <summary>The monitor's live layer must leave the map (monitor deleted, disabled, or live map turned off).</summary>
    public event Action<Guid>? LiveMapRemoveRequested;

    public ObservableCollection<MonitorItemViewModel> Monitors { get; } = [];
    public ObservableCollection<AlertItem> Alerts { get; } = [];
    public ObservableCollection<MonitoringMapDetailColumnItem> MapDetailColumns { get; } = [];
    public bool HasNoMonitors => Monitors.Count == 0;
    public bool HasNoAlerts => Alerts.Count == 0;
    public int MapDetailColumnCount => MapDetailColumns.Count(column => column.IsSelected);
    public bool IsMapSendEnabled => HasValidSelectedCoordinates();

    [ObservableProperty] private MonitorEditorViewModel? _editor;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewTarget = "";
    [ObservableProperty] private string _previewRowCountText = "";
    [ObservableProperty] private string _previewStatus = "";
    [ObservableProperty] private IReadOnlyList<string> _previewColumns = [];
    [ObservableProperty] private IReadOnlyList<MonitoringResultRow> _previewRows = [];
    [ObservableProperty] private string? _latitudeColumn;
    [ObservableProperty] private string? _longitudeColumn;
    [ObservableProperty] private bool _isResultPreviewVisible;

    partial void OnLatitudeColumnChanged(string? value) => RefreshMapCommandState();
    partial void OnLongitudeColumnChanged(string? value) => RefreshMapCommandState();

    public async Task InitializeAsync()
    {
        try
        {
            var all = await _store.LoadAsync(_lifetime.Token);
            foreach (var monitor in all)
            {
                if (string.Equals(monitor.Server, _settings.Server, StringComparison.OrdinalIgnoreCase))
                    Monitors.Add(new MonitorItemViewModel(monitor));
                else
                    _otherServers.Add(monitor);
            }
            RefreshSnapshot();

            // Created here (UI thread) so its events come back on the UI thread.
            _scheduler = new MonitorScheduler(_checks, _settings, () => _snapshot);
            _scheduler.Started += OnStarted;
            _scheduler.Completed += OnCompleted;
            _scheduler.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorMessage = ex.Message; } // UI boundary
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void NewMonitor() => OpenEditor(null);

    [RelayCommand]
    private void EditMonitor(MonitorItemViewModel item) => OpenEditor(item.Definition);

    [RelayCommand]
    private void DeleteMonitor(MonitorItemViewModel item)
    {
        Monitors.Remove(item);
        LiveMapRemoveRequested?.Invoke(item.Definition.Id);
        RefreshSnapshot();
        _ = SaveAsync();
    }

    [RelayCommand]
    private void ToggleEnabled(MonitorItemViewModel item)
    {
        item.Definition = item.Definition with { Enabled = !item.Definition.Enabled };
        if (!item.Definition.Enabled) LiveMapRemoveRequested?.Invoke(item.Definition.Id);
        RefreshSnapshot();
        _ = SaveAsync();
    }

    [RelayCommand]
    private Task CheckNowAsync(MonitorItemViewModel item) =>
        _scheduler?.CheckNowAsync(item.Definition.Id) ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenReport(AlertItem alert)
    {
        if (alert.ReportPath is null) return;
        var error = ShellOpen.Open(alert.ReportPath);
        ErrorMessage = error is null ? "" : MonitoringStrings.ErrOpenFile + error;
    }

    [RelayCommand]
    private void ShowReportInFolder(AlertItem alert)
    {
        if (alert.ReportPath is null) return;
        var error = ShellOpen.Reveal(alert.ReportPath);
        ErrorMessage = error is null ? "" : MonitoringStrings.ErrOpenFile + error;
    }

    [RelayCommand]
    private void ShowResults(AlertItem alert)
    {
        if (alert.Result is not { Rows.Count: > 0 } result) return;

        _selectedResultRows = [];
        PreviewTitle = alert.MonitorName;
        PreviewTarget = alert.Definition is { } definition
            ? $"{definition.Database}.{definition.Schema}.{definition.Table}"
            : alert.MonitorName;
        PreviewColumns = result.Columns.ToList();
        PreviewRowCountText = MonitoringStrings.PreviewRowCount(alert.TotalRowCount ?? result.Rows.Count);
        PreviewStatus = result.Truncated
            ? MonitoringStrings.PreviewTruncated
            : alert.PreviewLimited ? MonitoringStrings.PreviewLimited : "";
        PreviewRows = result.Rows.Select(row => new MonitoringResultRow(row)).ToList();

        LatitudeColumn = FindCoordinateColumn(PreviewColumns, alert.Definition?.LatitudeColumn, isLatitude: true);
        LongitudeColumn = FindCoordinateColumn(PreviewColumns, alert.Definition?.LongitudeColumn, isLatitude: false);
        ConfigureMapDetailColumns();
        RefreshMapCommandState();
        IsResultPreviewVisible = true;
    }

    [RelayCommand]
    private void CloseResults()
    {
        IsResultPreviewVisible = false;
        _selectedResultRows = [];
        PreviewRows = [];
        PreviewColumns = [];
        MapDetailColumns.Clear();
        OnPropertyChanged(nameof(MapDetailColumnCount));
        PreviewTitle = "";
        PreviewTarget = "";
        PreviewRowCountText = "";
        PreviewStatus = "";
        LatitudeColumn = null;
        LongitudeColumn = null;
        RefreshMapCommandState();
    }

    public void SetSelectedResultRows(IReadOnlyList<MonitoringResultRow> rows)
    {
        _selectedResultRows = rows;
        RefreshMapCommandState();
    }

    [RelayCommand(CanExecute = nameof(CanSendSelectedRowsToMap))]
    private void SendSelectedRowsToMap()
    {
        if (!TryGetCoordinateIndexes(out var latitudeIndex, out var longitudeIndex)) return;

        var points = new List<QueryMapPoint>(_selectedResultRows.Count);
        var skipped = 0;
        foreach (var (row, rowIndex) in _selectedResultRows.Select((row, index) => (row, index)))
        {
            if (!TryGetCoordinates(row, latitudeIndex, longitudeIndex, out var latitude, out var longitude))
            {
                skipped++;
                continue;
            }

            var name = GetPointName(row, latitudeIndex, longitudeIndex, rowIndex);
            points.Add(new QueryMapPoint(name, latitude, longitude, BuildMapDetails(row)));
        }

        if (points.Count == 0)
        {
            PreviewStatus = MonitoringStrings.NoValidSelectedCoordinates;
            return;
        }

        PreviewStatus = skipped > 0
            ? MonitoringStrings.RowsSkippedForMap(skipped, points.Count)
            : MonitoringStrings.RowsSentToMap(points.Count);
        ShowSelectedRowsOnMapRequested?.Invoke(PreviewTarget, points);
    }

    private void OpenEditor(MonitorDefinition? existing)
    {
        if (IsResultPreviewVisible) CloseResults();
        Editor?.Dispose();
        var editor = new MonitorEditorViewModel(_metadata, _settings, _databases, _folders, existing, _coordinateLayers);
        editor.Saved += OnEditorSaved;
        editor.Cancelled += CloseEditor;
        Editor = editor;
        _ = editor.InitializeAsync();
    }

    private void CloseEditor()
    {
        Editor?.Dispose();
        Editor = null;
    }

    private void OnEditorSaved(MonitorDefinition definition)
    {
        var item = Monitors.FirstOrDefault(m => m.Definition.Id == definition.Id);
        if (item is null)
        {
            Monitors.Add(new MonitorItemViewModel(definition));
        }
        else
        {
            // A check may have finished while the editor was open: keep the newest check state, take only the user's edits.
            item.Definition = definition with
            {
                LastWatermark = item.Definition.LastWatermark,
                LastCheckedUtc = item.Definition.LastCheckedUtc
            };
        }

        if (!definition.LiveMapEnabled || !definition.Enabled) LiveMapRemoveRequested?.Invoke(definition.Id);
        RefreshSnapshot();
        CloseEditor();
        _ = SaveAsync();
    }

    private void OnStarted(Guid id)
    {
        var item = Monitors.FirstOrDefault(m => m.Definition.Id == id);
        if (item is null) return;
        item.IsChecking = true;
        item.HasProblem = false;
        item.StatusText = MonitoringStrings.Checking;
    }

    private void OnCompleted(MonitorRunOutcome outcome)
    {
        var item = Monitors.FirstOrDefault(m => m.Definition.Id == outcome.Monitor.Id);
        if (item is null) return; // deleted while it was running
        item.IsChecking = false;

        if (outcome.Error is not null)
        {
            OnCheckFailed(item, outcome.Error);
            return;
        }

        item.ConsecutiveFailures = 0;
        var result = outcome.Result!;
        // Copy only the check state: the user may have edited the monitor while the check was running.
        item.Definition = item.Definition with
        {
            LastWatermark = outcome.Monitor.LastWatermark,
            LastCheckedUtc = outcome.Monitor.LastCheckedUtc
        };
        RefreshSnapshot();

        if (result.IsBaseline)
        {
            item.StatusText = MonitoringStrings.BaselineSet;
        }
        else if (result.Rows.Count == 0)
        {
            item.StatusText = MonitoringStrings.NoNewMatches;
        }
        else
        {
            item.StatusText = MonitoringStrings.Matched(result.Rows.Count);
            var summary = MonitoringStrings.AlertSummary(result.Rows.Count, result.Truncated);
            var previewResult = LimitAlertPreview(result, out var previewLimited);
            AddAlert(new AlertItem(item.Name, MonitoringStrings.TimeText(result.CheckedAtUtc), summary,
                result.ReportPath, previewResult, outcome.Monitor, result.Rows.Count, previewLimited));
            _notifier.Notify(MonitoringStrings.ToastTitle(item.Name), summary, result.ReportPath, item.Definition.PlaySound);
        }

        // Only records found by a real check go to the map; the baseline run just records the starting point.
        if (!result.IsBaseline && result.MapPoints is { Count: > 0 } livePoints
            && item.Definition.LiveMapEnabled && item.Definition.Enabled)
            LiveMapPointsRequested?.Invoke(item.Definition.Id, item.Name, item.Definition.LiveMapMinutes, livePoints);

        _ = SaveAsync();
    }

    /// <summary>
    /// A monitor that silently stops working is worse than none: tell the user on the first failure of a streak
    /// (server down, column dropped, folder not writable ...) and again every 10th failure, not on every interval.
    /// </summary>
    private void OnCheckFailed(MonitorItemViewModel item, Exception error)
    {
        item.HasProblem = true;
        item.ConsecutiveFailures++;
        var message = MonitoringStrings.CheckFailed(Describe(error));
        item.StatusText = message;

        if (item.ConsecutiveFailures == 1 || item.ConsecutiveFailures % RepeatFailureAlertEvery == 0)
        {
            AddAlert(new AlertItem(MonitoringStrings.ErrorPrefix + item.Name, MonitoringStrings.TimeText(DateTime.UtcNow), message, null));
            _notifier.Notify(MonitoringStrings.ToastErrorTitle(item.Name), message, null, item.Definition.PlaySound);
        }
    }

    private void AddAlert(AlertItem alert)
    {
        Alerts.Insert(0, alert);
        while (Alerts.Count > MaxAlerts) Alerts.RemoveAt(Alerts.Count - 1);
    }

    private static MonitorCheckResult LimitAlertPreview(MonitorCheckResult result, out bool limited)
    {
        var remainingText = MaxAlertPreviewTextCharacters;
        var remainingBinary = MaxAlertPreviewBinaryBytes;
        var previewLimited = result.Rows.Count > MaxAlertPreviewRows;
        var rows = new List<object?[]>(Math.Min(result.Rows.Count, MaxAlertPreviewRows));

        foreach (var row in result.Rows.Take(MaxAlertPreviewRows))
        {
            var copy = new object?[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                copy[i] = row[i] switch
                {
                    string text => LimitText(text, ref remainingText, ref previewLimited),
                    byte[] bytes => LimitBytes(bytes, ref remainingBinary, ref previewLimited),
                    var value => value
                };
            }
            rows.Add(copy);
        }

        limited = previewLimited;
        return result with { Rows = rows };
    }

    private static string LimitText(string text, ref int remaining, ref bool limited)
    {
        var length = Math.Min(text.Length, Math.Min(MaxAlertPreviewCellLength, remaining));
        remaining -= length;
        if (length == text.Length) return text;
        limited = true;
        return length == 0 ? "" : text[..length] + "…";
    }

    private static byte[] LimitBytes(byte[] bytes, ref int remaining, ref bool limited)
    {
        var length = Math.Min(bytes.Length, remaining);
        remaining -= length;
        if (length == bytes.Length) return bytes;
        limited = true;
        return bytes.AsSpan(0, length).ToArray();
    }

    private void ConfigureMapDetailColumns()
    {
        MapDetailColumns.Clear();
        var coordinateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (LatitudeColumn is not null) coordinateNames.Add(LatitudeColumn);
        if (LongitudeColumn is not null) coordinateNames.Add(LongitudeColumn);

        var columns = PreviewColumns.Select((name, index) => (name, index)).ToList();
        var defaults = columns.Where(item => !coordinateNames.Contains(item.name))
            .OrderByDescending(item => DetailUsefulness(item.name))
            .ThenBy(item => item.index).Take(2).Select(item => item.name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var column in columns)
            MapDetailColumns.Add(new MonitoringMapDetailColumnItem(
                column.name, defaults.Contains(column.name), OnMapDetailColumnChanged));
        OnPropertyChanged(nameof(MapDetailColumnCount));
    }

    private void OnMapDetailColumnChanged(MonitoringMapDetailColumnItem item, bool selected)
    {
        if (selected && MapDetailColumnCount > MaxMapDetailColumns)
        {
            item.IsSelected = false;
            PreviewStatus = MonitoringStrings.MaxMapDetailColumnsReached;
            return;
        }
        OnPropertyChanged(nameof(MapDetailColumnCount));
    }

    private void RefreshMapCommandState()
    {
        OnPropertyChanged(nameof(IsMapSendEnabled));
        SendSelectedRowsToMapCommand.NotifyCanExecuteChanged();
    }

    private bool CanSendSelectedRowsToMap() => IsMapSendEnabled;

    private bool HasValidSelectedCoordinates()
    {
        if (!TryGetCoordinateIndexes(out var latitudeIndex, out var longitudeIndex)) return false;
        return _selectedResultRows.Any(row => TryGetCoordinates(row, latitudeIndex, longitudeIndex, out _, out _));
    }

    private bool TryGetCoordinateIndexes(out int latitudeIndex, out int longitudeIndex)
    {
        latitudeIndex = FindColumnIndex(LatitudeColumn);
        longitudeIndex = FindColumnIndex(LongitudeColumn);
        return latitudeIndex >= 0 && longitudeIndex >= 0 && latitudeIndex != longitudeIndex;
    }

    private int FindColumnIndex(string? name)
    {
        if (name is null) return -1;
        for (var index = 0; index < PreviewColumns.Count; index++)
            if (string.Equals(PreviewColumns[index], name, StringComparison.Ordinal)) return index;
        return -1;
    }

    private static string? FindCoordinateColumn(IReadOnlyList<string> columns, string? preferred, bool isLatitude)
    {
        if (preferred is not null)
            foreach (var column in columns)
                if (string.Equals(column, preferred, StringComparison.OrdinalIgnoreCase)) return column;

        var ranked = columns.Select((name, index) => (name, index, score: CoordinateNameScore(name, isLatitude)))
            .Where(item => item.score > 0).OrderByDescending(item => item.score).ThenBy(item => item.index).FirstOrDefault();
        return ranked.score > 0 ? ranked.name : null;
    }

    private static int CoordinateNameScore(string name, bool isLatitude)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (isLatitude)
        {
            if (normalized is "lat" or "latitude" or "y") return 5;
            if (normalized.Contains("latitude", StringComparison.Ordinal)) return 4;
            if (normalized.StartsWith("lat", StringComparison.Ordinal) || normalized.EndsWith("lat", StringComparison.Ordinal)) return 2;
        }
        else
        {
            if (normalized is "lon" or "lng" or "long" or "longitude" or "x") return 5;
            if (normalized.Contains("longitude", StringComparison.Ordinal)) return 4;
            if (normalized.StartsWith("lon", StringComparison.Ordinal) || normalized.EndsWith("lon", StringComparison.Ordinal)
                || normalized.StartsWith("lng", StringComparison.Ordinal) || normalized.EndsWith("lng", StringComparison.Ordinal)) return 2;
        }
        return 0;
    }

    private static int DetailUsefulness(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (normalized.Contains("name", StringComparison.Ordinal) || normalized.Contains("title", StringComparison.Ordinal)
            || normalized.Contains("address", StringComparison.Ordinal) || normalized.Contains("nearestpoint", StringComparison.Ordinal)) return 4;
        if (normalized.Contains("distance", StringComparison.Ordinal) || normalized.Contains("description", StringComparison.Ordinal)
            || normalized.Contains("type", StringComparison.Ordinal)) return 3;
        if (normalized.Contains("layer", StringComparison.Ordinal) || normalized.Contains("category", StringComparison.Ordinal)
            || normalized.Contains("status", StringComparison.Ordinal)) return 2;
        return 0;
    }

    private static bool TryGetCoordinates(
        MonitoringResultRow row, int latitudeIndex, int longitudeIndex, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (latitudeIndex >= row.Values.Count || longitudeIndex >= row.Values.Count
            || !TryCoordinate(row.Values[latitudeIndex], out latitude)
            || !TryCoordinate(row.Values[longitudeIndex], out longitude)) return false;
        return CoordinateRules.IsValidLatitude(latitude) && CoordinateRules.IsValidLongitude(longitude);
    }

    private static bool TryCoordinate(object? value, out double coordinate)
    {
        coordinate = 0;
        if (value is null or DBNull or bool or DateTime or DateTimeOffset or DateOnly or TimeOnly) return false;
        try
        {
            if (value is string or char)
            {
                var text = Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim();
                text = string.Concat(text.Select(character =>
                {
                    var digit = CharUnicodeInfo.GetDecimalDigitValue(character);
                    if (digit is >= 0 and <= 9) return (char)('0' + digit);
                    return character == '\u066B' ? '.' : character;
                }));
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate)
                       && double.IsFinite(coordinate);
            }
            if (value is not IConvertible) return false;
            coordinate = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(coordinate);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return false;
        }
    }

    private string GetPointName(MonitoringResultRow row, int latitudeIndex, int longitudeIndex, int rowIndex)
    {
        for (var index = 0; index < row.Values.Count; index++)
        {
            if (index == latitudeIndex || index == longitudeIndex || row.Values[index] is null or DBNull) continue;
            var value = row.Cells[index];
            if (!string.IsNullOrWhiteSpace(value)) return TruncateMapText(value, 100);
        }
        return $"Row {rowIndex + 1}";
    }

    private IReadOnlyDictionary<string, string>? BuildMapDetails(MonitoringResultRow row)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalLength = 0;
        foreach (var column in MapDetailColumns.Where(item => item.IsSelected).Take(MaxMapDetailColumns))
        {
            var index = FindColumnIndex(column.Name);
            if (index < 0 || index >= row.Cells.Count) continue;
            var key = TruncateMapText(column.Name, 80);
            var remaining = MaxMapDetailTotalLength - totalLength - key.Length;
            if (remaining <= 0) break;
            var value = TruncateMapText(row.Cells[index], Math.Min(MaxMapDetailValueLength, remaining));
            details[key] = value;
            totalLength += key.Length + value.Length;
        }
        return details.Count == 0 ? null : details;
    }

    private static string TruncateMapText(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        var length = Math.Max(0, maxLength - 1);
        if (length > 0 && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
        return value[..length] + "…";
    }

    private void RefreshSnapshot() => _snapshot = Monitors.Select(m => m.Definition).ToList();

    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            var all = _otherServers.Concat(Monitors.Select(m => m.Definition)).ToList();
            await _store.SaveAsync(all);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; } // UI boundary
        finally { _saveGate.Release(); }
    }

    private static string Describe(Exception ex) => ex switch
    {
        DatabaseAccessException db => Strings.Describe(db),
        ArgumentException or FormatException or NotSupportedException => Strings.ErrInvalidQuery + ex.Message,
        _ => ex.Message
    };

    public void Dispose()
    {
        _lifetime.Cancel();
        _scheduler?.Dispose();
        Editor?.Dispose();
    }
}
