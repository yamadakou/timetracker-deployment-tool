using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using TTDeployTool.Controller.Desktop.Blazor.Shell;
using TTDeployTool.Controller.Desktop.Core.Diagnostics;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Services;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// アプリの起動（WinUI 3 のアプリの App.xaml.cs と同じ部品と順序。docs/design/desktop/desktop-blazor-hybrid.md の 16）: 診断用のログ、通知を押したときの受け口、
/// <see cref="MainViewModel"/>（WAM のサインイン・Azure・通知・サインイン時の起動・DPAPI・設定と記録のファイル）、ウィンドウ。テスト用のビルド（/p:UiTestMock=true）だけ、
/// サインインと Azure などを Fixtures のモックにする。Windows のサインイン時に起動されたら、ウィンドウを出さずに通知領域だけで動く。
/// 「--check &lt;報告のファイル&gt;」か、アプリのデータのフォルダーの <see cref="CheckRequest"/> があれば、Windows との連携を確かめて（<see cref="IntegrationCheck"/>）、結果を書いて終了する
/// （中身が「reactivate」なら、2 つ目の起動が渡されるのも待つ）。
/// </summary>
public partial class App : System.Windows.Application
{
    public const string CheckRequest = "check-request";

    public const string CheckReport = "check-report.json";

    /// <summary>2 つ目の起動を待っている印（CI が見て、2 つ目を起動する）。</summary>
    public const string CheckWaiting = "check-waiting";

    private MainWindow? _window;
    private bool _checking;
    private IDisposable? _azureEvents;

    /// <summary>起動の種類（通常・サインイン時の起動・通知から）。</summary>
    public static string ActivationKind { get; private set; } = "";

    /// <summary>押された通知の引数（確認で読む）。</summary>
    public static List<string> InvokedNotifications { get; } = [];

