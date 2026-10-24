using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDbExplorer.Application.Abstractions;
using FastDbExplorer.Domain;
using FastDbExplorer.Wpf.Localization;
using FastDbExplorer.Wpf.Services;

namespace FastDbExplorer.Wpf.ViewModels;

/// <summary>One cell keeps its raw value for export and a readable value for the grid.</summary>
public sealed record GridCell(string Display, string FullValue, object? RawValue, bool IsTruncated);

/// <summary>The grid binds to each row by index: [0], [1], ...; map-only values stay out of the grid/export.</summary>
public sealed class GridRow(GridCell[] cells, IReadOnlyDictionary<string, object?>? mapValues = null)
{
    public GridCell this[int index] => cells[index];
    public IReadOnlyList<GridCell> Cells => cells;
    public IReadOnlyDictionary<string, object?> MapValues { get; private set; } = mapValues ?? new Dictionary<string, object?>();

    internal void UpdateMapValues(IReadOnlyDictionary<string, object?> values) => MapValues = values;
}

public sealed record QueryMapPoint(
    string Name,
    double Latitude,
    double Longitude,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed partial class MapDetailColumnItem(string name, bool isSelected, Action<MapDetailColumnItem, bool> onChanged)
    : ObservableObject
{
    public string Name => name;

    [ObservableProperty] private bool _isSelected = isSelected;

    partial void OnIsSelectedChanged(bool value) => onChanged(this, value);
}

public sealed partial class ColumnItem(ColumnInfo info) : ObservableObject
{
    public string Name => info.Name;
    public string TypeName => info.TypeName;
    public bool IsKey => info.IsPrimaryKey;

    [ObservableProperty] private bool _isSelected;
}

public sealed partial class TableQueryViewModel : ObservableObject
{
    private const long KeysetThreshold = 10_000;
    private const int DefaultSelectedColumns = 20;
    private const int MaxMapDetailColumns = 8;
    private const int MaxMapDetailValueLength = 240;
    private const int MaxMapDetailTotalLength = 1_200;

    private sealed record PageCursor(long Offset, object?[]? AfterKey);

    private readonly IDatabaseMetadataService _metadata;
    private readonly ConnectionSettings _settings;
    private readonly IFileDialogService _dialogs;
    private readonly IQueryReportWriter _queryReportWriter;
    private readonly string _database;
    private readonly TableInfo _table;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stack<PageCursor> _history = new();
    private List<ColumnInfo> _keyColumns = [];
    private IReadOnlyList<ColumnInfo> _columnMeta = [];
    private IReadOnlyList<string> _displayColumns = [];
    private PageCursor _current = new(0, null);
    private PageRequest? _base;
    private PageResult? _lastResult;
    private CancellationTokenSource? _running;
    private long _exportedRows;
    private bool _suppressMapDetailChange;

    public TableQueryViewModel(
        IDatabaseMetadataService metadata, ConnectionSettings settings, string database, TableInfo table,
        IFileDialogService dialogs, IQueryReportWriter queryReportWriter)
    {
        _metadata = metadata;
        _settings = settings;
        _database = database;
        _table = table;
        _dialogs = dialogs;
        _queryReportWriter = queryReportWriter;
    }

    public event Action<IReadOnlyList<string>>? ResultColumnsChanged;
    public event Action<string, IReadOnlyList<QueryMapPoint>>? ShowSelectedRowsOnMapRequested;

    public string TableName => _table.FullName;
    public long ApproxRows => _table.ApproxRows;
    public ObservableCollection<ColumnItem> Columns { get; } = [];
    /// <summary>All table columns are available here, independently of which columns the result grid displays.</summary>
    public ObservableCollection<MapDetailColumnItem> MapDetailColumns { get; } = [];
    public ObservableCollection<FilterRowViewModel> Filters { get; } = [];
    public IReadOnlyList<OperatorOption> Operators => OperatorOption.All;
    public IReadOnlyList<int> PageSizes { get; } = [50, 100, 250, 500];
    public IReadOnlyList<string> ResultColumns { get; private set; } = [];
    /// <summary>Coordinate settings use all table columns, not only the visible result-grid columns.</summary>
    public IReadOnlyList<string> CoordinateColumns { get; private set; } = [];
    public int MapDetailColumnCount => MapDetailColumns.Count(column => column.IsSelected);
    public int SelectedRowCount => SelectedRows.Count;
    public bool NeedsOrderColumn => !UsesPrimaryKey;

    [ObservableProperty] private IReadOnlyList<string> _filterableColumns = [];
    [ObservableProperty] private IReadOnlyList<GridRow> _rows = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRowCount))]
    [NotifyCanExecuteChangedFor(nameof(ShowSelectedOnMapCommand))]
    private IReadOnlyList<GridRow> _selectedRows = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(MatchAll))] private bool _useOr;

    public bool MatchAll { get => !UseOr; set => UseOr = !value; }

    [ObservableProperty] private int _pageSize = 100;
    [ObservableProperty] private string? _orderColumn;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ShowSelectedOnMapCommand))][NotifyCanExecuteChangedFor(nameof(ShowAllOnMapCommand))] private string? _latitudeColumn;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ShowSelectedOnMapCommand))][NotifyCanExecuteChangedFor(nameof(ShowAllOnMapCommand))] private string? _longitudeColumn;
    [ObservableProperty] private string _keyDescription = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _pageInfo = "";
    [ObservableProperty] private string _resultCountText = "";
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ExportExcelCommand))][NotifyCanExecuteChangedFor(nameof(ShowAllOnMapCommand))] private bool _hasResult;
    [ObservableProperty] private bool _isConfigVisible = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(NeedsOrderColumn))] private bool _usesPrimaryKey;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _isLoadingColumns;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportExcelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSelectedOnMapCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowAllOnMapCommand))]
    private bool _isBusy;

    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(NextCommand))] private bool _hasNext;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(PreviousCommand))] private bool _hasPrevious;

    public async Task InitializeAsync()
    {
        IsLoadingColumns = true;
        try
        {
            var meta = await _metadata.GetColumnsAsync(_settings, _database, _table.Schema, _table.Name, _lifetime.Token);
            foreach (var column in meta)
                Columns.Add(new ColumnItem(column) { IsSelected = Columns.Count < DefaultSelectedColumns });

            _columnMeta = meta;
            _keyColumns = meta.Where(c => c.IsPrimaryKey).OrderBy(c => c.KeyOrdinal).ToList();
            UsesPrimaryKey = _keyColumns.Count > 0 && _keyColumns.All(c => c.IsFilterable);
            KeyDescription = string.Join(", ", _keyColumns.Select(c => c.Name));
            FilterableColumns = meta.Where(c => c.IsFilterable).Select(c => c.Name).ToList();
            CoordinateColumns = meta.Where(c => c.IsFilterable).Select(c => c.Name).ToList();
            OnPropertyChanged(nameof(CoordinateColumns));
            LatitudeColumn ??= DetectCoordinateColumn(true);
            LongitudeColumn ??= DetectCoordinateColumn(false);

            var displayName = Columns.FirstOrDefault(column => column.IsSelected)?.Name;
            var defaultDetails = meta.Where(column => column.IsFilterable).Select(column => column.Name)
                .Where(name => name != displayName && name != LatitudeColumn && name != LongitudeColumn)
                .Take(2)
                .ToHashSet(StringComparer.Ordinal);
            MapDetailColumns.Clear();
            foreach (var column in meta.Where(column => column.IsFilterable))
                MapDetailColumns.Add(new MapDetailColumnItem(column.Name, defaultDetails.Contains(column.Name), OnMapDetailColumnChanged));
            OnPropertyChanged(nameof(MapDetailColumnCount));
        }
        catch (OperationCanceledException) { }
        catch (DatabaseAccessException ex) { ErrorMessage = Strings.Describe(ex); }
        finally { IsLoadingColumns = false; }
    }

    public void Shutdown() => _lifetime.Cancel();

    private bool CanRun() => !IsBusy && !IsLoadingColumns && Columns.Count > 0;
    private bool CanGoNext() => HasNext && !IsBusy;
    private bool CanGoPrevious() => HasPrevious && !IsBusy;
    private bool CanExportExcel() => HasResult && _base is not null && !IsBusy;
    private bool CanShowSelectedOnMap() => !IsBusy && SelectedRows.Count > 0
        && LatitudeColumn is not null && LongitudeColumn is not null
        && LatitudeColumn != LongitudeColumn
        && CoordinateColumns.Contains(LatitudeColumn) && CoordinateColumns.Contains(LongitudeColumn);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (!TryBuildRequest(out var request, out var displayColumns)) return;
        var previousBase = _base;
        _base = request;
        var first = new PageCursor(0, null);
        var fetched = await FetchAsync(first);
        if (fetched is null)
        {
            _base = previousBase;
            ExportExcelCommand.NotifyCanExecuteChanged();
            return;
        }
        _displayColumns = displayColumns;
        _history.Clear();
        _current = first;
        Show(fetched.Value.Result, fetched.Value.Ms);
        StartCount();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        if (_base is null || _lastResult is null) return;
        var offset = _current.Offset + _base.PageSize;
        var useKey = _base.UseKeyset && _lastResult.LastKey is not null && offset >= KeysetThreshold;
        var next = new PageCursor(offset, useKey ? _lastResult.LastKey : null);
        var fetched = await FetchAsync(next);
        if (fetched is null) return;
        _history.Push(_current);
        _current = next;
        Show(fetched.Value.Result, fetched.Value.Ms);
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousAsync()
    {
        var previous = _history.Peek();
        var fetched = await FetchAsync(previous);
        if (fetched is null) return;
        _history.Pop();
        _current = previous;
        Show(fetched.Value.Result, fetched.Value.Ms);
    }

    [RelayCommand] private void Cancel() => _running?.Cancel();
    [RelayCommand] private void ToggleConfig() => IsConfigVisible = !IsConfigVisible;

    /// <summary>
    /// Saves every row of the query. A normal result becomes the .xlsx the user chose; a very big one is written as CSV, split
    /// into several files, so no single file gets heavy (the writer decides, see <see cref="ExportLimits"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportExcel))]
    private async Task ExportExcelAsync()
    {
        var path = _dialogs.PickExcelSaveFile();
        if (path is null || _base is null) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _running = cts;
        _exportedRows = 0;
        IsBusy = true;
        ErrorMessage = "";
        try
        {
            var result = await _queryReportWriter.WriteQueryAsync(_displayColumns, ReadExportRowsAsync(cts.Token), path, cts.Token);
            PageInfo = result.Format == QueryExportFormat.Xlsx
                ? string.Format(CultureInfo.CurrentCulture, Strings.ExportCompleted, result.Files[0], result.Rows)
                : ExportStrings.CsvCompleted(result);
        }
        catch (OperationCanceledException) { PageInfo = Strings.Cancelled; }
        catch (Exception ex) { ErrorMessage = Strings.ExportFailed + ex.Message; }
        finally
        {
            IsBusy = false;
            _running = null;
        }
    }

    private const int CountPageSize = 500;
    private const int MaxAllMapPoints = 10_000;
    private CancellationTokenSource? _countCts;

    /// <summary>
    /// Counts every row of the search in the background, page by page (no COUNT(*)), so the result line states the real total.
    /// A new search cancels the count of the previous one; the grid never waits for it.
    /// </summary>
    private void StartCount()
    {
        _countCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _countCts = cts;
        if (_base is null) return;
        _ = CountRowsAsync(_base with { Columns = _displayColumns }, cts.Token);
    }

    private async Task CountRowsAsync(PageRequest request, CancellationToken ct)
    {
        ResultCountText = Strings.ResultCounting(0);
        long total = 0;
        long offset = 0;
        object?[]? afterKey = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _metadata.GetPageAsync(_settings,
                    request with { PageSize = CountPageSize, Offset = offset, AfterKey = afterKey }, ct);
                total += page.Rows.Count;
                ResultCountText = page.HasMore ? Strings.ResultCounting(total) : Strings.ResultFound(total);
                if (!page.HasMore) return;
                offset += CountPageSize;
                afterKey = request.UseKeyset && offset >= KeysetThreshold ? page.LastKey : null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { ResultCountText = ""; } // the count is extra information: it never shows an error
    }

    private bool CanShowAllOnMap() => !IsBusy && HasResult && _base is not null
        && LatitudeColumn is not null && LongitudeColumn is not null
        && LatitudeColumn != LongitudeColumn;

    /// <summary>
    /// Sends every row of the search to the map (not only the rows of this page), up to <see cref="MaxAllMapPoints"/>.
    /// Rows are streamed page by page; only the coordinates, the name and the chosen detail columns are read.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowAllOnMap))]
    private async Task ShowAllOnMapAsync()
    {
        var latitudeColumn = LatitudeColumn;
        var longitudeColumn = LongitudeColumn;
        if (_base is null || latitudeColumn is null || longitudeColumn is null || latitudeColumn == longitudeColumn
            || !CoordinateColumns.Contains(latitudeColumn) || !CoordinateColumns.Contains(longitudeColumn))
        {
            ErrorMessage = Strings.ErrPickCoordinateColumns;
            return;
        }

        var nameColumn = _displayColumns.FirstOrDefault();
        var detailColumns = MapDetailColumns.Where(item => item.IsSelected)
            .Take(MaxMapDetailColumns).Select(item => item.Name).ToList();
        var columns = new List<string> { latitudeColumn, longitudeColumn };
        if (nameColumn is not null) columns.Add(nameColumn);
        columns.AddRange(detailColumns);
        columns = columns.Distinct(StringComparer.Ordinal).ToList();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _running = cts;
        IsBusy = true;
        ErrorMessage = "";
        var points = new List<QueryMapPoint>();
        var scanned = 0;
        var skipped = 0;
        var truncated = false;
        try
        {
            await foreach (var values in ReadMapValuesAsync(_base with { Columns = columns }, cts.Token))
            {
                scanned++;
                if (!TryBuildPoint(values, latitudeColumn, longitudeColumn, nameColumn, detailColumns, scanned, out var point))
                {
                    skipped++;
                    continue;
                }
                if (points.Count == MaxAllMapPoints) { truncated = true; break; }
                points.Add(point);
                if (scanned % 1000 == 0) PageInfo = Strings.MapSendingProgress(scanned);
            }

            if (points.Count == 0) { ErrorMessage = Strings.ErrNoValidCoordinates; return; }
            PageInfo = truncated ? Strings.MapSentLimited(points.Count) : Strings.MapSentAll(points.Count, skipped);
            ShowSelectedRowsOnMapRequested?.Invoke(TableName, points);
        }
        catch (OperationCanceledException) { PageInfo = Strings.Cancelled; }
        catch (Exception ex) { ErrorMessage = Strings.ErrSendingToMap + ex.Message; }
        finally
        {
            IsBusy = false;
            _running = null;
        }
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadMapValuesAsync(
        PageRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        const int pageSize = 500;
        long offset = 0;
        object?[]? afterKey = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _metadata.GetPageAsync(_settings,
                request with { PageSize = pageSize, Offset = offset, AfterKey = afterKey }, ct);
            foreach (var row in page.Rows)
            {
                ct.ThrowIfCancellationRequested();
                yield return ToMapValues(page.Columns, row);
            }
            if (!page.HasMore) yield break;
            offset += pageSize;
            afterKey = request.UseKeyset && offset >= KeysetThreshold ? page.LastKey : null;
        }
    }

    private bool TryBuildPoint(IReadOnlyDictionary<string, object?> values, string latitudeColumn, string longitudeColumn,
        string? nameColumn, IReadOnlyList<string> detailColumns, int rowNumber, out QueryMapPoint point)
    {
        point = null!;
        if (!values.TryGetValue(latitudeColumn, out var latitudeValue)
            || !values.TryGetValue(longitudeColumn, out var longitudeValue)
            || !TryCoordinate(latitudeValue, out var latitude)
            || !TryCoordinate(longitudeValue, out var longitude)
            || !CoordinateRules.IsValidLatitude(latitude) || !CoordinateRules.IsValidLongitude(longitude))
            return false;

        var name = nameColumn is not null && values.TryGetValue(nameColumn, out var nameValue)
            ? CreateCell(nameValue).Display
            : "";
        if (string.IsNullOrWhiteSpace(name)) name = $"Row {rowNumber}";
        point = new QueryMapPoint(TruncateMapText(name, 100), latitude, longitude, BuildMapDetails(values));
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanShowSelectedOnMap))]
    private async Task ShowSelectedOnMapAsync()
    {
        if (SelectedRows.Count == 0) { ErrorMessage = Strings.ErrSelectRows; return; }
        var latitudeColumn = LatitudeColumn;
        var longitudeColumn = LongitudeColumn;
        if (latitudeColumn is null || longitudeColumn is null) { ErrorMessage = Strings.ErrPickCoordinateColumns; return; }
        if (latitudeColumn == longitudeColumn
            || !CoordinateColumns.Contains(latitudeColumn) || !CoordinateColumns.Contains(longitudeColumn))
        {
            ErrorMessage = Strings.ErrPickCoordinateColumns;
            return;
        }
        if (!await EnsureMapValuesAvailableAsync()) return;

        var points = new List<QueryMapPoint>(SelectedRows.Count);
        foreach (var (row, index) in SelectedRows.Select((row, index) => (row, index)))
        {
            if (!row.MapValues.TryGetValue(latitudeColumn, out var latitudeValue)
                || !row.MapValues.TryGetValue(longitudeColumn, out var longitudeValue)
                || !TryCoordinate(latitudeValue, out var latitude)
                || !TryCoordinate(longitudeValue, out var longitude)
                || !CoordinateRules.IsValidLatitude(latitude) || !CoordinateRules.IsValidLongitude(longitude))
                continue;

            var name = row.Cells.FirstOrDefault()?.Display;
            if (string.IsNullOrWhiteSpace(name)) name = $"Row {index + 1}";
            name = TruncateMapText(name, 100);
            points.Add(new QueryMapPoint(name, latitude, longitude, BuildMapDetails(row)));
        }

        if (points.Count == 0) { ErrorMessage = Strings.ErrNoValidCoordinates; return; }
        ErrorMessage = "";
        ShowSelectedRowsOnMapRequested?.Invoke(TableName, points);
    }

    public void SetSelectedRows(IReadOnlyList<GridRow> rows) => SelectedRows = rows;

    [RelayCommand]
    private void AddFilter()
        => Filters.Add(new FilterRowViewModel(FilterableColumns.FirstOrDefault()) { ColumnMeta = _columnMeta });

    [RelayCommand] private void RemoveFilter(FilterRowViewModel row) => Filters.Remove(row);
    [RelayCommand] private void SelectAllColumns() { foreach (var c in Columns) c.IsSelected = true; }
    [RelayCommand] private void SelectNoColumns() { foreach (var c in Columns) c.IsSelected = false; }

    private void OnMapDetailColumnChanged(MapDetailColumnItem item, bool selected)
    {
        if (_suppressMapDetailChange) return;
        if (selected && MapDetailColumnCount > MaxMapDetailColumns)
        {
            _suppressMapDetailChange = true;
            item.IsSelected = false;
            _suppressMapDetailChange = false;
        }
        OnPropertyChanged(nameof(MapDetailColumnCount));
    }

    private IReadOnlyList<string> GetMapRequiredColumns()
    {
        var orderColumns = UsesPrimaryKey
            ? _keyColumns.Select(column => column.Name).ToList()
            : new List<string>();
        if (!UsesPrimaryKey && OrderColumn is { } orderColumn) orderColumns.Add(orderColumn);
        return new[] { LatitudeColumn, LongitudeColumn }
            .Where(name => name is not null && CoordinateColumns.Contains(name))
            .Cast<string>()
            .Concat(MapDetailColumns.Where(column => column.IsSelected).Select(column => column.Name))
            .Concat(orderColumns)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private bool SameRowsByOrder(PageResult refreshed)
    {
        var indexes = refreshed.Columns.Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        var identityColumns = _displayColumns.Concat(_base!.OrderBy).Distinct(StringComparer.Ordinal);
        for (var rowIndex = 0; rowIndex < Rows.Count; rowIndex++)
            foreach (var column in identityColumns)
                if (!Rows[rowIndex].MapValues.TryGetValue(column, out var previous)
                    || !indexes.TryGetValue(column, out var currentIndex)
                    || !MapValuesEqual(previous, refreshed.Rows[rowIndex][currentIndex]))
                    return false;
        return true;
    }

    private static bool MapValuesEqual(object? left, object? right) =>
        left is byte[] leftBytes && right is byte[] rightBytes
            ? leftBytes.AsSpan().SequenceEqual(rightBytes)
            : Equals(left, right);

    private async Task<bool> EnsureMapValuesAvailableAsync()
    {
        if (_base is null || _lastResult is null) return false;
        var requiredColumns = _displayColumns.Concat(GetMapRequiredColumns()).Distinct(StringComparer.Ordinal).ToList();
        if (_base.Columns.SequenceEqual(requiredColumns, StringComparer.Ordinal)) return true;

        var previousBase = _base;
        _base = _base with { Columns = requiredColumns };
        var fetched = await FetchAsync(_current);
        if (fetched is null)
        {
            _base = previousBase;
            return false;
        }

        if (fetched.Value.Result.Rows.Count != Rows.Count || !SameRowsByOrder(fetched.Value.Result))
        {
            Show(fetched.Value.Result, fetched.Value.Ms);
            ErrorMessage = Strings.ErrSelectRows;
            return false;
        }

        _lastResult = fetched.Value.Result;
        for (var index = 0; index < Rows.Count; index++)
            Rows[index].UpdateMapValues(ToMapValues(fetched.Value.Result.Columns, fetched.Value.Result.Rows[index]));
        HasNext = fetched.Value.Result.HasMore;
        return true;
    }

    private static IReadOnlyDictionary<string, object?> ToMapValues(IReadOnlyList<string> columns, object?[] values)
    {
        var result = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
        for (var index = 0; index < columns.Count; index++) result[columns[index]] = values[index];
        return result;
    }

    private IReadOnlyDictionary<string, string>? BuildMapDetails(GridRow row) => BuildMapDetails(row.MapValues);

    private IReadOnlyDictionary<string, string>? BuildMapDetails(IReadOnlyDictionary<string, object?> values)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalLength = 0;
        foreach (var column in MapDetailColumns.Where(item => item.IsSelected).Take(MaxMapDetailColumns))
        {
            if (!values.TryGetValue(column.Name, out var rawValue)) continue;
            var key = TruncateMapText(column.Name, 80);
            var remaining = MaxMapDetailTotalLength - totalLength - key.Length;
            if (remaining <= 0) break;
            var value = TruncateMapText(CreateCell(rawValue).FullValue, Math.Min(MaxMapDetailValueLength, remaining));
            details[key] = value;
            totalLength += key.Length + value.Length;
        }
        return details.Count == 0 ? null : details;
    }

    private static string TruncateMapText(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        var length = Math.Max(0, maxLength - 1);
        if (length > 0 && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return value[..length] + "…";
    }

    private bool TryBuildRequest(out PageRequest request, out IReadOnlyList<string> displayColumns)
    {
        request = null!;
        displayColumns = [];
        ErrorMessage = "";
        var selected = Columns.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        if (selected.Count == 0) { ErrorMessage = Strings.ErrNoColumns; return false; }

        var filters = new List<FilterCondition>();
        foreach (var row in Filters)
        {
            var incomplete = row.Column is null
                || (row.NeedsValue && string.IsNullOrWhiteSpace(row.Value))
                || (row.NeedsSecondValue && string.IsNullOrWhiteSpace(row.Value2));
            if (incomplete) { ErrorMessage = Strings.ErrIncompleteFilter; return false; }
            filters.Add(new FilterCondition(row.Column!, row.SelectedOperator.Operator,
                row.NeedsValue ? row.Value : null, row.NeedsSecondValue ? row.Value2 : null));
        }

        IReadOnlyList<string> order;
        if (UsesPrimaryKey) order = _keyColumns.Select(c => c.Name).ToList();
        else if (OrderColumn is not null) order = [OrderColumn];
        else { ErrorMessage = Strings.ErrPickOrder; return false; }

        displayColumns = selected;
        var queryColumns = selected.Concat(GetMapRequiredColumns()).Distinct(StringComparer.Ordinal).ToList();
        request = new PageRequest(_database, _table.Schema, _table.Name, queryColumns, filters,
            UseOr ? FilterLogic.Or : FilterLogic.And, order, UsesPrimaryKey, PageSize, 0, null);
        return true;
    }

    private async Task<(PageResult Result, long Ms)?> FetchAsync(PageCursor cursor)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _running = cts;
        IsBusy = true;
        ErrorMessage = "";
        var watch = Stopwatch.StartNew();
        try
        {
            var request = _base! with { Offset = cursor.Offset, AfterKey = cursor.AfterKey };
            var result = await _metadata.GetPageAsync(_settings, request, cts.Token);
            return (result, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { PageInfo = Strings.Cancelled; return null; }
        catch (DatabaseAccessException ex) { ErrorMessage = Strings.Describe(ex); return null; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
            ErrorMessage = Strings.ErrInvalidQuery + ex.Message;
            return null;
        }
        finally { IsBusy = false; _running = null; }
    }

    private void Show(PageResult result, long milliseconds)
    {
        var firstResult = !HasResult;
        _lastResult = result;
        ResultColumns = _displayColumns;
        OnPropertyChanged(nameof(ResultColumns));
        ResultColumnsChanged?.Invoke(ResultColumns);
        if (LatitudeColumn is null) LatitudeColumn = DetectCoordinateColumn(true);
        if (LongitudeColumn is null) LongitudeColumn = DetectCoordinateColumn(false);
        var resultIndexes = result.Columns.Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        var displayIndexes = _displayColumns.Select(name => resultIndexes[name]).ToArray();
        Rows = result.Rows.Select(row => new GridRow(
            displayIndexes.Select(index => CreateCell(row[index])).ToArray(),
            ToMapValues(result.Columns, row))).ToList();
        SelectedRows = [];
        HasResult = true;
        // The settings card is tall. Once the first result is there, the table gets the room; the gear button brings the card back.
        if (firstResult) IsConfigVisible = false;
        HasNext = result.HasMore;
        HasPrevious = _history.Count > 0;
        PageInfo = result.Rows.Count == 0
            ? Strings.NoResults
            : $"{Strings.RowsRange(_current.Offset + 1, _current.Offset + result.Rows.Count)} · {milliseconds:N0} ms";
    }

    private static GridCell CreateCell(object? value)
    {
        var full = value switch
        {
            null => "NULL",
            string s => s,
            DateTime d => FormatDateTimeCell(d),
            byte[] b => "0x" + Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 16))) + (b.Length > 16 ? "…" : ""),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
        var truncated = value is string text && text.Length > 160;
        return new GridCell(truncated ? full[..160] + "…" : full, full, value, truncated);
    }

    private async IAsyncEnumerable<object?[]> ReadExportRowsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var request = _base! with { Columns = _displayColumns };
        const int pageSize = 500;
        long offset = 0;
        object?[]? afterKey = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _metadata.GetPageAsync(_settings,
                request with { PageSize = pageSize, Offset = offset, AfterKey = afterKey }, ct);
            foreach (var row in page.Rows)
            {
                ct.ThrowIfCancellationRequested();
                _exportedRows++;
                yield return row;
            }
            if (!page.HasMore) yield break;
            offset += pageSize;
            afterKey = request.UseKeyset && offset >= KeysetThreshold ? page.LastKey : null;
        }
    }

    private string? DetectCoordinateColumn(bool latitude)
    {
        var preferred = latitude
            ? new[] { "latitude", "lat", "y" }
            : new[] { "longitude", "long", "lng", "lon", "x" };
        var matches = new List<(string Name, int Index, int Rank)>();
        for (var index = 0; index < _columnMeta.Count; index++)
        {
            var name = _columnMeta[index].Name;
            var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            for (var rank = 0; rank < preferred.Length; rank++)
            {
                if (normalized == preferred[rank]) { matches.Add((name, index, rank)); break; }
                if (normalized.Contains(preferred[rank], StringComparison.Ordinal))
                {
                    matches.Add((name, index, rank + preferred.Length));
                    break;
                }
            }
        }
        return matches.OrderBy(match => match.Rank).ThenBy(match => match.Index).Select(match => match.Name).FirstOrDefault();
    }

    private static bool TryCoordinate(object? value, out double coordinate)
    {
        coordinate = 0;
        if (value is null or DBNull) return false;
        if (value is string text)
        {
            text = string.Concat(text.Select(c =>
            {
                var digit = CharUnicodeInfo.GetDecimalDigitValue(c);
                return digit is >= 0 and <= 9 ? (char)('0' + digit) : c;
            })).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate);
        }
        try { coordinate = Convert.ToDouble(value, CultureInfo.InvariantCulture); return double.IsFinite(coordinate); }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException) { return false; }
    }

    private static string FormatDateTimeCell(DateTime value)
    {
        try
        {
            var display = PersianDateTimeCodec.ToPersianDisplay(value);
            if (value.TimeOfDay == TimeSpan.Zero) display = display[..10];
            return string.Concat(display.Select(c =>
                c is >= '0' and <= '9' ? (char)('\u06f0' + c - '0') : c));
        }
        catch (ArgumentOutOfRangeException)
        {
            return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
