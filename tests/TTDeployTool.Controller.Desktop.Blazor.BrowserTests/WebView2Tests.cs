using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests;

/// <summary>
/// ③ WPF のホストのテスト用のビルド（/p:UiTestMock=true。Windows）を起動し、WebView2 に CDP でつないで、テスト用のホストと同じ画面が WebView2 の中でも動くことを確かめる
/// （Playwright「WebView2」・Microsoft Learn「Automate and test WebView2 apps」。desktop-blazor-hybrid.md の 6・16）。
/// 環境変数 TT_BLAZOR_WPF_EXE（WPF のホストの exe）があるときだけ動く。アプリのデータのフォルダーは一時フォルダー（TT_BLAZOR_DATA_DIRECTORY）にし、見本のモックのデータ
/// （デプロイ 45 件）を置いて、モックのアカウントでサインインしてから確かめる。CDP のポートは、WPF のホストが BlazorWebViewInitializing で WebView2 に渡す（TT_BLAZOR_CDP_PORT）。
/// TT_BLAZOR_REPORT があれば、起動から表が出るまでの時間を JSON で書く。
/// </summary>
public sealed class WebView2Tests
{
    [Fact]
    public async Task WebView2_Should_Show_The_Table_And_Take_Real_Input()
    {
        var exe = Environment.GetEnvironmentVariable("TT_BLAZOR_WPF_EXE");
        if (string.IsNullOrEmpty(exe))
            Assert.Skip("WPF のホスト（環境変数 TT_BLAZOR_WPF_EXE）が無いため、スキップします（Windows の CI の blazor-prototype-windows ジョブで動かす）。");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var root = Path.Combine(Path.GetTempPath(), "ttdeploytool-blazor-webview2", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var now = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(data, Fixtures.FixtureData.FileName), Fixtures.FixtureSamples.Data(45, now).ToJson(), TestContext.Current.CancellationToken);
        new Core.Services.FileJobHistory(Path.Combine(data, "job-history.json")).Records = Fixtures.FixtureSamples.JobRecords(30, now);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.Environment["TT_BLAZOR_CDP_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["TT_BLAZOR_USER_DATA"] = Path.Combine(root, "webview2");
        start.Environment["TT_BLAZOR_DATA_DIRECTORY"] = data;
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            while (true)
            {
                try
                {
                    await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", TestContext.Current.CancellationToken);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && clock.Elapsed < TimeSpan.FromSeconds(90))
                {
                    process.HasExited.Should().BeFalse("WPF のホストが CDP のポートを開く前に終わった（終了コード {0}）", process.HasExited ? process.ExitCode : 0);
                    await Task.Delay(200, TestContext.Current.CancellationToken);
                }
            }
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");
            var page = await PageAsync(browser);
            await page.Locator("[data-id='Panel.Target']").WaitForAsync(new() { Timeout = 60_000 });
            var toShell = clock.Elapsed;
            (await page.TitleAsync()).Should().Be("TTDeployTool");
            await BrowserHost.SignInAndOpenDeploymentsAsync(page);
            var toTable = clock.Elapsed;

            (await page.Locator("tbody tr").CountAsync()).Should().Be(20);
            var sort = page.Locator("th[data-key='AppName'] button.tt-sort");
            await sort.ClickAsync();
            await Assertions.Expect(page.Locator("th[data-key='AppName']")).ToHaveAttributeAsync("aria-sort", "ascending");
            await sort.ClickAsync();
            await sort.ClickAsync();
            await Assertions.Expect(page.Locator("th[data-key='AppName']")).ToHaveAttributeAsync("aria-sort", "none");

            // 含む文字（キーボードで打つ）と、絞り込みの後もフォーカスが入力欄にあること
            var box = page.Locator("input[data-filter='deployments']");
            await box.ClickAsync();
            await page.Keyboard.TypeAsync("app07");
            await Assertions.Expect(page.Locator(".tt-page-text")).ToContainTextAsync("絞り込んだ 1 件");
            (await box.EvaluateAsync<bool>("b => document.activeElement === b")).Should().BeTrue();
            await page.Locator("button.tt-clear").ClickAsync();

            // 見出しの幅（キーボード）と、選んだ行のコピー（WPF のクリップボード）
            var before = (await page.Locator("th[data-key='ResourceGroup']").BoundingBoxAsync())!.Width;
            await page.Locator("th[data-key='ResourceGroup'] button.tt-sort").FocusAsync();
            await page.Keyboard.PressAsync("Alt+ArrowRight");
            await Assertions.Expect(page.Locator("col[data-key='ResourceGroup']")).ToHaveAttributeAsync("style", $"width: {before + 16:0}px");
            await page.Locator(".tt-reset").ClickAsync();
            await page.Locator("tbody tr").Nth(0).Locator("td.tt-check input").CheckAsync();
            await page.Locator(".tt-table-scroll").FocusAsync();
            await page.Keyboard.PressAsync("Control+C");
            await Assertions.Expect(page.Locator(".tt-status")).ToHaveTextAsync("1 行をコピーしました。");

            if (Environment.GetEnvironmentVariable("TT_BLAZOR_REPORT") is { Length: > 0 } report)
            {
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    timeToShellMs = (long)toShell.TotalMilliseconds,
                    timeToTableMs = (long)toTable.TotalMilliseconds,
                    webView2 = browser.Version,
                }), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // WebView2 のプロセスが残っていれば、消せないことがある
            }
        }
    }

    // WebView2 のページ（つないだ直後は、まだ無いことがある）
    private static async Task<IPage> PageAsync(IBrowser browser)
    {
        for (var i = 0; i < 300; i++)
        {
            if (browser.Contexts.SelectMany(c => c.Pages).FirstOrDefault() is { } page)
                return page;
            await Task.Delay(200);
        }
        throw new TimeoutException("WebView2 のページが見つかりません。");
    }
}
