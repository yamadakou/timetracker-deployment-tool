using FluentAssertions;
using Microsoft.Playwright;
using TTDeployTool.Controller.Desktop.Fixtures;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// 一覧の行の右クリックのメニューの操作・行のダブルクリック・右の詳細の範囲選択とコピー（オーナーの依頼、2026-11-14）を、本物のブラウザーで確かめる。
/// </summary>
public sealed class RowMenuUiTests
{
    [Fact]
    public async Task Rows_Should_Offer_Operations_Open_The_Detail_By_Double_Click_And_Copy_The_Selected_Detail()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        var app = FixtureSamples.AppName(1);
        var row = page.RowWith("Deployments", "AppName", app);

        // ダブルクリックで、その行だけを選んで右に詳細を出す
        await row.DblClickAsync();
        await Assertions.Expect(page.Locator("[data-id='Deployments.Detail'] h2")).ToContainTextAsync(app);
        await page.ExpectTextAsync("Deployments.Selection", "1 件を選んでいます。");

        // 右クリックのメニュー: コピーの後に行の操作。「期限・グループを変える…」で期限日の入力に移る
        await row.Locator("td[data-key='AppName']").ClickAsync(new() { Button = MouseButton.Right });
        await Assertions.Expect(page.Id("Menu.Row.Delete")).ToBeVisibleAsync();
        if (Environment.GetEnvironmentVariable("TT_SCREENSHOT_DIR") is { Length: > 0 } dir)
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "row-menu.png") });
        await page.ClickIdAsync("Menu.Row.EditBatch");
        await Assertions.Expect(page.Id("Deployments.BatchExpiryDate")).ToBeFocusedAsync();

        // 詳細の文字を範囲選択して、右クリック →「選んだ文字をコピー」
        var title = page.Locator("[data-id='Deployments.Detail'] h2");
        await title.EvaluateAsync("h => { const r = document.createRange(); r.selectNodeContents(h.firstChild); const s = getSelection(); s.removeAllRanges(); s.addRange(r); }");
        // 選んだ文字の上で右クリックする（選んだ範囲の外を押すと、ブラウザーが選択を外す）
        var box = (await title.BoundingBoxAsync())!;
        await title.ClickAsync(new() { Button = MouseButton.Right, Position = new() { X = 6, Y = box.Height / 2 } });
        await Assertions.Expect(page.Id("Detail.CopySelection")).ToBeEnabledAsync();
        if (Environment.GetEnvironmentVariable("TT_SCREENSHOT_DIR") is { Length: > 0 } dir2)
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir2, "detail-menu.png") });
        await page.ClickIdAsync("Detail.CopySelection");
        await WaitUntilAsync(async () => (await host.ClipboardAsync()).Trim() == app, "選んだ文字がコピーされる");

        // 詳細の外で選んだ文字は、詳細のコピーの対象にしない
        var outside = await page.EvaluateAsync<string>(
            "async () => { const h = document.querySelector('h1'); const r = document.createRange(); r.selectNodeContents(h); const s = getSelection(); s.removeAllRanges(); s.addRange(r);" +
            " const m = await import('./_content/TTDeployTool.Controller.Desktop.Blazor/table.js'); return m.selectionIn(document.querySelector(\"[data-id='Deployments.Detail']\")); }");
        outside.Should().BeEmpty();

        // 「詳細をコピー」
        await page.Id("Deployments.Detail").ClickAsync(new() { Button = MouseButton.Right, Position = new() { X = 10, Y = 10 } });
        await page.ClickIdAsync("Detail.CopyAll");
        await WaitUntilAsync(async () => (await host.ClipboardAsync()).StartsWith($"アプリ名: {app}\n", StringComparison.Ordinal), "詳細がコピーされる");

        // ジョブの一覧の右クリック →「詳細（実行状況・ログ）を開く」
        await page.ShowAsync("Jobs");
        await page.Rows("Jobs").First.Locator("td[data-key]").First.ClickAsync(new() { Button = MouseButton.Right });
        await page.ClickIdAsync("Menu.Job.Detail");
        await Assertions.Expect(page.Id("Panel.Status")).ToBeVisibleAsync();
    }
}
