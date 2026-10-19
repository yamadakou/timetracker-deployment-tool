using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using TTDeployTool.Controller.Desktop.Core.Diagnostics;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Services;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// アプリの起動。画面の状態と操作（<see cref="MainViewModel"/>）に、OS の機能（WAM でのサインイン、ダイアログ、既定のアプリで開く）を渡す。
/// </summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _window;
    private IDisposable? _azureEvents;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 診断用のログ（詳細設計 7.2）。Azure SDK のイベントと、処理されなかった例外も書く
        var log = new DiagnosticLog(Path.Combine(DesktopSettings.DataDirectory, "logs"), TimeProvider.System);
        log.DeleteOld();
        _azureEvents = log.ForwardAzureEvents();
        var appLogger = log.CreateLogger("TTDeployTool.Desktop.App");
        UnhandledException += (_, e) => appLogger.LogCritical(e.Exception, "処理されなかった例外");
        appLogger.LogInformation("起動しました（版 {Version}）", typeof(App).Assembly.GetName().Version);

        _window = new MainWindow();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var viewModel = new MainViewModel(
            new WamSignInService(hwnd, DesktopSettings.ClientId, DesktopSettings.DataDirectory),
            new AzureClients(),
            new DialogService(_window, hwnd),
            new SystemLauncher(),
            TimeProvider.System,
            log);
        _window.Closed += (_, _) => _azureEvents?.Dispose();
        _window.Initialize(viewModel, hwnd);
        _window.Activate();
    }
}
