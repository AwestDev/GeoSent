using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDbExplorer.Application.Abstractions;
using FastDbExplorer.Domain;
using FastDbExplorer.Wpf.Localization;
using FastDbExplorer.Wpf.Services;

namespace FastDbExplorer.Wpf.ViewModels;

public sealed partial class MapLayerItem(string name, Action<string, bool> onChanged) : ObservableObject
{
    public string Name => name;

    [ObservableProperty] private bool _isVisible = true;

    partial void OnIsVisibleChanged(bool value) => onChanged(name, value);
}

/// <summary>One set of rows sent to the map from the Explorer or from Monitoring. Sets are independent and stay until the user removes them.</summary>
public sealed record QueryResultSet(MapLayer Layer, MapLayerKind Kind);

/// <summary>
/// Map workspace: opens a map file, owns the opened source, and relays commands to the page through events
/// (the view talks to the WebView2/MapLibre page; this class never touches UI controls).
/// Phase 2 adds the Excel coordinate layers: import, list, visibility, radius (metres), rename, delete, refresh, zoom,
/// and a per-layer marker icon + colour. It also remembers the last opened map (reopened at start-up) and shows the
/// point the mouse is over / the selected one. Rows sent from the Explorer or Monitoring are kept as separate, temporary
/// result sets (<see cref="QueryResults"/>), each removable on its own.
/// </summary>
public sealed partial class MapViewModel : ObservableObject
{
    private readonly IMapSourceFactory _factory;
    private readonly IFileDialogService _dialogs;
    private readonly IExcelImportService _excel;
    private readonly ICoordinateLayerStore _store;
    private readonly IMapSettingsStore _settings;
    private readonly SemaphoreSlim _storeGate = new(1, 1); // one store operation at a time (the store writes whole files)
    private readonly List<QueryResultSet> _queryResults = [];
    private bool _coordinateLayersLoaded;

