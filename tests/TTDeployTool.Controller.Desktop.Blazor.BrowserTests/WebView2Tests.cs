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

            // 見出しのドラッグ（ポインターのイベント。ブラウザーの標準のドラッグは、この WebView2 では働かなかった）と、▽ の「左へ移動」
            var group = (await page.Locator("th[data-key='Group'] .tt-head-text").BoundingBoxAsync())!;
            var target = (await page.Locator("th[data-key='ResourceGroup']").BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(group.X + 5, group.Y + group.Height / 2);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(target.X + target.Width / 2, target.Y + target.Height / 2, new() { Steps = 8 });
            await page.Mouse.UpAsync();
            await Assertions.Expect(page.Locator("thead th[data-key]").Nth(1)).ToHaveAttributeAsync("data-key", "Group");
            // ドラッグの後は「サブスクリプション・グループ・リソースグループ・アプリ名・状態」。▽ の「左へ移動」で、状態は 5 番目から 4 番目へ
            await Assertions.Expect(page.Locator("thead th[data-key]").Nth(4)).ToHaveAttributeAsync("data-key", "State");
            await page.Locator("th[data-key='State'] button.tt-filter").ClickAsync();
            await Assertions.Expect(page.Locator(".tt-popup")).ToBeVisibleAsync();
            await page.Locator(".tt-popup [data-move='left']").ClickAsync();
            await Assertions.Expect(page.Locator("thead th[data-key]").Nth(3)).ToHaveAttributeAsync("data-key", "State");
            await Assertions.Expect(page.Locator("thead th[data-key]").Nth(4)).ToHaveAttributeAsync("data-key", "AppName");
            await page.Keyboard.PressAsync("Escape");
            await page.Locator(".tt-reset").ClickAsync();
            await Assertions.Expect(page.Locator("thead th[data-key]").Nth(1)).ToHaveAttributeAsync("data-key", "ResourceGroup");

            await page.Locator("tbody tr").Nth(0).Locator("td.tt-check input").CheckAsync();
            await page.Locator(".tt-table-scroll").FocusAsync();
            await page.Keyboard.PressAsync("Control+C");
            await Assertions.Expect(page.Locator(".tt-status")).ToHaveTextAsync("1 行をコピーしました。");
            var first = (await page.Locator("tbody tr").Nth(0).Locator("td[data-key='AppName']").TextContentAsync())!.Trim();
            // Windows のクリップボードに入った（ほかのアプリに貼り付けられる。手順書の実機の確認から移した。2026-11-14）
            await WaitForClipboardAsync(text => text.Contains(first, StringComparison.Ordinal), "Ctrl+C で選んだ行");
            // キーボードだけで行のメニュー: Shift+F10 で開いて最初の項目にフォーカス、↓ で次へ、Esc で閉じて表に戻る（UX の見直し D、2026-11-16）
            await page.Keyboard.PressAsync("Shift+F10");
            await Assertions.Expect(page.Locator(".tt-menu")).ToBeVisibleAsync();
            await UiTests.Ui.WaitUntilAsync(async () => await page.EvaluateAsync<string>("document.activeElement.textContent") == "この文字をコピー", "メニューの最初の項目にフォーカス");
            await page.Keyboard.PressAsync("ArrowDown");
            (await page.EvaluateAsync<string>("document.activeElement.textContent")).Should().Be("選んだ行をコピー");
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.Locator(".tt-menu")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".tt-table-scroll")).ToBeFocusedAsync();
            await page.Locator("tbody tr").Nth(0).Locator("td.tt-check input").UncheckAsync();

            // 行の右クリック: WebView2 の標準のメニューは出さず（contextmenu を止める）、アプリのメニューに行の操作が並ぶ（オーナーの依頼、2026-11-14）
            var prevented = await page.EvaluateAsync<bool>(
                "() => { const td = document.querySelector(\"tbody td[data-key='AppName']\"); const e = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 20, clientY: 20 }); td.dispatchEvent(e); return e.defaultPrevented; }");
            prevented.Should().BeTrue("WebView2 の標準の右クリックのメニューを出さない");
            await Assertions.Expect(page.Locator("[data-id='Menu.Row.Delete']")).ToBeVisibleAsync();
            await page.Locator("[data-id='Menu.Row.EditBatch']").ClickAsync();
            await Assertions.Expect(page.Locator("[data-id='Deployments.BatchExpiryDate']")).ToBeFocusedAsync();

            // 行のダブルクリックで、その行だけを選んで右に詳細を出す
            var second = page.Locator("tbody tr").Nth(1);
            var secondApp = (await second.Locator("td[data-key='AppName']").TextContentAsync())!.Trim();
            await second.Locator("td[data-key='AppName']").DblClickAsync();
            await Assertions.Expect(page.Locator("[data-id='Deployments.Detail'] h2")).ToContainTextAsync(secondApp);

            // 右の詳細: 範囲選択した文字と、詳細のすべてを、右クリックのメニューから Windows のクリップボードにコピーする
            var title = page.Locator("[data-id='Deployments.Detail'] h2");
            await title.EvaluateAsync("h => { const r = document.createRange(); r.selectNodeContents(h.firstChild); const s = getSelection(); s.removeAllRanges(); s.addRange(r); }");
            var titleBox = (await title.BoundingBoxAsync())!;
            await title.ClickAsync(new() { Button = MouseButton.Right, Position = new() { X = 6, Y = titleBox.Height / 2 } });
            await page.Locator("[data-id='Detail.CopySelection']").ClickAsync();
            await WaitForClipboardAsync(text => text.Trim() == secondApp, "詳細で選んだ文字");
            await page.Locator("[data-id='Deployments.Detail']").ClickAsync(new() { Button = MouseButton.Right, Position = new() { X = 10, Y = 10 } });
            await page.Locator("[data-id='Detail.CopyAll']").ClickAsync();
            await WaitForClipboardAsync(text => text.StartsWith($"アプリ名: {secondApp}", StringComparison.Ordinal) && !text.Contains("パスワード"), "詳細のすべて（パスワードは含めない）");

            // ジョブの一覧の行の右クリック →「詳細（実行状況・ログ）を開く」
            await page.Locator("[data-id='Nav.Jobs']").ClickAsync();
            await page.Locator("[data-id='Panel.Jobs'] tbody tr").First.Locator("td[data-key]").First.ClickAsync(new() { Button = MouseButton.Right });
            await page.Locator("[data-id='Menu.Job.Detail']").ClickAsync();
            await Assertions.Expect(page.Locator("[data-id='Panel.Status']")).ToBeVisibleAsync();

            // 左のメニュー: つまみのドラッグで幅、ボタンで畳む・広げる。アカウントの右クリックでメニュー（オーナーの依頼、2026-11-16）
            var nav = page.Locator("[data-id='Nav']");
            var navWidth = (await nav.BoundingBoxAsync())!.Width;
            var grip = (await page.Locator("[data-id='Nav.Grip']").BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(grip.X + grip.Width / 2, grip.Y + 200);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(grip.X + grip.Width / 2 + 40, grip.Y + 200, new() { Steps = 6 });
            await page.Mouse.UpAsync();
            await UiTests.Ui.WaitUntilAsync(async () => (await nav.BoundingBoxAsync())!.Width == navWidth + 40, "つまみのドラッグで広がる");
            await page.Locator("[data-id='Nav.Grip']").DblClickAsync();
            await page.Locator("[data-id='Nav.Toggle']").ClickAsync();
            await UiTests.Ui.WaitUntilAsync(async () => (await nav.BoundingBoxAsync())!.Width == 60, "畳む");
            await page.Locator("[data-id='Nav.Toggle']").ClickAsync();
            await page.Locator("[data-id='Nav.Account']").ClickAsync(new() { Button = MouseButton.Right });
            await Assertions.Expect(page.Locator("[data-id='Menu.Account.Portal']")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.Locator("[data-id='Account.Menu']")).ToHaveCountAsync(0);

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

    // Windows のクリップボード（PowerShell の Get-Clipboard で読む。WPF のアプリが書いた後、読めるまで待つ）
    private static async Task WaitForClipboardAsync(Func<string, bool> condition, string what)
    {
        var text = "";
        for (var i = 0; i < 50; i++)
        {
            var start = new ProcessStartInfo("powershell", ["-NoProfile", "-Command", "[Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-Clipboard -Raw"])
            {
                UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var process = Process.Start(start)!;
            text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (condition(text))
                return;
            await Task.Delay(200);
        }
        text.Should().Be("（条件に合う文字）", $"{what}が Windows のクリップボードに入る");
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
