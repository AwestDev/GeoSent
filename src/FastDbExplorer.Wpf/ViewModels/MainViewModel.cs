using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDbExplorer.Application.Abstractions;
using FastDbExplorer.Domain;
using FastDbExplorer.Wpf.Localization;
using FastDbExplorer.Wpf.Services;

namespace FastDbExplorer.Wpf.ViewModels;

/// <summary>
/// Owns the app shell: a persistent navigation rail with five sections (Home, Connections, Explorer, Map, Monitoring).
/// Every section keeps its state while the user works in another one, so there is no "Back" button.
/// Connections is a separate profile-management area; Home never hosts raw connection fields.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ConnectionViewModel _connection;
    private readonly MapViewModel _map;
    private readonly IDatabaseMetadataService _metadata;
    private readonly IFileDialogService _dialogs;
    private readonly IQueryReportWriter _queryReportWriter;
    private readonly MonitoringViewModelFactory _monitoringFactory;
    private readonly ConnectPromptViewModel _explorerPrompt;
    private readonly ConnectPromptViewModel _monitoringPrompt;
    private readonly ConnectPromptViewModel _homePrompt;
    private readonly NavItemViewModel _connectionsNav;
    private readonly NavItemViewModel _monitoringNav;
    private MonitoringViewModel? _monitoring;
    private HomeViewModel? _home;
    private NotifyCollectionChangedEventHandler? _alertsHandler;
    private AppSection _section = AppSection.Home;
    private int _unreadAlerts;

    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(ServerName))]
    private ExplorerViewModel? _explorer;

    public MainViewModel(
        ConnectionViewModel connection, MapViewModel map, IDatabaseMetadataService metadata,
        MonitoringViewModelFactory monitoringFactory, IFileDialogService dialogs, IQueryReportWriter queryReportWriter)
    {
        _connection = connection;
        _map = map;
        _metadata = metadata;
        _monitoringFactory = monitoringFactory;
        _dialogs = dialogs;
        _queryReportWriter = queryReportWriter;

        _connectionsNav = new NavItemViewModel(AppSection.Connections, ShellStrings.NavConnections, "\uE716", 2);
        _monitoringNav = new NavItemViewModel(AppSection.Monitoring, ShellStrings.NavMonitoring, "\uE823", 5);
        NavItems =
        [
            new NavItemViewModel(AppSection.Home, ShellStrings.NavHome, "\uE80F", 1),
            _connectionsNav,
            new NavItemViewModel(AppSection.Explorer, ShellStrings.NavExplorer, "\uE80A", 3),
            new NavItemViewModel(AppSection.Map, ShellStrings.NavMap, "\uE774", 4),
            _monitoringNav
        ];

        _explorerPrompt = new ConnectPromptViewModel(
            ShellStrings.ExplorerPromptTitle, ShellStrings.ExplorerPromptBody, "\uE80A", () => Navigate(AppSection.Connections));
        _monitoringPrompt = new ConnectPromptViewModel(
            ShellStrings.MonitoringPromptTitle, ShellStrings.MonitoringPromptBody, "\uE823", () => Navigate(AppSection.Connections));
        _homePrompt = new ConnectPromptViewModel(
            ShellStrings.HomePromptTitle, ShellStrings.HomePromptBody, "\uE716", () => Navigate(AppSection.Connections));

        _connection.Connected += OnConnected;
        _connection.ProfileDeleted += OnProfileDeleted;
        _connection.Profiles.CollectionChanged += (_, _) => RefreshActiveProfile();
        _map.BackRequested += () => Navigate(AppSection.Home);

        Navigate(AppSection.Connections);
    }

    /// <summary>Raised when the Ctrl+K palette should open; the window (view layer) shows it.</summary>
    public event Action<CommandPaletteViewModel>? PaletteRequested;

    public IReadOnlyList<NavItemViewModel> NavItems { get; }
    public IReadOnlyList<SavedConnection> Profiles => _connection.Profiles;
    public ConnectionViewModel ConnectionManager => _connection;

    public bool IsConnected => Explorer is not null;
    public string ServerName => Explorer?.ServerName ?? "";

    [ObservableProperty]
    private SavedConnection? _activeProfile;

    public Task<bool> SwitchProfileAsync(SavedConnection profile) => _connection.ConnectProfileAsync(profile);

    public async Task InitializeAsync()
    {
        // The last used map opens in the background at start-up, so it is ready when the user opens the map screen.
        var lastMap = _map.LoadLastMapAsync();
        await _connection.InitializeAsync();
        await lastMap;
    }

    [RelayCommand]
    private void Navigate(AppSection section)
    {
        _section = section;
        foreach (var item in NavItems) item.IsSelected = item.Section == section;

        if (section == AppSection.Monitoring)
        {
            _unreadAlerts = 0;
            _monitoringNav.Badge = 0;
        }

        CurrentView = ResolveView(section);
    }

    [RelayCommand]
    private void Disconnect()
    {
        // The explorer cancels its running query and raises Disconnected, which ends the session below.
        Explorer?.DisconnectCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenPalette() => PaletteRequested?.Invoke(CreatePalette());

    private CommandPaletteViewModel CreatePalette()
    {
        var items = new List<PaletteItem>();

        foreach (var nav in NavItems)
        {
            var section = nav.Section;
            items.Add(new PaletteItem(ShellStrings.KindSection, nav.Title, "", nav.Glyph, () => Navigate(section)));
        }

        var explorer = Explorer;
        if (explorer is not null)
        {
            items.Add(new PaletteItem(ShellStrings.KindAction, Strings.Disconnect, "", "\uE7E8", Disconnect));

            if (_monitoring is not null)
            {
                foreach (var monitor in _monitoring.Monitors)
                    items.Add(new PaletteItem(ShellStrings.KindMonitor, monitor.Name, monitor.Target, "\uE823", () => Navigate(AppSection.Monitoring)));
            }

            // SourceCollection = every table of the active database, even if the sidebar search box has filtered the list.
            foreach (var table in explorer.Tables.SourceCollection.Cast<TableInfo>())
            {
                var picked = table;
                items.Add(new PaletteItem(ShellStrings.KindTable, picked.FullName, ShellStrings.RowsApprox(picked.ApproxRows), "\uE80A", () =>
                {
                    Navigate(AppSection.Explorer);
                    explorer.SelectedTable = picked;
                }));
            }
        }

        return new CommandPaletteViewModel(items);
    }

    private object ResolveView(AppSection section)
    {
        if (section == AppSection.Explorer)
        {
            if (Explorer is not null) return Explorer;
            return _explorerPrompt;
        }

        if (section == AppSection.Monitoring)
        {
            if (_monitoring is not null) return _monitoring;
            return _monitoringPrompt;
        }

        if (section == AppSection.Map) return _map;

        if (section == AppSection.Connections) return _connection;

        if (_home is not null) return _home;
        return _homePrompt;
    }

    private void OnConnected(ConnectionSettings settings, IReadOnlyList<DatabaseInfo> databases, SavedConnection profile)
    {
        var destination = IsConnected ? _section : AppSection.Home;
        EndSession();
        ActiveProfile = profile;

        var explorer = new ExplorerViewModel(_metadata, settings, databases, _dialogs, _queryReportWriter);
        // Each send adds its own result set (marked with where it came from); earlier sets stay until the user removes them.
        explorer.ShowSelectedRowsOnMapRequested += (tableName, points) =>
        {
            _map.ShowQueryResultLayer(tableName, points, MapLayerKind.Explorer);
            Navigate(AppSection.Map);
        };
        var monitoring = _monitoringFactory.Create(settings, databases);
        monitoring.ShowSelectedRowsOnMapRequested += (tableName, points) =>
        {
            _map.ShowQueryResultLayer(tableName, points, MapLayerKind.Monitoring);
            Navigate(AppSection.Map);
        };
        // Live map: matching records update one layer per monitor on the map; the user stays on the monitoring page.
        monitoring.LiveMapPointsRequested += (monitorId, monitorName, points) =>
            _map.UpsertLiveMonitorPoints(monitorId, monitorName, points);
        monitoring.LiveMapRemoveRequested += monitorId => _map.RemoveLiveMonitorLayer(monitorId);
        _monitoring = monitoring;

        monitoring.BackRequested += () => Navigate(AppSection.Home);
        explorer.Disconnected += () =>
        {
            EndSession();
            Navigate(AppSection.Home);
        };

        // New alerts raise a badge on the Monitoring item until the user opens that section.
        _alertsHandler = (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || _section == AppSection.Monitoring) return;
            _unreadAlerts++;
            _monitoringNav.Badge = _unreadAlerts;
        };
        monitoring.Alerts.CollectionChanged += _alertsHandler;

        _home = new HomeViewModel(settings.Server, databases.Count, monitoring, Navigate, Disconnect);
        Explorer = explorer;

        Navigate(destination);
        _ = monitoring.InitializeAsync();
    }

    private void OnProfileDeleted(SavedConnection profile)
    {
        if (ActiveProfile?.Id != profile.Id) return;
        EndSession();
        Navigate(AppSection.Connections);
    }

    private void RefreshActiveProfile()
    {
        if (ActiveProfile is null) return;
        var saved = _connection.Profiles.FirstOrDefault(profile => profile.Id == ActiveProfile.Id);
        if (saved is not null) ActiveProfile = saved;
    }

    private void EndSession()
    {
        Explorer?.Shutdown();
        _map.ClearQueryResultLayer(); // the rows belong to the session that ends here: every result set leaves the map
        if (_monitoring is not null)
        {
            if (_alertsHandler is not null) _monitoring.Alerts.CollectionChanged -= _alertsHandler;
            _monitoring.Dispose(); // stops all scheduled checks (the password leaves memory with the session)
        }

        _home?.Dispose();
        _monitoring = null;
        _home = null;
        _alertsHandler = null;
        Explorer = null;
        ActiveProfile = null;
        _unreadAlerts = 0;
        _monitoringNav.Badge = 0;
    }
}
