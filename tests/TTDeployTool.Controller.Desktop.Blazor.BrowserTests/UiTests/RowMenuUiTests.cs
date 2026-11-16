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

    /// <summary>
    /// キーボードだけで、行のメニューを開いて動き、閉じる（UX の見直し D、2026-11-16）。Shift+F10・メニューキーで開き、最初の項目にフォーカス。
    /// ↑/↓・Home/End で押せる項目を動き、Esc で閉じて表に戻る。右の詳細も、フォーカスを置いて Shift+F10 で開ける。
    /// </summary>
    [Fact]
    public async Task The_Row_Menu_Should_Work_With_The_Keyboard_Only()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        var scroll = page.Locator("[data-id='Panel.Deployments'] .tt-table-scroll");
        var focused = "document.activeElement?.textContent ?? ''";

        // 選んだ行が無いと開かず、選び方を伝える
        await scroll.FocusAsync();
        await page.Keyboard.PressAsync("Shift+F10");
        await Assertions.Expect(page.Locator("[data-id='Panel.Deployments'] .tt-status")).ToContainTextAsync("行を選んでから");
        (await page.Locator(".tt-menu").CountAsync()).Should().Be(0);

        // 行のチェックボックスにフォーカスを置いて Space で選び、Shift+F10 でその行のメニュー（最初の項目にフォーカス）
        var row = page.Rows("Deployments").Nth(1);
        await row.Locator("input[type=checkbox]").FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await Assertions.Expect(row).ToHaveAttributeAsync("aria-selected", "true");
        await page.Keyboard.PressAsync("Shift+F10");
        await Assertions.Expect(page.Locator(".tt-menu")).ToBeVisibleAsync();
        await WaitUntilAsync(async () => await page.EvaluateAsync<string>(focused) == "この文字をコピー", "最初の項目にフォーカス");

        // ↓・↑・End・Home（押せない項目は飛ばす）。ほかのキーでは動かない
        await page.Keyboard.PressAsync("ArrowDown");
        (await page.EvaluateAsync<string>(focused)).Should().Be("選んだ行をコピー");
        await page.Keyboard.PressAsync("ArrowUp");
        await page.Keyboard.PressAsync("ArrowUp");
        var last = await page.EvaluateAsync<string>(
            "() => [...document.querySelectorAll('.tt-menu [role=menuitem]:not([disabled])')].at(-1).textContent");
        (await page.EvaluateAsync<string>(focused)).Should().Be(last, "最初から ↑ で最後に回る");
        await page.Keyboard.PressAsync("Home");
        (await page.EvaluateAsync<string>(focused)).Should().Be("この文字をコピー");
        await page.Keyboard.PressAsync("End");
        (await page.EvaluateAsync<string>(focused)).Should().Be(last);
        await page.Keyboard.PressAsync("ArrowDown");
        (await page.EvaluateAsync<string>(focused)).Should().Be("この文字をコピー", "最後から ↓ で最初に回る");
        await page.Keyboard.PressAsync("x");
        (await page.EvaluateAsync<string>(focused)).Should().Be("この文字をコピー");

        // Esc で閉じて、表に戻る
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".tt-menu")).ToHaveCountAsync(0);
        await Assertions.Expect(scroll).ToBeFocusedAsync();

        // 表にフォーカスがあれば、メニューキーで選んだ行のメニュー。Enter で項目を押す
        await page.Keyboard.PressAsync("ContextMenu");
        await Assertions.Expect(page.Id("Menu.Row.Detail")).ToBeVisibleAsync();
        await page.Id("Menu.Row.Detail").FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Id("Deployments.Detail")).ToBeVisibleAsync();

        // 右の詳細: フォーカスを置いて Shift+F10 でメニュー、Esc で詳細に戻る
        await page.Id("Deployments.Detail").FocusAsync();
        await page.Keyboard.PressAsync("Shift+F10");
        await Assertions.Expect(page.Id("Detail.Menu")).ToBeVisibleAsync();
        await WaitUntilAsync(async () => await page.EvaluateAsync<string>(focused) == "詳細をコピー", "押せる最初の項目にフォーカス");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Id("Detail.Menu")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Id("Deployments.Detail")).ToBeFocusedAsync();
    }
}