    /// <summary>2 つ目の起動が渡された回数（確認で読む）。</summary>
    public static int Reactivations { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 処理されなかった例外を crash.log に書く（Microsoft Learn の Blazor Hybrid の WPF の勧め。アプリのデータのフォルダーが分からなくても、一時フォルダーに書く）
        AppDomain.CurrentDomain.UnhandledException += (_, error) => Crash(error.ExceptionObject);
        DispatcherUnhandledException += (_, error) => Crash(error.Exception);
        var check = Array.IndexOf(e.Args, "--check");
        var argument = check >= 0 && check + 1 < e.Args.Length ? e.Args[check + 1] : null;
        try
        {
            // 診断用のログ（WinUI のアプリと同じ。Azure SDK のイベントと、処理されなかった例外も書く）
            var log = new DiagnosticLog(Path.Combine(HostSettings.DataDirectory, "logs"), TimeProvider.System);
            log.DeleteOld();
            _azureEvents = log.ForwardAzureEvents();
            var appLogger = log.CreateLogger("TTDeployTool.Desktop.App");
            AppDomain.CurrentDomain.UnhandledException += (_, error) => appLogger.LogCritical(error.ExceptionObject as Exception, "処理されなかった例外（AppDomain）");
            DispatcherUnhandledException += (_, error) => appLogger.LogCritical(error.Exception, "処理されなかった例外");
            TaskScheduler.UnobservedTaskException += (_, error) => appLogger.LogError(error.Exception, "待たれなかったタスクの例外");
            appLogger.LogInformation("起動しました（版 {Version}・Blazor Hybrid）", typeof(App).Assembly.GetName().Version);

            // 通知を押したときに、そのジョブを開く（Register の前に NotificationInvoked を登録する。docs/design/desktop/desktop-jobs.md の 5.1）
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                var arguments = new Dictionary<string, string>(args.Arguments);
                Dispatcher.InvokeAsync(() =>
                {
                    InvokedNotifications.Add(string.Join("&", arguments.Select(a => $"{a.Key}={a.Value}")));
                    _window?.ShowFromNotification(arguments, open: !_checking);
                });
            };
            AppNotificationManager.Default.Register();
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs().Kind;
            ActivationKind = activation.ToString();
            // 常駐中に 2 つ目を起動したら、隠したウィンドウを出す（Program.cs）
            Program.Reactivated += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                Reactivations++;
                _window?.ShowFromTray();
            });

            var request = Path.Combine(HostSettings.DataDirectory, CheckRequest);
            var waitForReactivation = File.Exists(request) && File.ReadAllText(request).Trim() == "reactivate";
            var report = argument ?? (File.Exists(request) ? Path.Combine(HostSettings.DataDirectory, CheckReport) : null);
            File.Delete(request);
            _checking = report != null;

            // サインイン（WAM）にウィンドウのハンドルを渡すため、先にウィンドウを作る（WinUI のアプリと同じ順）
            _window = new MainWindow();
            var dialogs = new BlazorDialogService(PickFolderAsync);
            var viewModel = CreateViewModel(dialogs, log, appLogger, _window.Handle);
            // Windows のサインイン時に起動されたら、ウィンドウを出さずに通知領域だけで動く（docs/design/desktop/desktop-jobs.md の 6.5）
            var atStartup = activation == ExtendedActivationKind.StartupTask;
            appLogger.LogInformation("起動の種類: {Kind}", atStartup ? "サインイン時の起動" : "通常");
            var session = new DesktopSession(viewModel, dialogs, new ClipboardService(), vm => vm.StartAsync(atStartup));
            _window.Initialize(session);
            Exit += (_, _) =>
            {
                viewModel.Dispose();
                AppNotificationManager.Default.Unregister();
                _azureEvents?.Dispose();
            };
            if (report != null)
            {
                _window.Loaded += async (_, _) =>
                {
                    await IntegrationCheck.WriteAsync(_window, report, waitForReactivation);
                    _window.Exit();
                };
            }
            // サインイン時の起動は、ウィンドウを出さずに始めの処理だけ動かす（画面は、通知領域から開いたときに描く。始めの処理は 1 回だけ）
            if (atStartup && report == null)
                _ = session.StartAsync();
            else
                _window.Show();
        }
        catch (Exception ex) when (argument != null)
        {
            // 確認のときは、起動できなかった理由を報告のファイルに書いて終わる
            File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(new { startupError = ex.ToString() }));
            Shutdown(1);
        }
    }

    // WinUI のアプリと同じ部品（テスト用のビルドだけ Fixtures のモック）
    private static MainViewModel CreateViewModel(BlazorDialogService dialogs, DiagnosticLog log, ILogger appLogger, IntPtr hwnd)
    {
#if UITEST_MOCK
        // テスト用のビルドだけ、モックのサインインと Azure を使う（アプリのデータの uitest-fixture.json。docs/design/desktop/desktop-ui-tests.md の 6.2）
        appLogger.LogWarning("UI テスト用のビルドのため、モックのサインインと Azure を使います");
        _ = hwnd;
        return Fixtures.FixtureApp.CreateViewModel(HostSettings.DataDirectory, dialogs, new ClipboardService(), TimeProvider.System,
            loggers: log, secrets: new DpapiSecretProtector());
#else
        _ = appLogger;
        return new MainViewModel(
            new WamSignInService(hwnd, HostSettings.ClientId, HostSettings.DataDirectory),
            new AzureClients(),
            dialogs,
            new SystemLauncher(),
            TimeProvider.System,
            log,
            preferences: new FilePreferences(Path.Combine(HostSettings.DataDirectory, "preferences.json")),
            notifications: new AppNotificationService(),
            clipboard: new ClipboardService(),
            startupTask: new StartupTaskService(),
            secrets: new DpapiSecretProtector(),
            history: new FileJobHistory(Path.Combine(HostSettings.DataDirectory, "job-history.json")));
#endif
    }

    // フォルダーの選択（WPF の OpenFolderDialog。ウィンドウの上に出す）
    private Task<string?> PickFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "保存先のフォルダーを選ぶ" };
        return Task.FromResult(dialog.ShowDialog(_window) == true ? dialog.FolderName : null);
    }

    private static void Crash(object exception)
    {
        string directory;
        try
        {
            directory = HostSettings.DataDirectory;
        }
        catch (Exception)
        {
            directory = Path.GetTempPath();
        }
        File.AppendAllText(Path.Combine(directory, "crash.log"), $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
    }
}
