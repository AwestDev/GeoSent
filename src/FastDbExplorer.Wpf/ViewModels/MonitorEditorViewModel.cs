using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDbExplorer.Application.Abstractions;
using FastDbExplorer.Domain;
using FastDbExplorer.Infrastructure;
using FastDbExplorer.Wpf.Localization;
using FastDbExplorer.Wpf.Services;

namespace FastDbExplorer.Wpf.ViewModels;

public sealed partial class MonitorEditorViewModel : ObservableObject, IDisposable
{
    private readonly IDatabaseMetadataService _metadata;
    private readonly ICoordinateLayerStore _coordinateLayers;
    private readonly ConnectionSettings _settings;
    private readonly IFolderPickerService _folders;
    private readonly MonitorDefinition? _existing;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<ColumnInfo> _columns = [];   // full column metadata (needed for datetime detection + dry-run)

    public MonitorEditorViewModel(
        IDatabaseMetadataService metadata, ConnectionSettings settings, IReadOnlyList<DatabaseInfo> databases,
        IFolderPickerService folders, MonitorDefinition? existing, ICoordinateLayerStore coordinateLayers)
    {
        _metadata = metadata;
        _coordinateLayers = coordinateLayers;
        _settings = settings;
        _folders = folders;
        _existing = existing;
        Databases = databases.Select(d => d.Name).ToList();
        _outputFolder = DefaultFolder();

        if (existing is not null)
        {
            _name = existing.Name;
            _selectedDatabase = existing.Database;
            _selectedWatermark = existing.WatermarkColumn;
            _useOr = existing.Logic == FilterLogic.Or;
            _intervalText = existing.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
            _outputFolder = existing.OutputFolder;
            _playSound = existing.PlaySound;
            _useProximity = existing.HasProximityRule;
            _useLiveMap = existing.LiveMapEnabled;
            _liveMapMinutes = LiveResultLifetime.Normalize(existing.LiveMapMinutes);
            _liveMapShowAll = existing.LiveMapShowAllMatches;
            _selectedLatitudeColumn = existing.LatitudeColumn;
            _selectedLongitudeColumn = existing.LongitudeColumn;
            _distanceText = existing.DistanceMeters?.ToString("R", CultureInfo.InvariantCulture) ?? "20000";
        }
    }

    public event Action<MonitorDefinition>? Saved;
    public event Action? Cancelled;

    public bool IsNew      => _existing is null;
    public bool IsExisting => _existing is not null;
    public string Title      => IsNew ? MonitoringStrings.EditorTitleNew : MonitoringStrings.EditorTitleEdit;
    public string TargetText => _existing?.TargetText ?? "";
    public IReadOnlyList<string> Databases { get; }
    public ObservableCollection<FilterRowViewModel> Filters { get; } = [];
    public IReadOnlyList<OperatorOption> Operators => OperatorOption.All;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private IReadOnlyList<TableInfo> _tables = [];
    [ObservableProperty] private TableInfo? _selectedTable;
    [ObservableProperty] private IReadOnlyList<string> _watermarkColumns = [];
    [ObservableProperty] private string? _selectedWatermark;
    [ObservableProperty] private IReadOnlyList<string> _filterableColumns = [];
    [ObservableProperty] private IReadOnlyList<string> _coordinateColumns = [];
    /// <summary>Every saved layer as a checkbox: the proximity rule may watch several layers at once.</summary>
    public ObservableCollection<CoordinateLayerChoice> CoordinateLayerChoices { get; } = [];
    [ObservableProperty] private string? _selectedLatitudeColumn;
    [ObservableProperty] private string? _selectedLongitudeColumn;
    [ObservableProperty] private bool _useProximity;
    [ObservableProperty] private bool _useLiveMap;
    [ObservableProperty] private int _liveMapMinutes = LiveResultLifetime.DefaultMinutes;
    [ObservableProperty] private bool _liveMapShowAll;

    public IReadOnlyList<LiveMapDurationChoice> LiveMapDurations { get; } =
        LiveResultLifetime.Choices.Select(m => new LiveMapDurationChoice(m, MonitoringStrings.LiveMapDurationLabel(m))).ToList();

