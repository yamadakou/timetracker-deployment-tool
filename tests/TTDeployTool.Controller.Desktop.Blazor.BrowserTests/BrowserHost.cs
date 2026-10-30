using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests;

/// <summary>
/// テスト用のホストを、空いているポートと新しいアプリのデータのフォルダーで起動し、Chromium で開く。<see cref="RestartAsync"/> は、同じフォルダーで起動し直す
/// （保存した列の配置で始まることを確かめる）。Chromium は Playwright の既定の場所のもの（環境変数 TT_CHROMIUM_PATH があれば、そのファイル）。
/// </summary>
public sealed class BrowserHost : IAsyncDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "ttdeploytool-blazor-browser", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private Process? _process;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public string Url { get; private set; } = "";

    public IPage Page { get; private set; } = default!;

    public static async Task<BrowserHost> StartAsync(int width = 1600, int height = 1000)
    {
        var host = new BrowserHost();
        await host.LaunchHostAsync();
        host._playwright = await Playwright.CreateAsync();
        host._browser = await host._playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = Environment.GetEnvironmentVariable("TT_CHROMIUM_PATH") is { Length: > 0 } path ? path : null,
        });
        await host.OpenAsync(width, height);
        return host;
    }

    /// <summary>最後にコピーした文字。</summary>
    public Task<string> ClipboardAsync() => _http.GetStringAsync($"{Url}/test/clipboard");

    /// <summary>ホストを止めて、同じデータのフォルダーで起動し直し、開き直す（アプリの起動し直しの代わり）。</summary>
    public async Task RestartAsync()
    {
        var size = Page.ViewportSize!;
        await Page.CloseAsync();
        StopHost();
        await LaunchHostAsync();
        await OpenAsync(size.Width, size.Height);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
        StopHost();
        _http.Dispose();
        if (Directory.Exists(_data))
            Directory.Delete(_data, recursive: true);
    }

    private async Task OpenAsync(int width, int height)
    {
        Page = await _browser!.NewPageAsync(new BrowserNewPageOptions { Locale = "ja-JP", ViewportSize = new ViewportSize { Width = width, Height = height } });
        await Page.GotoAsync(Url);
        await Page.Locator("tbody tr").First.WaitForAsync();
    }

    private async Task LaunchHostAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Url = $"http://127.0.0.1:{port}";
        // ホストは、このテストと同じ構成（Debug・Release）でビルドしたもの
        var dll = Path.Combine(AppContext.BaseDirectory.Replace("Blazor.BrowserTests", "Blazor.TestHost"), "TTDeployTool.Controller.Desktop.Blazor.TestHost.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { dll, "--urls", Url, "--data", _data })
            start.ArgumentList.Add(arg);
        // ビルドの出力から動かすため、静的なファイル（_content・_framework）の場所の一覧を使う
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        _process = Process.Start(start)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        for (var i = 0; i < 150; i++)
        {
            try
            {
                using var response = await _http.GetAsync(Url);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            if (_process.HasExited)
                throw new InvalidOperationException($"テスト用のホストが終了しました（終了コード {_process.ExitCode}）: {dll}");
            await Task.Delay(200);
        }
        throw new TimeoutException($"テスト用のホストが起動しません: {Url}");
    }

    private void StopHost()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
        _process?.Dispose();
        _process = null;
    }
}
