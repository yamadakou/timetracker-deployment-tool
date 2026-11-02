using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests;

/// <summary>
/// テスト用のホストを、空いているポートと新しいアプリのデータのフォルダーで起動し、Chromium で開く。<see cref="RestartAsync"/> は、同じフォルダーで起動し直す
/// （保存した列の配置で始まることを確かめる）。モックのデータは見本（--sample: デプロイ 45 件・終わったジョブの記録 30 件）。Chromium は Playwright の既定の場所のもの（環境変数 TT_CHROMIUM_PATH があれば、そのファイル）。
/// </summary>
public sealed class BrowserHost : IAsyncDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "ttdeploytool-blazor-browser", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private Process? _process;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public string Url { get; private set; } = "";

    /// <summary>アプリのデータのフォルダー（モックの通知などの記録・Dry-run の保存先 dryrun を含む）。</summary>
    public string DataDirectory => _data;

    public IPage Page { get; private set; } = default!;

    private bool _signIn = true;
    private ICDPSession? _coverage;
    private bool _sample = true;

    /// <param name="signIn">開いた後に、モックのアカウントでサインインしてデプロイの一覧を開く（false ならサインインの画面のまま）</param>
    /// <param name="prepare">起動の前に、アプリのデータのフォルダーにモックのデータなどを置く（あれば見本〔--sample〕は使わない。WinUI 3 版の UI テストの WithFixture と同じ）</param>
    public static async Task<BrowserHost> StartAsync(int width = 1600, int height = 1000, bool signIn = true, Action<string>? prepare = null)
    {
        var host = new BrowserHost { _signIn = signIn, _sample = prepare == null };
        Directory.CreateDirectory(host._data);
        prepare?.Invoke(host._data);
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

    /// <summary>ウィンドウを閉じたときの動き（Exit か Hide。WPF のホストと同じ Core の判断）。</summary>
    public Task<string> CloseActionAsync() => _http.GetStringAsync($"{Url}/test/close");

    /// <summary>Dry-run の保存先（フォルダーの選択で返すフォルダー）。</summary>
    public string DryRunFolder => Path.Combine(_data, "dryrun");

    /// <summary>アプリが書いた、外の機能の記録（通知・開いたフォルダーと URL・サインイン時の起動）。</summary>
    public IReadOnlyList<(string Kind, string Text)> Events() =>
        Fixtures.FixtureEvents.Read(Path.Combine(_data, Fixtures.FixtureEvents.FileName));

    /// <summary>ホストを止めて、同じデータのフォルダーで起動し直し、開き直す（アプリの起動し直しの代わり。<paramref name="signIn"/> なら、またサインインする）。</summary>
    public async Task RestartAsync(bool? signIn = null)
    {
        var size = Page.ViewportSize!;
        await SaveCoverageAsync();
        await Page.CloseAsync();
        StopHost();
        await LaunchHostAsync();
        _signIn = signIn ?? _signIn;
        await OpenAsync(size.Width, size.Height);
    }

    public async ValueTask DisposeAsync()
    {
        await SaveCoverageAsync();
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
        await StartCoverageAsync();
        await Page.GotoAsync(Url);
        await Page.Locator("[data-id='Panel.Target']").WaitForAsync();
        if (_signIn)
            await SignInAndOpenDeploymentsAsync(Page);
    }

    // 画面の JavaScript（table.js）の網羅率（環境変数 TT_JS_COVERAGE_DIR があるときだけ。Chromium の CDP の Profiler で、どのブロックを通ったかを取り、
    // ページを閉じる前にそのフォルダーに書く。.github/scripts/js-coverage.py がまとめて、通っていないブロックを数える。オーナーの依頼、2026-11-02）
    private async Task StartCoverageAsync()
    {
        if (Environment.GetEnvironmentVariable("TT_JS_COVERAGE_DIR") is not { Length: > 0 })
            return;
        _coverage = await Page.Context.NewCDPSessionAsync(Page);
        await _coverage.SendAsync("Profiler.enable");
        await _coverage.SendAsync("Profiler.startPreciseCoverage", new Dictionary<string, object> { ["callCount"] = true, ["detailed"] = true });
    }

    private async Task SaveCoverageAsync()
    {
        if (_coverage == null || Environment.GetEnvironmentVariable("TT_JS_COVERAGE_DIR") is not { Length: > 0 } directory)
            return;
        var result = await _coverage.SendAsync("Profiler.takePreciseCoverage");
        _coverage = null;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{Guid.NewGuid():N}.json"), result?.GetRawText() ?? "{}");
    }

    /// <summary>モックのアカウントでサインインし、デプロイの一覧を開いて行が出るまで待つ（読み込まれていなければ「読み込む」を押す）。</summary>
    public static async Task SignInAndOpenDeploymentsAsync(IPage page)
    {
        await page.Locator("[data-id='SignIn.SignIn']").ClickAsync();
        await Assertions.Expect(page.Locator("[data-id='SignIn.Account']")).ToHaveTextAsync("mock-user@example.com");
        await page.Locator("[data-id='Nav.Deployments']").ClickAsync();
        try
        {
            await page.Locator("tbody tr").First.WaitForAsync(new() { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
            await page.Locator("[data-id='Deployments.Load']").ClickAsync();
            await page.Locator("tbody tr").First.WaitForAsync();
        }
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
        Directory.CreateDirectory(DryRunFolder);
        foreach (var arg in new[] { dll, "--urls", Url, "--data", _data, "--folder", DryRunFolder })
            start.ArgumentList.Add(arg);
        if (_sample)
        {
            start.ArgumentList.Add("--sample");
            start.ArgumentList.Add("true");
        }
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
