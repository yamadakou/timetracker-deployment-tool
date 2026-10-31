using FluentAssertions;
using Microsoft.Playwright;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// WinUI 3 版の MockInputTests と同じ内容（マウスとキーボードの入力）: 見出しのドラッグ・右クリックのメニュー・Ctrl+C・▽ のメニュー・行のダブルクリック・
/// 左上のチェックボックス・右端の列の幅。サインインせずに、終わったジョブの記録 45 件のジョブの一覧で確かめる。
/// </summary>
public sealed class MockInputTests
{
    private static readonly string Newest = $"rg-mock-{100 + Records - 1} / app{100 + Records - 1}";

    private static readonly string Second = $"rg-mock-{100 + Records - 2} / app{100 + Records - 2}";

    private static async Task OpenJobsAsync(IPage page)
    {
        await page.ShowAsync("Jobs");
        await page.ExpectTextAsync("Jobs.Count", $"全 {Records} 件");
    }

    private static ILocator Header(IPage page, string key) => page.Locator($"[data-id='Panel.Jobs'] th[data-key='{key}']");

    private static ILocator Cell(IPage page, string target, string key) => page.RowWith("Jobs", "Target", target).Locator($"td[data-key='{key}']");

    private static async Task<bool> KindAfterSubscriptionAsync(IPage page) =>
        (await Cell(page, Newest, "Kind").BoundingBoxAsync())!.X > (await Cell(page, Newest, "Subscription").BoundingBoxAsync())!.X;

    private static async Task<float> KindCellWidthAsync(IPage page) => (await Cell(page, Newest, "Kind").BoundingBoxAsync())!.Width;

