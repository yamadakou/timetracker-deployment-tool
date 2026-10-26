using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using TTDeployTool.Controller.Desktop.Core.Diagnostics;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Services;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// アプリの起動。画面の状態と操作（<see cref="MainViewModel"/>）に、OS の機能（WAM でのサインイン、ダイアログ、既定のアプリで開く、通知）を渡す。
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

        // 通知を押したときに、そのジョブを開く（Register の前に NotificationInvoked を登録する。docs/design/desktop-jobs.md の 5.1）
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        AppNotificationManager.Default.Register();

        _window = new MainWindow();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
#if UITEST_SIGNIN
        // UI テストの System Test のビルドだけ、Azure CLI のサインインを使う（Store に出すパッケージには入らない）
        appLogger.LogWarning("UI テスト用のビルドのため、Azure CLI のサインインを使います");
        ISignInService signIn = new AzureCliSignInService();
#else
        ISignInService signIn = new WamSignInService(hwnd, DesktopSettings.ClientId, DesktopSettings.DataDirectory);
#endif
        var viewModel = new MainViewModel(
            signIn,
            new AzureClients(),
            new DialogService(_window, hwnd),
            new SystemLauncher(),
            TimeProvider.System,
            log,
            preferences: new FilePreferences(Path.Combine(DesktopSettings.DataDirectory, "preferences.json")),
            notifications: new AppNotificationService(),
            clipboard: new ClipboardService(),
            startupTask: new StartupTaskService());
        // 常駐中に 2 つ目を起動したら、隠したウィンドウを出す（Program.cs）
        Program.Reactivated += (_, _) => _window.DispatcherQueue.TryEnqueue(_window.ShowFromTray);
        _window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            AppNotificationManager.Default.Unregister();
            _azureEvents?.Dispose();
        };
        // Windows のサインイン時に起動されたら、ウィンドウを出さずに通知領域だけで動く（docs/design/desktop-jobs.md の 6.5。Register の後に読む）
        var atStartup = AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        appLogger.LogInformation("起動の種類: {Kind}", atStartup ? "サインイン時の起動" : "通常");
        _window.Initialize(viewModel, hwnd, atStartup);
        if (!atStartup)
            _window.Activate();
    }

    // 通知を押した（アプリが動いている間。終わった後に押された場合は、ジョブが残っていないため普通に起動する）
    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var arguments = new Dictionary<string, string>(args.Arguments);
        _window?.DispatcherQueue.TryEnqueue(() => _window.ShowFromNotification(arguments));
    }
}
