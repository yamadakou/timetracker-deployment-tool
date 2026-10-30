using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests;

/// <summary>
/// ③ WPF のホスト（Windows）を起動し、WebView2 に CDP でつないで、テスト用のホストと同じ画面が WebView2 の中でも動くことを確かめる
/// （Playwright「WebView2」・Microsoft Learn「Automate and test WebView2 apps」。desktop-blazor-hybrid.md の 6）。
/// 環境変数 TT_BLAZOR_WPF_EXE（WPF のホストの exe）があるときだけ動く。TT_BLAZOR_REPORT があれば、起動から表が出るまでの時間を JSON で書く。
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
        var userData = Path.Combine(Path.GetTempPath(), "ttdeploytool-blazor-webview2", Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = $"--remote-debugging-port={port}";
        start.Environment["WEBVIEW2_USER_DATA_FOLDER"] = userData;
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        try
        {
            using var http = new HttpClient();
            for (var i = 0; ; i++)
            {
                try
                {
                    await http.GetStringAsync($"http://127.0.0.1:{port}/json/version");
                    break;
                }
                catch (HttpRequestException) when (i < 300 && !process.HasExited)
                {
                    await Task.Delay(200);
                }
            }
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");
            var page = await PageAsync(browser);
            await page.Locator("tbody tr").First.WaitForAsync(new() { Timeout = 60_000 });
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
                    timeToTableMs = (long)toTable.TotalMilliseconds,
                    webView2 = browser.Version,
                }));
            }
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
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
