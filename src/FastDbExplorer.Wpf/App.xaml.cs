using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FastDbExplorer.Application.Abstractions;
using FastDbExplorer.Infrastructure;
using FastDbExplorer.Infrastructure.CoordinateLayers;
using FastDbExplorer.Infrastructure.Maps;
using FastDbExplorer.Infrastructure.Monitoring;
using FastDbExplorer.Wpf.Localization;
using FastDbExplorer.Wpf.Services;
using FastDbExplorer.Wpf.ViewModels;
using FastDbExplorer.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FastDbExplorer.Wpf;

// Note: "Application" would clash with the FastDbExplorer.Application namespace, so the base type is fully qualified.
public partial class App : System.Windows.Application
{
    private const int StartupSteps = 3;
    // The splash stays at least this long, otherwise it would only flash on a fast machine.
    private static readonly TimeSpan MinSplashTime = TimeSpan.FromMilliseconds(1200);

    private IHost? _host;
    private ThemeService? _theme;

    /// <summary>Appends an unhandled exception to %LocalAppData%\FastDbExplorer\logs\crash.log. Never throws.</summary>
    internal static void WriteCrashLog(Exception exception)
    {
        try
        {
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastDbExplorer", "logs");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never make the failure worse.
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            MessageBox.Show(args.Exception.Message, "FastDbExplorer", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var splash = new SplashWindow();
        splash.Show();
        var shownFor = Stopwatch.StartNew();

        try
        {
            splash.Report(1, StartupSteps, SplashStrings.StepServices);
            // Let the splash paint before the (synchronous) service setup starts.
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<IDatabaseMetadataService, SqlServerMetadataService>();
            builder.Services.AddSingleton<IConnectionProfileStore>(_ => new JsonConnectionProfileStore());
            builder.Services.AddSingleton<ICredentialVault>(_ => new DpapiCredentialVault());
            builder.Services.AddSingleton<IMapSourceFactory, MapSourceFactory>();
            builder.Services.AddSingleton<IMapSettingsStore>(_ => new JsonMapSettingsStore());
            builder.Services.AddSingleton<IExcelImportService, ExcelCoordinateImportService>();
            builder.Services.AddSingleton<ICoordinateLayerStore>(_ => new JsonCoordinateLayerStore());
            builder.Services.AddSingleton<IFileDialogService, FileDialogService>();
            builder.Services.AddSingleton<IMonitorStore>(_ => new JsonMonitorStore());
            builder.Services.AddSingleton<IWatermarkReader, SqlServerWatermarkReader>();
            builder.Services.AddSingleton<IReportWriter, XlsxReportWriter>();
            builder.Services.AddSingleton<IQueryReportWriter>(sp => (IQueryReportWriter)sp.GetRequiredService<IReportWriter>());
            builder.Services.AddSingleton<IMonitorCheckService, MonitorCheckService>();
            builder.Services.AddSingleton<IFolderPickerService, FolderPickerService>();
            builder.Services.AddSingleton<IAlertNotifier, AlertNotifier>();
            builder.Services.AddSingleton<MonitoringViewModelFactory>();
            builder.Services.AddSingleton<ConnectionViewModel>();
            builder.Services.AddSingleton<MapViewModel>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();
            _host = builder.Build();

            splash.Report(2, StartupSteps, SplashStrings.StepStarting);
            await _host.StartAsync();

            splash.Report(3, StartupSteps, SplashStrings.StepInterface);
            _theme = new ThemeService();
            _theme.Start();

            var remaining = MinSplashTime - shownFor.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

            // Explicit shutdown mode lets the splash close before the main window is shown.
            splash.Close();

            var main = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
        catch (Exception ex)
        {
            if (splash.IsVisible) splash.Close();
            MessageBox.Show(ex.Message, "FastDbExplorer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _theme?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