    /// <summary>The "show all matches" option only means something when a radius filters the map.</summary>
    public bool ShowLiveMapShowAll => UseProximity && UseLiveMap;
    [ObservableProperty] private string _distanceText = "20000";

    /// <summary>The coordinate columns are needed by both the proximity rule and the live map.</summary>
    public bool ShowCoordinatePickers => UseProximity || UseLiveMap;

    partial void OnUseProximityChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCoordinatePickers));
        OnPropertyChanged(nameof(ShowLiveMapShowAll));
    }
    partial void OnUseLiveMapChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCoordinatePickers));
        OnPropertyChanged(nameof(ShowLiveMapShowAll));
    }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(MatchAll))] private bool _useOr;

    [ObservableProperty] private string _intervalText = "60";
    [ObservableProperty] private string _outputFolder = "";
    [ObservableProperty] private bool _playSound = true;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isLoading;

    public bool MatchAll { get => !UseOr; set => UseOr = !value; }

    partial void OnSelectedDatabaseChanged(string? value) { if (IsNew) _ = LoadTablesAsync(value); }
    partial void OnSelectedTableChanged(TableInfo? value) { if (IsNew) _ = LoadColumnsAsync(value); }

    public async Task InitializeAsync()
    {
        try
        {
            try
            {
                var layers = await _coordinateLayers.LoadAllAsync(_lifetime.Token);
                var selectedIds = _existing?.SelectedCoordinateLayerIds ?? [];
                CoordinateLayerChoices.Clear();
                foreach (var layer in layers)
                    CoordinateLayerChoices.Add(new CoordinateLayerChoice(layer, selectedIds.Contains(layer.Id)));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ErrorMessage = "خواندن لایه‌های مختصات ذخیره‌شده ممکن نشد: " + ex.Message; }

            if (_existing is null)
            {
                SelectedDatabase = Databases.FirstOrDefault();
                return;
            }

            IsLoading = true;
            var existingColumns = await _metadata.GetColumnsAsync(
                _settings, _existing.Database, _existing.Schema, _existing.Table, _lifetime.Token);
            _columns = existingColumns;
            ApplyColumns();

            foreach (var c in _existing.Conditions)
            {
                var row = MakeFilter(c.Column);
                row.SelectedOperator = OperatorOption.All.First(o => o.Operator == c.Operator);
                row.Value  = c.Value  ?? "";
                row.Value2 = c.Value2 ?? "";
                Filters.Add(row);
            }
        }
        catch (OperationCanceledException) { }
        catch (DatabaseAccessException ex) { ErrorMessage = Strings.Describe(ex); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    private async Task LoadTablesAsync(string? database)
    {
        Tables = []; SelectedTable = null;
        if (database is null) return;

        IsLoading = true; ErrorMessage = "";
        try
        {
            var list = await _metadata.GetTablesAsync(_settings, database, _lifetime.Token);
            if (SelectedDatabase != database) return;
            Tables = list.ToList();
        }
        catch (OperationCanceledException) { }
        catch (DatabaseAccessException ex) { ErrorMessage = Strings.Describe(ex); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    private async Task LoadColumnsAsync(TableInfo? table)
    {
        _columns = []; WatermarkColumns = []; FilterableColumns = [];
        SelectedWatermark = null; Filters.Clear();
        if (table is null || SelectedDatabase is null) return;

        IsLoading = true; ErrorMessage = "";
        try
        {
            var columns = await _metadata.GetColumnsAsync(
                _settings, SelectedDatabase, table.Schema, table.Name, _lifetime.Token);
            if (SelectedTable != table) return;
            _columns = columns;
            ApplyColumns();
        }
        catch (OperationCanceledException) { }
        catch (DatabaseAccessException ex) { ErrorMessage = Strings.Describe(ex); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    private void ApplyColumns()
    {
        WatermarkColumns = _columns.Where(c => WatermarkTypes.IsSupported(c.TypeName)).Select(c => c.Name).ToList();
        FilterableColumns = _columns.Where(c => c.IsFilterable).Select(c => c.Name).ToList();
        CoordinateColumns = _columns.Where(c => MonitorCoordinateTypes.IsSupported(c.TypeName)).Select(c => c.Name).ToList();

        if (IsNew)
        {
            var key = _columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.KeyOrdinal)
                .FirstOrDefault(c => WatermarkTypes.IsSupported(c.TypeName));
            SelectedWatermark = key?.Name ?? WatermarkColumns.FirstOrDefault();
        }
    }

    /// <summary>
    /// Creates a new filter row pre-wired with the full column metadata so
    /// <see cref="FilterRowViewModel.IsDateTimeField"/> works without a second lookup.
    /// </summary>
    private FilterRowViewModel MakeFilter(string? column) =>
        new(column) { ColumnMeta = _columns };

    [RelayCommand]
    private void AddFilter() => Filters.Add(MakeFilter(FilterableColumns.FirstOrDefault()));

    [RelayCommand]
    private void RemoveFilter(FilterRowViewModel row) => Filters.Remove(row);

    [RelayCommand]
    private void BrowseFolder()
    {
        var picked = _folders.PickFolder(OutputFolder);
        if (picked is not null) OutputFolder = picked;
    }

    [RelayCommand] private void Cancel() => Cancelled?.Invoke();

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = "";

        var name = Name.Trim();
        if (name.Length == 0) { ErrorMessage = MonitoringStrings.ErrName; return; }

        string database, schema, table;
        if (_existing is not null)
        {
            database = _existing.Database;
            schema   = _existing.Schema;
            table    = _existing.Table;
        }
        else
        {
            if (SelectedDatabase is null || SelectedTable is null) { ErrorMessage = MonitoringStrings.ErrTarget; return; }
            database = SelectedDatabase;
            schema   = SelectedTable.Schema;
            table    = SelectedTable.Name;
        }

        var watermark = _existing?.WatermarkColumn ?? SelectedWatermark;
        if (watermark is null) { ErrorMessage = MonitoringStrings.ErrWatermark; return; }

        if (!int.TryParse(IntervalText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || minutes < MonitorDefinition.MinIntervalMinutes || minutes > MonitorDefinition.MaxIntervalMinutes)
        { ErrorMessage = MonitoringStrings.ErrInterval; return; }

        var folder = OutputFolder.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) { ErrorMessage = MonitoringStrings.ErrFolder; return; }
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { ErrorMessage = MonitoringStrings.ErrFolderCreate + ex.Message; return; }

        // The proximity rule stands on its own: a monitor may have conditions, a proximity rule, or both.
        // Only a monitor with neither would alert on every new row, so that combination is refused.
        // The checkboxes are the switches: unticking one removes its part even if old choices are still in the list.
        var proximityRequested = UseProximity;
        var liveMapRequested = UseLiveMap;
        var needsCoordinates = proximityRequested || liveMapRequested;
        var liveMapShowAll = liveMapRequested && proximityRequested && LiveMapShowAll;
        var layerIds = proximityRequested
            ? CoordinateLayerChoices.Where(choice => choice.IsSelected).Select(choice => choice.Layer.Id).ToList()
            : [];
        if (Filters.Count == 0 && !proximityRequested)
        { ErrorMessage = MonitoringStrings.ErrNoConditionsOrProximity; return; }

        var conditions = new List<FilterCondition>();
        foreach (var row in Filters)
        {
            var incomplete = row.Column is null
                || (row.NeedsValue && string.IsNullOrWhiteSpace(row.Value))
                || (row.NeedsSecondValue && string.IsNullOrWhiteSpace(row.Value2));
            if (incomplete) { ErrorMessage = Strings.ErrIncompleteFilter; return; }
            conditions.Add(new FilterCondition(
                row.Column!, row.SelectedOperator.Operator,
                row.NeedsValue ? row.Value : null,
                row.NeedsSecondValue ? row.Value2 : null));
        }

        var logic = UseOr ? FilterLogic.Or : FilterLogic.And;

        IReadOnlyList<Guid>? coordinateLayerIds = null;
        string? latitudeColumn = null;
        string? longitudeColumn = null;
        double? distanceMeters = null;
        if (needsCoordinates)
        {
            if (SelectedLatitudeColumn is null || SelectedLongitudeColumn is null
                || !CoordinateColumns.Contains(SelectedLatitudeColumn, StringComparer.Ordinal)
                || !CoordinateColumns.Contains(SelectedLongitudeColumn, StringComparer.Ordinal)
                || string.Equals(SelectedLatitudeColumn, SelectedLongitudeColumn, StringComparison.OrdinalIgnoreCase))
            { ErrorMessage = MonitoringStrings.ErrCoordinateColumns; return; }
            latitudeColumn = SelectedLatitudeColumn;
            longitudeColumn = SelectedLongitudeColumn;
        }

        if (proximityRequested)
        {
            if (layerIds.Count == 0)
            { ErrorMessage = MonitoringStrings.ErrNoProximityLayer; return; }
            if (CoordinateLayerChoices.Any(choice => choice.IsSelected && choice.Layer.Points.Count == 0))
            { ErrorMessage = MonitoringStrings.ErrEmptyProximityLayer; return; }

            var distanceText = DistanceText.Trim();
            if (distanceText.EndsWith('m') || distanceText.EndsWith('M'))
                distanceText = distanceText[..^1].Trim();
            if (!double.TryParse(distanceText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsedDistance)
                || !double.IsFinite(parsedDistance) || parsedDistance <= 0 || parsedDistance > CoordinateRules.MaxRadiusMeters)
            { ErrorMessage = $"فاصله باید عددی بزرگ‌تر از صفر و حداکثر {CoordinateRules.MaxRadiusMeters} متر باشد."; return; }

            coordinateLayerIds = layerIds;
            distanceMeters = parsedDistance;
        }

        try
        {
            var probe = new PageRequest(database, schema, table, [watermark], conditions, logic, [watermark], false, 1, 0, null);
            SelectQueryBuilder.Build(probe, _columns);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        { ErrorMessage = Strings.ErrInvalidQuery + ex.Message; return; }

        var sound      = PlaySound;
        var definition = _existing is null
            ? new MonitorDefinition(Guid.NewGuid(), name, _settings.Server, database, schema, table, watermark,
                conditions, logic, minutes, folder, sound, true,
                CoordinateLayerId: coordinateLayerIds is { Count: 1 } ? coordinateLayerIds[0] : null,
                LatitudeColumn: latitudeColumn, LongitudeColumn: longitudeColumn, DistanceMeters: distanceMeters,
                CoordinateLayerIds: coordinateLayerIds, LiveMapEnabled: liveMapRequested,
                LiveMapMinutes: LiveResultLifetime.Normalize(LiveMapMinutes), LiveMapShowAllMatches: liveMapShowAll)
            : _existing with { Name = name, Conditions = conditions, Logic = logic,
                               IntervalMinutes = minutes, OutputFolder = folder, PlaySound = sound,
                               CoordinateLayerId = coordinateLayerIds is { Count: 1 } ? coordinateLayerIds[0] : null,
                               LatitudeColumn = latitudeColumn, LongitudeColumn = longitudeColumn,
                               DistanceMeters = distanceMeters, CoordinateLayerIds = coordinateLayerIds,
                               LiveMapEnabled = liveMapRequested,
                               LiveMapMinutes = LiveResultLifetime.Normalize(LiveMapMinutes),
                               LiveMapShowAllMatches = liveMapShowAll };

        Saved?.Invoke(definition);
    }

    public void Dispose() => _lifetime.Cancel();

    private static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FastDbExplorer", "Reports");
}

/// <summary>One saved coordinate layer in the proximity list: the checkbox decides whether this layer is watched.</summary>
public sealed partial class CoordinateLayerChoice(MapLayer layer, bool isSelected) : ObservableObject
{
    public MapLayer Layer { get; } = layer;
    public string Name => Layer.Name;
    public string PointsText => MonitoringStrings.PointsCount(Layer.Points.Count);

    [ObservableProperty] private bool _isSelected = isSelected;
}

/// <summary>One choice of the live map display time (minutes; 0 = until cleared).</summary>
public sealed record LiveMapDurationChoice(int Minutes, string Label);
