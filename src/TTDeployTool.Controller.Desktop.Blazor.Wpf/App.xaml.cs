using System.Windows;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// 試作の起動。「--check &lt;報告のファイル&gt;」なら、窓を出して Windows との連携を確かめ（<see cref="IntegrationCheck"/>）、結果を書いて終了する。
/// MSIX から起動すると引数を渡せないため、アプリのデータのフォルダーに <see cref="CheckRequest"/> があれば、同じ確認をして <see cref="CheckReport"/> に書く。
/// </summary>
public partial class App : System.Windows.Application
{
    public const string CheckRequest = "check-request";

    public const string CheckReport = "check-report.json";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 処理されなかった例外を、アプリのデータの crash.log に書く（Microsoft Learn の Blazor Hybrid の WPF の勧め）
        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
            File.AppendAllText(Path.Combine(HostSettings.DataDirectory, "crash.log"), $"{DateTimeOffset.Now:O} {error.ExceptionObject}{Environment.NewLine}");
        var window = new MainWindow();
        var check = Array.IndexOf(e.Args, "--check");
        var request = Path.Combine(HostSettings.DataDirectory, CheckRequest);
        var report = check >= 0 && check + 1 < e.Args.Length ? e.Args[check + 1]
            : File.Exists(request) ? Path.Combine(HostSettings.DataDirectory, CheckReport) : null;
        File.Delete(request);
        if (report != null)
        {
            window.Loaded += async (_, _) =>
            {
                await IntegrationCheck.WriteAsync(window, report);
                Shutdown();
            };
        }
        window.Show();
    }
}