    [Fact]
    public async Task Columns_Should_Resize_And_Reorder_By_Dragging_And_Survive_A_Restart()
    {
        await using var host = await WithFixtureAsync(layout: false);
        var page = host.Page;
        await OpenJobsAsync(page);
        var kind = (await Header(page, "Kind").BoundingBoxAsync())!;
        var kindCell = await KindCellWidthAsync(page);
        // 見出しの右端のつまみをドラッグして広げる
        var grip = (await Header(page, "Kind").Locator(".tt-grip").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(grip.X + grip.Width / 2, grip.Y + grip.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(grip.X + 60, grip.Y + grip.Height / 2, new() { Steps = 5 });
        await page.Mouse.UpAsync();
        await WaitUntilAsync(async () => (await Header(page, "Kind").BoundingBoxAsync())!.Width >= kind.Width + 40, "つまみのドラッグで列が広がる");
        var widened = await KindCellWidthAsync(page);
        // 見出しをドラッグして、リソースグループ / アプリ名 の位置に動かす
        await Header(page, "Kind").Locator(".tt-head").DragToAsync(Header(page, "Target"));
        await WaitUntilAsync(() => KindAfterSubscriptionAsync(page), "見出しのドラッグで順序が変わる");

        // 起動し直しても同じ（列の配置を保存する）
        (await host.CloseActionAsync()).Should().Be("Exit", "ジョブが無ければ閉じると終わる");
        await host.RestartAsync();
        page = host.Page;
        await OpenJobsAsync(page);
        (await KindAfterSubscriptionAsync(page)).Should().BeTrue("順序は起動し直しても同じ");
        (await KindCellWidthAsync(page)).Should().BeApproximately(widened, 4, "幅は起動し直しても同じ");
        await page.ResetLayoutAsync("Jobs");
        await WaitUntilAsync(async () => !await KindAfterSubscriptionAsync(page), "初期値に戻すと元の順序");
        (await KindCellWidthAsync(page)).Should().BeApproximately(kindCell, 4, "初期値の幅に戻る");
    }

    [Fact]
    public async Task Cells_Should_Be_Copied_From_The_Menu_And_The_Keyboard()
    {
        await using var host = await WithFixtureAsync(jobsLayout:
        [
            new("Kind", 110), new("Target", 200), new("Result", 320), new("Subscription", 200), new("State", 170), new("Time", 120), new("RegisteredAt", 120),
            new("ChangedAt", 120), new("StartedAt", 120), new("Finished", 120), new("Duration", 0),
        ]);
        var page = host.Page;
        await OpenJobsAsync(page);
        // セルを右クリック →「この文字をコピー」
        await Cell(page, Newest, "Target").ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator(".tt-menu button", new() { HasText = "この文字をコピー" }).ClickAsync();
        await WaitUntilAsync(async () => await host.ClipboardAsync() == Newest, "セルの文字がコピーされる");

        // 行を 2 つ選び、右クリック →「選んだ行を Markdown 形式でコピー」
        await page.RowWith("Jobs", "Target", Newest).ClickAsync();
        await page.RowWith("Jobs", "Target", Second).ClickAsync();
        await Cell(page, Second, "Kind").ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator(".tt-menu button", new() { HasText = "選んだ行を Markdown 形式でコピー" }).ClickAsync();
        await WaitUntilAsync(async () => (await host.ClipboardAsync()).StartsWith("| 種類 | ", StringComparison.Ordinal), "Markdown の表がコピーされる");
        var markdown = (await host.ClipboardAsync()).Split('\n');
        markdown[1].Should().StartWith("|---|", "見出しの下の区切り");
        markdown.Should().HaveCount(4, "見出し・区切り・選んだ 2 行");

        // Ctrl+C（選んだ行をタブ区切り）、Ctrl+Shift+C（見出しつき）
        await page.Locator("[data-id='Panel.Jobs'] .tt-table-scroll").FocusAsync();
        await page.Keyboard.PressAsync("Control+C");
        await WaitUntilAsync(async () => await host.ClipboardAsync() is var text && text.Contains(Newest) && text.Contains('\t') && !text.StartsWith('|'), "Ctrl+C で選んだ行がコピーされる");
        (await host.ClipboardAsync()).Split('\n').Should().HaveCount(2);
        await page.Keyboard.PressAsync("Control+Shift+C");
        await WaitUntilAsync(async () => (await host.ClipboardAsync()).StartsWith("種類\t", StringComparison.Ordinal), "Ctrl+Shift+C で見出しつき");
    }

    [Fact]
    public async Task Filter_Menu_Corner_Check_Box_And_Double_Click_Should_Work()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await OpenJobsAsync(page);
        // 見出しの ▽ → 値の一覧の「デプロイ」を外して OK → 削除の 22 件
        await Header(page, "Kind").Locator("button.tt-filter").ClickAsync();
        await page.Locator(".tt-popup-values label", new() { HasText = "デプロイ" }).ClickAsync();
        await page.Locator(".tt-popup-buttons .tt-primary").ClickAsync();
        await page.ExpectTextAsync("Jobs.Count", $"絞り込んだ {Records / 2} 件 / 全 {Records} 件");
        await page.ClearFiltersAsync("Jobs");
        await page.ExpectTextAsync("Jobs.Count", $"（全 {Records} 件）");

        // 左上のチェックボックスで、今のページ（20 件）の行をすべて選ぶ・外す
        var corner = page.Locator("[data-id='Panel.Jobs'] thead input[type=checkbox]");
        await corner.ClickAsync();
        await Assertions.Expect(page.Id("Jobs.Selection")).ToHaveTextAsync("20 件を選んでいます。");
        await corner.ClickAsync();
        await Assertions.Expect(page.Id("Jobs.Selection")).ToContainTextAsync("行を選ぶと");

        // 行のダブルクリックで実行状況の画面を開き、ジョブの一覧に戻る
        await Cell(page, Newest, "Kind").DblClickAsync();
        await Assertions.Expect(page.Id("Panel.Status")).ToBeVisibleAsync();
        await page.ClickIdAsync("Status.BackToList");
        await Assertions.Expect(page.Id("Panel.Jobs")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_Last_Column_Should_Fill_The_Width_And_Stay_Left_Of_The_Scroll_Bar()
    {
        await using var host = await WithFixtureAsync(jobsLayout:
        [
            new("Kind", 50), new("Subscription", 50), new("Target", 50), new("State", 50), new("Time", 50), new("RegisteredAt", 50),
            // 列を狭くし、最後の列（結果。0 = 残りの幅いっぱい）が残りの幅で広がるようにする（WinUI 版は左のメニューを畳んだ 1024 × 768。こちらはメニューの分だけ広げる）
            new("ChangedAt", 50), new("StartedAt", 50), new("Finished", 50), new("Duration", 50), new("Result", 0),
        ], width: 1400, height: 768);
        var page = host.Page;
        await OpenJobsAsync(page);
        var last = (await Header(page, "Result").BoundingBoxAsync())!;
        // 最後の列（結果）は残りの幅いっぱいで、表の枠（縦のスクロールバーの内側）に収まる
        last.Width.Should().BeGreaterThanOrEqualTo(320, "最後の列は初期の幅（320）より狭くならない");
        var inner = await page.Locator("[data-id='Panel.Jobs'] .tt-table-scroll").EvaluateAsync<double>("s => s.getBoundingClientRect().left + s.clientWidth");
        (last.X + last.Width).Should().BeLessThanOrEqualTo((float)inner + 1, "右端の列はスクロールバーにかからない");
        (last.X + last.Width).Should().BeGreaterThan((float)inner - 2, "右端の列は残りの幅いっぱい");
        // 右端の列の幅（最小の幅）は、つまみで変えられる（残りの幅いっぱいのまま、最小の幅が今の幅から狭まる。WinUI 版は右隣の余白の列の左の端でつかむ）
        var grip = (await Header(page, "Result").Locator(".tt-grip").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(grip.X + grip.Width / 2, grip.Y + grip.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(grip.X - 38, grip.Y + grip.Height / 2, new() { Steps = 5 });
        await page.Mouse.UpAsync();
        await WaitUntilAsync(async () => Math.Abs(await Header(page, "Result").EvaluateAsync<double>("h => parseFloat(h.style.minWidth)") - (last.Width - 38)) <= 6,
            "右端の列の幅を変えられる");
    }
}
