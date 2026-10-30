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
        // 処理されなかった例外を crash.log に書く（Microsoft Learn の Blazor Hybrid の WPF の勧め。アプリのデータのフォルダーが分からなくても、一時フォルダーに書く）
        AppDomain.CurrentDomain.UnhandledException += (_, error) => Crash(error.ExceptionObject);
        DispatcherUnhandledException += (_, error) => Crash(error.Exception);
        var check = Array.IndexOf(e.Args, "--check");
        var argument = check >= 0 && check + 1 < e.Args.Length ? e.Args[check + 1] : null;
        try
        {
            var request = Path.Combine(HostSettings.DataDirectory, CheckRequest);
            var report = argument ?? (File.Exists(request) ? Path.Combine(HostSettings.DataDirectory, CheckReport) : null);
            File.Delete(request);
            var window = new MainWindow();
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
