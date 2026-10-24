using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FastDbExplorer.Application.CoordinateLayers;
using FastDbExplorer.Domain;
using FastDbExplorer.Wpf.Localization;
using FastDbExplorer.Wpf.Services;
using FastDbExplorer.Wpf.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace FastDbExplorer.Wpf.Views;

/// <summary>
/// Hosts the MapLibre page in WebView2. Tiles are never fetched from the network: requests to
/// https://tiles.local/{z}/{x}/{y} are intercepted and answered from the opened map file.
/// Excel coordinate layers travel as GeoJSON messages ("coordUpsert" / "coordVisible" / "coordRemove" / "coordFit" /
/// "coordRename" / "coordSelectClear"); the page answers with "pointHover" and "pointSelect".
/// "coordUpsert" also carries the marker style (color, iconKey, iconPath) and the layer kind/shape (excel, my points, Explorer
/// results, Monitoring results); a style change simply sends the layer again. The map tools overlay (go to / pick / measure /
/// result sets) is in <c>MapView.Tools.cs</c>.
/// The shell keeps one map view alive and only hides/shows it, so returning to the Map section is instant; the page also reports
/// its camera ("view"), which is sent back with the next "load" (for example after a theme change or a re-created page).
/// </summary>
public partial class MapView : UserControl
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private MapViewModel? _viewModel;
    private bool _pageReady;
    private bool _pageDark; // the theme the map style was built for

    // One WebView2 environment for every map view: two environments on the same data folder can conflict.
    private static Task<CoreWebView2Environment>? _sharedEnvironment;

    // Only one map WebView2 is created/alive at a time: the next view waits until the previous one has been released.
    private static readonly SemaphoreSlim WebGate = new(1, 1);

    // At most one detached map window exists.
    private static MapWindow? _openWindow;

    private CoreWebView2Environment? _environment;
    private Task? _initTask;
    private bool _gateHeld;
    private bool _released; // ReleaseWebView() has run: this view never touches its WebView2 again
    private bool _closing;  // set while this view is releasing: late WebView2 callbacks must not touch the control

    /// <summary>True for the copy that lives in <see cref="MapWindow"/>: it does not open another window itself.</summary>
    public bool IsDetached { get; init; }

    /// <summary>True once this view's WebView2 has been released; the shell then creates a fresh view instead of reusing this one.</summary>
    internal bool IsReleased => _released;

    public MapView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => { Detach(); _viewModel = e.NewValue as MapViewModel; Attach(); };
        Loaded += async (_, _) =>
        {
            if (_released || _initTask is not null) return; // never start a second WebView2 for the same view
            Attach();
            if (_viewModel is not null) _ = _viewModel.EnsureCoordinateLayersLoadedAsync();
            _initTask = InitializeWebAsync();
            await _initTask;
        };
        Unloaded += (_, _) => ReleaseWebView();
    }

    private void Attach()
    {
        Detach();
        if (_viewModel is null || _released) return;
        _viewModel.SourceChanged += PushSource;
        _viewModel.LayerToggled += PostLayer;
        _viewModel.FitRequested += PostFit;
        _viewModel.CoordinateLayerChanged += PostCoordinateLayer;
        _viewModel.CoordinateLayerVisibilityChanged += PostCoordinateVisibility;
        _viewModel.CoordinateLayerRemoved += PostCoordinateRemove;
        _viewModel.CoordinateLayerZoomRequested += PostCoordinateZoom;
        _viewModel.CoordinateLayerRenamed += PostCoordinateRename;
        _viewModel.SelectionClearRequested += PostSelectionClear;
        _viewModel.QueryResultAdded += PostQueryResultAdded;
        _viewModel.QueryResultRemoved += PostQueryResultRemoved;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (!IsDetached) _viewModel.PopOutRequested += OpenDetachedWindow;
        UpdateWebVisibility();
    }

    private void Detach()
    {
        if (_viewModel is null) return;
        _viewModel.SourceChanged -= PushSource;
        _viewModel.LayerToggled -= PostLayer;
        _viewModel.FitRequested -= PostFit;
        _viewModel.CoordinateLayerChanged -= PostCoordinateLayer;
        _viewModel.CoordinateLayerVisibilityChanged -= PostCoordinateVisibility;
        _viewModel.CoordinateLayerRemoved -= PostCoordinateRemove;
        _viewModel.CoordinateLayerZoomRequested -= PostCoordinateZoom;
        _viewModel.CoordinateLayerRenamed -= PostCoordinateRename;
        _viewModel.SelectionClearRequested -= PostSelectionClear;
        _viewModel.QueryResultAdded -= PostQueryResultAdded;
        _viewModel.QueryResultRemoved -= PostQueryResultRemoved;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PopOutRequested -= OpenDetachedWindow;
    }

    // ---- WebView2 lifetime -------------------------------------------------------------------------------------

    /// <summary>
    /// Releases this view's WebView2 explicitly and exactly once. Called from <c>Unloaded</c>, from the pop-out command
    /// (before the Map section is left) and from <see cref="MapWindow"/> while its window handle still exists, so the
    /// teardown never depends on when WPF raises <c>Unloaded</c>.
    /// </summary>
    internal void ReleaseWebView()
    {
        if (_released) return;
        _released = true;
        _closing = true;
        _viewModel?.SetPageReady(false);
        Detach();

        // Still being created: disposing in the middle of that is unsafe, so dispose right after it has finished.
        if (_initTask is { IsCompleted: false } pending)
        {
            pending.ContinueWith(_ => DisposeWebView(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        DisposeWebView();
    }

    private void DisposeWebView()
    {
        try
        {
            if (Web.CoreWebView2 is { } core)
            {
                core.WebResourceRequested -= OnTileRequested;
                core.WebMessageReceived -= OnMessage;
            }
            Web.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The host window already tore it down: nothing left to release.
        }
        finally
        {
            ReleaseGate();
        }
    }

    private void ReleaseGate()
    {
        if (!_gateHeld) return;
        _gateHeld = false;
        WebGate.Release();
    }

    /// <summary>The shell shows this (already running) view again. A theme change in the meantime needs the map style rebuilt.</summary>
    internal void OnShown()
    {
        if (_pageReady && !_released && _viewModel?.Source is not null && ThemeService.IsDark() != _pageDark) PushSource();
    }

    /// <summary>Opens the map in its own window. Both windows share this view model; this copy is released meanwhile.</summary>
    /// <remarks>
    /// Only one WebView2 is alive at a time: this view's WebView is released first (from this command, not from a
    /// visual-tree event), the Map section is left, the window opens after that, and when the window has completely
    /// closed the Map section is opened again (the shell then creates a fresh map view).
    /// </remarks>
    private void OpenDetachedWindow()
    {
        if (_viewModel is null) return;
        if (_openWindow is { IsLoaded: true } existing) { existing.Activate(); return; }
        if (_openWindow is not null) return; // the window is still opening

        var mainWindow = Window.GetWindow(this);
        var shell = mainWindow?.DataContext as MainViewModel;

        ReleaseWebView();
        shell?.NavigateCommand.Execute(AppSection.Home);

        var window = new MapWindow(_viewModel);
        _openWindow = window;
        window.Closed += (_, _) =>
        {
            _openWindow = null;
            if (mainWindow is null || shell is null) return;
            // Return to the Map section only after the window and its WebView2 are gone, not inside Closed itself,
            // and not at all while the application is shutting down.
            mainWindow.Dispatcher.BeginInvoke(() =>
            {
                if (mainWindow.IsLoaded && !mainWindow.Dispatcher.HasShutdownStarted)
                    shell.NavigateCommand.Execute(AppSection.Map);
            }, DispatcherPriority.Background);
        };
        Dispatcher.BeginInvoke(() => window.Show(), DispatcherPriority.ApplicationIdle);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapViewModel.IsPoppedOut) or nameof(MapViewModel.HasMap)) UpdateWebVisibility();
    }

    /// <summary>
    /// The WebView is a native window that WPF cannot layer over. The detached copy always shows the map when there is one.
    /// The copy in the Map section steps aside while the map is in its own window and shows a short note instead.
    /// </summary>
    private void UpdateWebVisibility()
    {
        if (_released) return;
        var hasMap = _viewModel?.HasMap == true;
        var stepAside = !IsDetached && _viewModel?.IsPoppedOut == true;
        Web.Visibility = hasMap && !stepAside ? Visibility.Visible : Visibility.Collapsed;
        DetachedNote.Visibility = stepAside ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task InitializeWebAsync()
    {
        if (_viewModel is null || _released) return;

        var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Map");
        if (!File.Exists(Path.Combine(assets, "maplibre-gl.js")))
        {
            _viewModel.ReportAssetsMissing();
            return;
        }

        // Wait (briefly) until the previous map WebView2 has been released; never longer than 5 s, so it cannot lock up.
        _gateHeld = await WebGate.WaitAsync(TimeSpan.FromSeconds(5));
        if (_released) return; // DisposeWebView (queued by ReleaseWebView) frees the gate

        CoreWebView2Environment environment;
        try
        {
            _sharedEnvironment ??= CreateEnvironmentAsync();
            environment = await _sharedEnvironment;
            if (_released) return; // released while the runtime was starting
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) // UI boundary: a missing runtime must become a readable message, not a crash
        {
            _sharedEnvironment = null; // allow a retry after the problem is fixed
            ReleaseGate();
            if (!_released) _viewModel?.ReportWebViewProblem(ex.Message);
            return;
        }

        if (_released || Web.CoreWebView2 is not { } core) return;
        _environment = environment;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true; // F12 for diagnosing map problems (debug builds only)
#else
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.Settings.IsStatusBarEnabled = false;
        Web.AllowExternalDrop = false;
        core.SetVirtualHostNameToFolderMapping("app.local", assets, CoreWebView2HostResourceAccessKind.Allow);
        // MapLibre fetches tiles inside Web Workers. The plain AddWebResourceRequestedFilter only sees requests from the
        // page itself (Document), so worker requests would never reach OnTileRequested and the map stays blank.
        core.AddWebResourceRequestedFilter(
            "https://tiles.local/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnTileRequested;
        core.WebMessageReceived += OnMessage;
        core.Navigate("https://app.local/map.html");
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastDbExplorer", "WebView2");
        return CoreWebView2Environment.CreateAsync(null, dataFolder);
    }

    private async void OnTileRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        // Once the view is releasing, its WebView2 is being disposed: a request that is still being answered must not
        // touch the request or the deferral any more (answering a disposed WebView2 can take the whole process down).
        if (_closing) return;
        CoreWebView2Deferral? deferral = null;
        try
        {
            deferral = e.GetDeferral(); // inside the try: on a WebView that is closing this can throw
            var parts = new Uri(e.Request.Uri).AbsolutePath.Trim('/').Split('/');
            var source = _viewModel?.Source;
            if (source is null || parts.Length != 3
                || !int.TryParse(parts[0], out var z) || !int.TryParse(parts[1], out var x) || !int.TryParse(parts[2], out var y))
            {
                e.Response = Respond(404, "Not Found", null, null);
                return;
            }

            // Read + gunzip off the UI thread; the page then receives plain protobuf, no Content-Encoding needed.
            var tile = await Task.Run(async () =>
            {
                var t = await source.GetTileAsync(z, x, y, CancellationToken.None);
                return t is { IsGzip: true } ? t with { Data = Gunzip(t.Data), IsGzip = false } : t;
            });
            if (_closing) return; // the view was released while the tile was being read
            e.Response = tile is null
                ? Respond(404, "Not Found", null, null)
                : Respond(200, "OK", tile.Data, tile.ContentType);
        }
        catch (Exception) // event-handler boundary: answer 500 so the page keeps working
        {
            if (!_closing)
            {
                try { e.Response = Respond(500, "Error", null, null); }
                catch (Exception) { /* the WebView went away meanwhile */ }
            }
        }
        finally
        {
            if (!_closing)
            {
                try { deferral?.Complete(); }
                catch (Exception) { /* the WebView went away meanwhile */ }
            }
        }
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private CoreWebView2WebResourceResponse Respond(int status, string reason, byte[]? data, string? contentType)
    {
        var headers = "Access-Control-Allow-Origin: *\r\nCache-Control: max-age=3600";
        if (contentType is not null)
        {
            var gzip = contentType.EndsWith("|gzip", StringComparison.Ordinal);
            headers += $"\r\nContent-Type: {contentType.Replace("|gzip", "")}";
            if (gzip) headers += "\r\nContent-Encoding: gzip"; // the browser unzips vector tiles for us
        }
        // The stored environment, not Web.CoreWebView2: the control may already be disposed.
        return _environment!.CreateWebResourceResponse(
            new MemoryStream(data ?? []), status, reason, headers);
    }

    /// <summary>Opens the format menu under the camera button (PNG or JPG).</summary>
    private void OnScreenshotMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void OnScreenshotPngClick(object sender, RoutedEventArgs e) =>
        await SaveScreenshotAsync(CoreWebView2CapturePreviewImageFormat.Png, "png");

    private async void OnScreenshotJpgClick(object sender, RoutedEventArgs e) =>
        await SaveScreenshotAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, "jpg");

    /// <summary>
    /// Saves the map as it is shown now, including an open point card. The picture is taken before the save dialog
    /// opens, so the dialog is never part of it.
    /// </summary>
    private async Task SaveScreenshotAsync(CoreWebView2CapturePreviewImageFormat format, string extension)
    {
        if (_released || _closing || Web.CoreWebView2 is not { } core) return;
        try
        {
            using var image = new MemoryStream();
            await core.CapturePreviewAsync(format, image);

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = MapUiStrings.ScreenshotTitle,
                Filter = extension == "png" ? "PNG (*.png)|*.png" : "JPEG (*.jpg)|*.jpg",
                DefaultExt = "." + extension,
                AddExtension = true,
                FileName = $"map-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            await File.WriteAllBytesAsync(dialog.FileName, image.ToArray());
        }
        catch (Exception ex)
        {
            MessageBox.Show(MapUiStrings.ScreenshotFailed + "\n" + ex.Message, MapUiStrings.ScreenshotTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_viewModel is null || _closing) return;
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        switch (type)
        {
            case "ready":
                _pageReady = true;
                _viewModel.SetPageReady(true);
                _viewModel.SetHoverPoint(null, null);
                _viewModel.ClearSelectedPoint(); // a fresh page has no highlighted point
                PushToolsInit();
                PushSource();
                PushCoordinateLayers();
                PushQueryResultLayers();
                PostToolResults();
                _viewModel.OnPageReady(); // an action that waited for this page (zoom to a layer chosen in the palette)
                break;
            case "cursor":
                _viewModel.SetCursor(root.GetProperty("lng").GetDouble(), root.GetProperty("lat").GetDouble());
                break;
            case "zoom":
                _viewModel.SetZoom(root.GetProperty("zoom").GetDouble());
                break;
            case "view":
                _viewModel.RememberView(
                    root.GetProperty("lng").GetDouble(), root.GetProperty("lat").GetDouble(), root.GetProperty("zoom").GetDouble());
                break;
            case "pointHover":
                _viewModel.SetHoverPoint(Text(root, "name"), Text(root, "layer"));
                break;
            case "pointSelect":
                if (root.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True
                    && Text(root, "name") is { } name)
                    _viewModel.SetSelectedPoint(name, Text(root, "layer"), root.GetProperty("lat").GetDouble(), root.GetProperty("lng").GetDouble());
                else
                    _viewModel.ClearSelectedPoint();
                break;
            default:
                HandleToolMessage(type, root); // go to / pick / measure helpers: add or delete a "my points" point, zoom to or remove result sets
                break;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void PushSource()
    {
        if (!_pageReady || _viewModel?.Source is not { } source) return;
        var info = source.Info;
        var saved = _viewModel.LastView;
        var dark = ThemeService.IsDark();
        _pageDark = dark;
        Post(new
        {
            type = "load",
            dark,
            // null for a newly opened file (the page starts on Iran); otherwise the camera the user left the map with.
            view = (saved is { } camera) ? new { lng = camera.Lng, lat = camera.Lat, zoom = camera.Zoom } : null,
            info = new
            {
                name = info.Name,
                formatLabel = info.FormatLabel,
                kind = info.Kind == MapTileKind.Vector ? "vector" : "raster",
                minZoom = info.MinZoom,
                maxZoom = info.MaxZoom,
                bounds = info.Bounds,
                center = info.Center,
                layers = info.Layers
            }
        });
        foreach (var layer in _viewModel.Layers.Where(l => !l.IsVisible)) PostLayer(layer.Name, false);
    }

    private void PostLayer(string name, bool visible) => Post(new { type = "layer", name, visible });

    private void PostFit() => Post(new { type = "fit" });

    // ---- Excel coordinate layers -------------------------------------------------------------------------------

    /// <summary>Sends every known coordinate layer; the page caches them and redraws after each map (re)load.</summary>
    private void PushCoordinateLayers()
    {
        if (_viewModel is null) return;
        foreach (var item in _viewModel.CoordinateLayers) PostCoordinateLayer(item.Layer);
    }

    // ---- Explorer / Monitoring result sets ---------------------------------------------------------------------

    /// <summary>Sends every result set (without moving the camera); used when a page starts.</summary>
    private void PushQueryResultLayers()
    {
        if (_viewModel is null) return;
        foreach (var set in _viewModel.QueryResults) PostCoordinateLayer(set.Layer);
    }

    /// <summary>A new set was sent from the Explorer or Monitoring: draw it, zoom to it, update the chips over the map.</summary>
    private void PostQueryResultAdded(QueryResultSet set)
    {
        PostCoordinateLayer(set.Layer);
        PostCoordinateZoom(set.Layer.Id);
        PostToolResults();
    }

    private void PostQueryResultRemoved(Guid layerId)
    {
        PostCoordinateRemove(layerId);
        PostToolResults();
    }

    private void PostCoordinateLayer(MapLayer layer)
    {
        // The source decides the marker: Excel layers keep their chosen icon/colour (round marker); "my points" use a pin;
        // Explorer results are amber diamonds with a magnifier, Monitoring results rose squares with a bell.
        var kind = KindOf(layer);
        var shape = MapSourceStyles.Shape(kind);
        var iconPath = MapSourceStyles.FixedGlyph(kind) ?? MapMarkerCatalog.Find(layer.IconKey)?.PathData;
        if (iconPath is null && shape != "circle") iconPath = MapMarkerCatalog.Find("dot")?.PathData; // pin/diamond/square always carry a glyph
        var color = MapSourceStyles.FixedColor(kind) ?? MapMarkerCatalog.EffectiveColor(layer.Id, layer.Color);

        Post(new
        {
            type = "coordUpsert",
            id = layer.Id,
            name = layer.Name,
            visible = layer.Visibility,
            radiusMeters = layer.RadiusMeters,
            kind = MapSourceStyles.Key(kind),
            shape,
            color,
            iconKey = layer.IconKey,
            iconPath,
            points = CoordinateLayerGeoJson.Points(layer),
            circles = CoordinateLayerGeoJson.Circles(layer),
            bounds = CoordinateLayerGeoJson.Bounds(layer)
        });
    }

    private void PostCoordinateVisibility(Guid id, bool visible) => Post(new { type = "coordVisible", id, visible });

    private void PostCoordinateRemove(Guid id) => Post(new { type = "coordRemove", id });

    private void PostCoordinateZoom(Guid id) => Post(new { type = "coordFit", id });

    /// <summary>A rename only changes a label, so a tiny message is sent instead of the whole layer.</summary>
    private void PostCoordinateRename(Guid id, string name) => Post(new { type = "coordRename", id, name });

    private void PostSelectionClear() => Post(new { type = "coordSelectClear" });

    private void Post(object message)
    {
        if (!_pageReady || _closing || Web.CoreWebView2 is null) return;
        try { Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The WebView was closed between the check and the call.
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_viewModel is not null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            _ = _viewModel.OpenPathAsync(files[0]);
    }

    /// <summary>When the inline rename editor appears, put the caret in it with the old name selected.</summary>
    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            box.Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }
}