    public MapViewModel(
        IMapSourceFactory factory,
        IFileDialogService dialogs,
        IExcelImportService excel,
        ICoordinateLayerStore store,
        IMapSettingsStore settings)
    {
        _factory = factory;
        _dialogs = dialogs;
        _excel = excel;
        _store = store;
        _settings = settings;
        CoordinateLayers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCoordinateLayers));
            OnPropertyChanged(nameof(ShowCoordinatePanel));
            OnPropertyChanged(nameof(ShowEmptyExcelHint));
            OnPropertyChanged(nameof(ExcelTabText));
        };
        ResetInfo();
    }

    public event Action? BackRequested;
    public event Action? SourceChanged;
    public event Action<string, bool>? LayerToggled;
    public event Action? FitRequested;

    /// <summary>A coordinate layer was added, or its data/radius/style changed: the view (re)sends it to the page.</summary>
    public event Action<MapLayer>? CoordinateLayerChanged;
    public event Action<Guid, bool>? CoordinateLayerVisibilityChanged;
    public event Action<Guid>? CoordinateLayerRemoved;
    public event Action<Guid>? CoordinateLayerZoomRequested;
    public event Action<Guid, string>? CoordinateLayerRenamed;

    /// <summary>A new set of Explorer/Monitoring rows was added to the map (the view draws it and zooms to it).</summary>
    public event Action<QueryResultSet>? QueryResultAdded;

    /// <summary>A result set was removed from the map.</summary>
    public event Action<Guid>? QueryResultRemoved;

    /// <summary>The user asked to drop the highlighted point; the view tells the page.</summary>
    public event Action? SelectionClearRequested;

    /// <summary>The opened map file. The view reads tiles from it.</summary>
    public IMapSource? Source { get; private set; }

    public ObservableCollection<MapLayerItem> Layers { get; } = [];

    public ObservableCollection<CoordinateLayerItem> CoordinateLayers { get; } = [];

    /// <summary>All result sets currently on the map, oldest first.</summary>
    public IReadOnlyList<QueryResultSet> QueryResults => _queryResults;

    /// <summary>The newest result set's layer (null when there is none).</summary>
    public MapLayer? QueryResultLayer => _queryResults.Count > 0 ? _queryResults[^1].Layer : null;

    public bool HasQueryResultLayer => _queryResults.Count > 0;

    public string QueryResultLayerSummary => _queryResults.Count == 0
        ? ""
        : CoordinateStrings.QueryResultsSummary(_queryResults.Sum(set => set.Layer.Points.Count));

    [ObservableProperty] private bool _hasMap;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _formatText = "";
    [ObservableProperty] private string _cursorText = "";
    [ObservableProperty] private string _zoomText = "";
    [ObservableProperty] private string _infoTitle = "";
    [ObservableProperty] private string _infoBody = "";
    [ObservableProperty] private bool _infoIsProblem;
    [ObservableProperty] private bool _isLayersOpen = true;
    [ObservableProperty] private bool _hasLayers;
    [ObservableProperty] private string _coordinateStatus = "";
    [ObservableProperty] private bool _coordinateStatusIsProblem;

    /// <summary>Side panel tab: Excel points (true) or the layers of the opened map file (false).</summary>
    [ObservableProperty] private bool _isExcelTab = true;

    // Point under the mouse (status bar) and the clicked point (info card in the coordinate panel).
    [ObservableProperty] private string _hoverText = "";
    [ObservableProperty] private bool _hasSelectedPoint;
    [ObservableProperty] private string _selectedPointName = "";
    [ObservableProperty] private string _selectedPointLayer = "";
    [ObservableProperty] private string _selectedPointCoords = "";

    public bool IsMapLayersTab
    {
        get => !IsExcelTab;
        set => IsExcelTab = !value;
    }

    public string ExcelTabText => MapUiStrings.TabExcel(CoordinateLayers.Count);
    public string MapTabText => MapUiStrings.TabMap(Layers.Count);

    /// <summary>The single side panel (tabs: Excel points | map layers). It sits beside the map, never over it (WebView2 airspace).</summary>
    public bool ShowSidePanel => HasMap && IsLayersOpen;

    public bool ShowEmptyExcelHint => !HasCoordinateLayers;
    public bool HasNoMapLayers => !HasLayers;

    public bool ShowLayersPanel => HasMap && IsLayersOpen && HasLayers;

    public bool HasCoordinateLayers => CoordinateLayers.Count > 0;

    public bool HasCoordinateStatus => CoordinateStatus.Length > 0;

    /// <summary>The coordinate panel needs the map (the layers are drawn on it) and has something to show.</summary>
    public bool ShowCoordinatePanel => HasMap && (HasCoordinateLayers || HasCoordinateStatus);

    partial void OnIsExcelTabChanged(bool value) => OnPropertyChanged(nameof(IsMapLayersTab));

    partial void OnHasMapChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLayersPanel));
        OnPropertyChanged(nameof(ShowCoordinatePanel));
        OnPropertyChanged(nameof(ShowSidePanel));
        if (!value)
        {
            HoverText = "";
            ClearSelectedPoint();
        }
    }

    partial void OnIsLayersOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLayersPanel));
        OnPropertyChanged(nameof(ShowSidePanel));
    }

    partial void OnHasLayersChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLayersPanel));
        OnPropertyChanged(nameof(HasNoMapLayers));
        OnPropertyChanged(nameof(MapTabText));
    }

    partial void OnCoordinateStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasCoordinateStatus));
        OnPropertyChanged(nameof(ShowCoordinatePanel));
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke();

    [RelayCommand]
    private void ToggleLayers() => IsLayersOpen = !IsLayersOpen;

    [RelayCommand]
    private void ShowAllMapLayers()
    {
        foreach (var layer in Layers) layer.IsVisible = true;
    }

    [RelayCommand]
    private void HideAllMapLayers()
    {
        foreach (var layer in Layers) layer.IsVisible = false;
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var path = _dialogs.PickMapFile();
        if (path is not null) await OpenPathAsync(path);
    }

    /// <summary>
    /// Start-up: reopens the map that was open last time. Nothing saved = nothing happens (empty state);
    /// a saved file that no longer exists = a clear error card instead of a silent empty map.
    /// </summary>
    public async Task LoadLastMapAsync()
    {
        try
        {
            var path = await _settings.LoadLastMapPathAsync();
            if (string.IsNullOrWhiteSpace(path) || HasMap) return; // nothing saved, or the user was faster

            if (!File.Exists(path))
            {
                ShowProblem((CoordinateStrings.LastMapMissingTitle, CoordinateStrings.LastMapMissingBody(path)));
                return;
            }

            await OpenPathAsync(path);
        }
        catch (Exception ex) // start-up boundary: a bad settings file or map must never stop the application
        {
            ShowProblem((Strings.CorruptTitle, ex.Message));
        }
    }

    public async Task OpenPathAsync(string path)
    {
        IsLoading = true;
        try
        {
            var source = await _factory.OpenAsync(path, CancellationToken.None);

            Source?.Dispose();
            Source = source;
            Layers.Clear();
            foreach (var name in source.Info.Layers)
                Layers.Add(new MapLayerItem(name, (n, v) => LayerToggled?.Invoke(n, v)));
            HasLayers = Layers.Count > 0;
            OnPropertyChanged(nameof(MapTabText));

            FileName = Path.GetFileName(path);
            FormatText = $"{source.Info.FormatLabel} · {(source.Info.Kind == MapTileKind.Vector ? "Vector" : "Raster")}";
            CursorText = "";
            ZoomText = "";
            InfoIsProblem = false;
            HasMap = true;
            SourceChanged?.Invoke();
            await RememberMapAsync(path);
        }
        catch (UnsupportedMapFormatException ex)
        {
            ShowProblem(ex.Reason switch
            {
                UnsupportedMapReason.OsmPbfNeedsConversion => (Strings.OsmTitle, Strings.OsmBody + ex.Message),
                UnsupportedMapReason.UnknownSqliteSchema => (Strings.SchemaTitle, Strings.SchemaBody + ex.Message),
                _ => (Strings.UnknownTitle, Strings.UnknownBody + ex.Message)
            });
        }
        catch (Exception ex) when (ex is MapSourceException or IOException or UnauthorizedAccessException)
        {
            ShowProblem((Strings.CorruptTitle, ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Saves the path of a map that opened. Failing to save is not worth bothering the user.</summary>
    private async Task RememberMapAsync(string path)
    {
        try { await _settings.SaveLastMapPathAsync(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public void ReportAssetsMissing() => ShowProblem((Strings.AssetsMissingTitle, Strings.AssetsMissingBody));

    public void ReportWebViewProblem(string details) =>
        ShowProblem((Strings.WebViewMissingTitle, Strings.WebViewMissingBody + details));

    public void SetCursor(double lng, double lat) =>
        CursorText = string.Create(CultureInfo.InvariantCulture, $"{lat:F5}, {lng:F5}");

    public void SetZoom(double zoom) =>
        ZoomText = string.Create(CultureInfo.InvariantCulture, $"{Strings.Zoom} {zoom:F1}");

    // ---- Point hover / selection (messages from the page) -----------------------------------------------------

    /// <summary>The mouse is over a coordinate point (name set) or left it (name null).</summary>
    public void SetHoverPoint(string? name, string? layerName) =>
        HoverText = string.IsNullOrEmpty(name) ? "" : CoordinateStrings.HoverPoint(name, layerName);

    public void SetSelectedPoint(string name, string? layerName, double latitude, double longitude)
    {
        SelectedPointName = name;
        SelectedPointLayer = layerName ?? "";
        SelectedPointCoords = CoordinateStrings.Coordinates(latitude, longitude);
        HasSelectedPoint = true;
    }

    public void ClearSelectedPoint()
    {
        HasSelectedPoint = false;
        SelectedPointName = "";
        SelectedPointLayer = "";
        SelectedPointCoords = "";
    }

    // ---- Rows sent from the Explorer / Monitoring ---------------------------------------------------------------

    /// <summary>
    /// Adds the rows as a NEW result set. Earlier sets (from the Explorer or from Monitoring) stay on the map until the user
    /// removes them. The very same rows from the same table are not added twice: the existing set is only shown again.
    /// </summary>
    public void ShowQueryResultLayer(string tableName, IReadOnlyList<QueryMapPoint> points, MapLayerKind kind = MapLayerKind.Explorer)
    {
        var source = kind == MapLayerKind.Monitoring ? MapLayerKind.Monitoring : MapLayerKind.Explorer;

        var duplicate = _queryResults.FirstOrDefault(set =>
            set.Kind == source && set.Layer.FileName == tableName && SamePoints(set.Layer, points));
        if (duplicate is not null)
        {
            CoordinateLayerZoomRequested?.Invoke(duplicate.Layer.Id);
            return;
        }

        var id = Guid.NewGuid();
        var mapPoints = points.Select((point, index) =>
            new MapPoint(Guid.NewGuid(), string.IsNullOrWhiteSpace(point.Name) ? $"Row {index + 1}" : point.Name,
                point.Latitude, point.Longitude, id, point.Details));
        var sameTable = _queryResults.Count(set => set.Kind == source && set.Layer.FileName == tableName);
        var layer = new MapLayer(id, MapToolStrings.ResultLayerName(source, tableName, sameTable + 1), tableName, mapPoints);

        var added = new QueryResultSet(layer, source);
        _queryResults.Add(added);
        NotifyQueryResultsChanged();
        QueryResultAdded?.Invoke(added);
    }

    /// <summary>Removes one result set from the map.</summary>
    public void RemoveQueryResult(Guid layerId)
    {
        _liveMonitors.Remove(layerId); // a live monitor layer forgets its points with the layer
        StopExpiryTimerIfIdle();
        var set = _queryResults.FirstOrDefault(s => s.Layer.Id == layerId);
        if (set is null) return;
        _queryResults.Remove(set);
        NotifyQueryResultsChanged();
        QueryResultRemoved?.Invoke(layerId);
    }

    /// <summary>Removes every result set (also called when the session ends).</summary>
    public void ClearQueryResultLayer()
    {
        foreach (var set in _queryResults.ToList()) RemoveQueryResult(set.Layer.Id);
    }

    private void NotifyQueryResultsChanged()
    {
        OnPropertyChanged(nameof(HasQueryResultLayer));
        OnPropertyChanged(nameof(QueryResultLayerSummary));
        OnPropertyChanged(nameof(QueryResultLayer));
    }

    private static bool SamePoints(MapLayer layer, IReadOnlyList<QueryMapPoint> points)
    {
        if (layer.Points.Count != points.Count) return false;
        for (var i = 0; i < points.Count; i++)
            if (layer.Points[i].Latitude != points[i].Latitude || layer.Points[i].Longitude != points[i].Longitude) return false;
        return true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        ClearSelectedPoint();
        SelectionClearRequested?.Invoke();
    }

    private void ShowProblem((string Title, string Body) problem)
    {
        HasMap = false;
        InfoIsProblem = true;
        (InfoTitle, InfoBody) = problem;
    }

    private void ResetInfo()
    {
        InfoIsProblem = false;
        InfoTitle = Strings.MapEmptyTitle;
        InfoBody = Strings.MapEmptyBody;
    }

    // ---- Excel coordinate layers (Phase 2) --------------------------------------------------------------------

    /// <summary>Loads the saved layers once (called by the view when it appears). Safe to call repeatedly.</summary>
    public async Task EnsureCoordinateLayersLoadedAsync()
    {
        if (_coordinateLayersLoaded) return;
        _coordinateLayersLoaded = true;
        try
        {
            IReadOnlyList<MapLayer> stored;
            await _storeGate.WaitAsync();
            try { stored = await _store.LoadAllAsync(); }
            finally { _storeGate.Release(); }

            foreach (var layer in stored)
                if (CoordinateLayers.All(i => i.Id != layer.Id)) AddCoordinateLayer(layer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _coordinateLayersLoaded = false;
            SetCoordinateStatus(CoordinateStrings.LoadFailed + ex.Message, true);
        }
    }

    [RelayCommand]
    private async Task ImportExcelAsync()
    {
        var path = _dialogs.PickExcelFile();
        if (path is null) return;

        var result = await _excel.ImportAsync(path, CancellationToken.None);
        if (result.Layer is not { } layer)
        {
            SetCoordinateStatus(CoordinateStrings.ImportFailed(result.Issues), true);
            return;
        }

        // A new layer starts with the next palette colour, so neighbouring layers are told apart at a glance.
        layer.Color = MapMarkerCatalog.Palette[CoordinateLayers.Count % MapMarkerCatalog.Palette.Count];
        AddCoordinateLayer(layer);
        IsExcelTab = true;
        SetCoordinateStatus(
            CoordinateStrings.Imported(result.RowsImported, result.RowsSkipped, result.Issues.Count > 0 ? result.Issues[0] : null),
            false);
        await SaveLayerAsync(layer); // a save problem replaces the success text
    }

    private void AddCoordinateLayer(MapLayer layer)
    {
        CoordinateLayers.Add(new CoordinateLayerItem(layer, this));
        CoordinateLayerChanged?.Invoke(layer);
    }

    internal void OnCoordinateLayerVisibilityChanged(CoordinateLayerItem item, bool visible)
    {
        item.Layer.Visibility = visible;
        CoordinateLayerVisibilityChanged?.Invoke(item.Id, visible);
        _ = SaveLayerAsync(item.Layer);
    }

    /// <summary>The icon or colour of a layer changed: redraw it on the map and keep the choice.</summary>
    internal async Task ApplyLayerStyleAsync(CoordinateLayerItem item)
    {
        CoordinateLayerChanged?.Invoke(item.Layer);
        await SaveLayerAsync(item.Layer);
    }

    internal async Task ApplyRadiusAsync(CoordinateLayerItem item)
    {
        // The only place where user text becomes metres; everything after this (model, store, circles) is metres.
        if (!RadiusInput.TryParse(item.RadiusText, out var meters))
        {
            item.RadiusError = CoordinateStrings.RadiusInvalid;
            return;
        }

        item.RadiusError = "";
        item.Layer.RadiusMeters = meters;
        item.RadiusText = CoordinateLayerItem.FormatRadius(meters);
        item.NotifyLayerDataChanged();
        CoordinateLayerChanged?.Invoke(item.Layer);
        await SaveLayerAsync(item.Layer);
    }

    /// <summary>Validates and applies the typed name; the layer keeps its old name until the new one is valid.</summary>
    internal async Task RenameCoordinateLayerAsync(CoordinateLayerItem item)
    {
        var name = item.EditName.Trim();
        if (name.Length == 0)
        {
            item.EditError = CoordinateStrings.NameEmpty;
            return;
        }
        if (name.Length > CoordinateStrings.MaxNameLength)
        {
            item.EditError = CoordinateStrings.NameTooLong();
            return;
        }

        item.EditError = "";
        item.IsRenaming = false;
        if (name == item.Layer.Name) return;

        item.Layer.Name = name;
        item.NotifyLayerDataChanged();
        CoordinateLayerRenamed?.Invoke(item.Id, name);
        SetCoordinateStatus(CoordinateStrings.Renamed(name), false);
        await SaveLayerAsync(item.Layer); // a save problem replaces the success text
    }

    internal void ZoomToCoordinateLayer(CoordinateLayerItem item) => CoordinateLayerZoomRequested?.Invoke(item.Id);

    /// <summary>Re-reads the layer from the store (the stored copy wins) and redraws it on the map.</summary>
    internal async Task RefreshCoordinateLayerAsync(CoordinateLayerItem item)
    {
        try
        {
            IReadOnlyList<MapLayer> stored;
            await _storeGate.WaitAsync();
            try { stored = await _store.LoadAllAsync(); }
            finally { _storeGate.Release(); }

            var fresh = stored.FirstOrDefault(l => l.Id == item.Id);
            if (fresh is null)
            {
                CoordinateLayerChanged?.Invoke(item.Layer);
                SetCoordinateStatus(CoordinateStrings.NotInStore(item.Name), true);
                return;
            }

            item.Sync(fresh);
            CoordinateLayerChanged?.Invoke(fresh);
            SetCoordinateStatus(CoordinateStrings.Refreshed(item.Name), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetCoordinateStatus(CoordinateStrings.LoadFailed + ex.Message, true);
        }
    }

    internal async Task RemoveCoordinateLayerAsync(CoordinateLayerItem item)
    {
        try
        {
            await _storeGate.WaitAsync();
            try { await _store.RemoveAsync(item.Id); }
            finally { _storeGate.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetCoordinateStatus(CoordinateStrings.RemoveFailed + ex.Message, true);
            return;
        }

        CoordinateLayers.Remove(item);
        CoordinateLayerRemoved?.Invoke(item.Id);
    }

    private async Task SaveLayerAsync(MapLayer layer)
    {
        try
        {
            await _storeGate.WaitAsync();
            try { await _store.SaveAsync(layer); }
            finally { _storeGate.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetCoordinateStatus(CoordinateStrings.SaveFailed + ex.Message, true);
        }
    }

    private void SetCoordinateStatus(string text, bool isProblem)
    {
        CoordinateStatusIsProblem = isProblem;
        CoordinateStatus = text;
    }
}
