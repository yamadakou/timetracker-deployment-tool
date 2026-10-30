using System.Windows;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// 試作の起動（WinUI 3 のアプリの App.xaml.cs と同じ順: 通知を押したときの受け口を登録してから、起動の種類を読む）。
/// 「--check &lt;報告のファイル&gt;」なら、窓を出して Windows との連携を確かめ（<see cref="IntegrationCheck"/>）、結果を書いて終了する。
/// MSIX から起動すると引数を渡せないため、アプリのデータのフォルダーに <see cref="CheckRequest"/> があれば、同じ確認をして <see cref="CheckReport"/> に書く
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
            // 通知を押したときに、ウィンドウを出す（Register の前に NotificationInvoked を登録する。WinUI のアプリと同じ）
            AppNotificationManager.Default.NotificationInvoked += (_, args) => Dispatcher.InvokeAsync(() =>
            {
                var arguments = string.Join("&", args.Arguments.Select(a => $"{a.Key}={a.Value}"));
                InvokedNotifications.Add(arguments);
                // 人が試すとき（確認の自動の実行でないとき）は、どの通知から開いたかを出す
                if (_window != null && !_checking)
                    ManualCheck.Opened(_window, "Windows App SDK", arguments);
                else
                    _window?.ShowFromTray();
            });
            AppNotificationManager.Default.Register();
            ActivationKind = AppInstance.GetCurrent().GetActivatedEventArgs().Kind.ToString();
            Program.Reactivated += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                Reactivations++;
                _window?.ShowFromTray();
            });
            Exit += (_, _) => AppNotificationManager.Default.Unregister();

            var request = Path.Combine(HostSettings.DataDirectory, CheckRequest);
            var waitForReactivation = File.Exists(request) && File.ReadAllText(request).Trim() == "reactivate";
            var report = argument ?? (File.Exists(request) ? Path.Combine(HostSettings.DataDirectory, CheckReport) : null);
            File.Delete(request);
            _checking = report != null;
            _window = new MainWindow();
            if (report != null)
            {
                _window.Loaded += async (_, _) =>
                {
                    await IntegrationCheck.WriteAsync(_window, report, waitForReactivation);
                    _window.Exit();
                };
            }
            _window.Show();
        }
        catch (Exception ex) when (argument != null)
        {
            // 確認のときは、起動できなかった理由を報告のファイルに書いて終わる
            File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(new { startupError = ex.ToString() }));
            Shutdown(1);
        }
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
