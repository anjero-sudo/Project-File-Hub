using Microsoft.UI.Xaml;
using ProjectFileHub.App.Diagnostics;
using ProjectFileHub.App.WindowsIntegration;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace ProjectFileHub.App;

public partial class App : WinUIApplication
{
    private readonly SingleInstanceCoordinator _singleInstanceCoordinator = new();
    private Window? _window;

    public App()
    {
        if (_singleInstanceCoordinator.IsPrimary)
        {
            AppDiagnostics.StartSession();
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AppDiagnostics.Log($"AppDomain unhandled exception | {eventArgs.ExceptionObject}");
        UnhandledException += (_, eventArgs) =>
            AppDiagnostics.Log("WinUI unhandled exception", eventArgs.Exception);
        InitializeComponent();
        AppDiagnostics.Log("App.InitializeComponent completed");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var launchedForStartup = args.Arguments.Contains("--startup", StringComparison.OrdinalIgnoreCase);
        if (!_singleInstanceCoordinator.IsPrimary)
        {
            if (!launchedForStartup)
            {
                _singleInstanceCoordinator.SignalPrimary();
            }

            _singleInstanceCoordinator.Dispose();
            Exit();
            return;
        }

        AppDiagnostics.Log("OnLaunched entered");
        _window = new MainWindow(launchedForStartup);
        AppDiagnostics.Log("MainWindow constructed");
        _singleInstanceCoordinator.StartListening(() =>
            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.RestoreFromTray();
                }
            }));
        _window.Closed += (_, _) =>
        {
            _singleInstanceCoordinator.Dispose();
            _window = null;
        };
        _window.Activate();
        AppDiagnostics.Log("MainWindow activated");
    }
}
