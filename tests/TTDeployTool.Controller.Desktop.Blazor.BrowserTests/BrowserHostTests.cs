using FluentAssertions;
using Microsoft.Playwright;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests;

/// <summary>
/// ② テスト用のホストの画面を、Chromium で本物の入力で操作する（desktop-blazor-hybrid.md の 9 の UX）。WinUI の画面で UI Automation の木に出ず、
/// FlaUI で確かめにくかったもの（▽ の中・ドラッグ・6 行の表示とツールチップ・変換中の入力・キーボードでの見出しの操作）を確かめる。
/// </summary>
public sealed class BrowserHostTests
{
    private static ILocator Header(IPage page, string key) => page.Locator($"th[data-key='{key}']");

    private static Task<string?> SortOf(IPage page, string key) => Header(page, key).GetAttributeAsync("aria-sort");

    private static Task<int> RowCount(IPage page) => page.Locator("tbody tr").CountAsync();

    private static async Task<double> WidthOf(IPage page, string key) => (await Header(page, key).BoundingBoxAsync())!.Width;

    private static async Task<string[]> HeaderKeys(IPage page) =>
        [.. await page.Locator("thead th[data-key]").EvaluateAllAsync<string[]>("hs => hs.map(h => h.dataset.key)")];

    [Fact]
    public async Task Header_Clicks_Should_Sort_Three_Ways_And_The_Job_Column_Should_Not_Sort()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;
        var sort = Header(page, "AppName").Locator("button.tt-sort");

        (await SortOf(page, "Subscription")).Should().Be("ascending");
        await sort.ClickAsync();
        await Assertions.Expect(Header(page, "AppName")).ToHaveAttributeAsync("aria-sort", "ascending");
        (await page.Locator("tbody tr td[data-key='AppName']").First.TextContentAsync()).Should().Be("app00");
        await sort.ClickAsync();
        await Assertions.Expect(Header(page, "AppName")).ToHaveAttributeAsync("aria-sort", "descending");
        (await page.Locator("tbody tr td[data-key='AppName']").First.TextContentAsync()).Should().Be("app44");
        await sort.ClickAsync();
        await Assertions.Expect(Header(page, "AppName")).ToHaveAttributeAsync("aria-sort", "none");
        (await SortOf(page, "Subscription")).Should().Be("ascending");

        // 並べ替えない「ジョブ」の見出しは、押しても何も変わらない（WinUI の画面では、押すとアプリが落ちた。desktop-tables.md の 10.6）
        await Header(page, "Job").Locator(".tt-head-text").ClickAsync();
        (await SortOf(page, "Subscription")).Should().Be("ascending");
        (await Header(page, "Job").Locator("button.tt-sort").CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Filter_Menu_Should_Narrow_The_Rows_With_The_Mouse()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;

        await Header(page, "Group").Locator("button.tt-filter").ClickAsync();
        await page.Locator(".tt-popup-values label", new() { HasText = "研修" }).ClickAsync();
        await page.Locator(".tt-popup-buttons .tt-primary").ClickAsync();
        await Assertions.Expect(page.Locator(".tt-page-text")).ToContainTextAsync("絞り込んだ 30 件 / 全 45 件");
        await Assertions.Expect(Header(page, "Group")).ToHaveClassAsync("tt-filtered");

        // 探して選ぶ
        await Header(page, "State").Locator("button.tt-filter").ClickAsync();
        await page.Locator(".tt-popup-search").FillAsync("停止");
        await Assertions.Expect(page.Locator(".tt-popup-value")).ToHaveTextAsync(["停止中"]);
        await page.Locator(".tt-popup-buttons .tt-primary").ClickAsync();
        await Assertions.Expect(page.Locator(".tt-page-text")).ToContainTextAsync("絞り込んだ 6 件");

        await page.Locator("button.tt-clear").ClickAsync();
        await Assertions.Expect(page.Locator(".tt-page-text")).ToContainTextAsync("全 45 件");
        await Assertions.Expect(page.Locator("th.tt-filtered")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Text_Filter_Should_Wait_While_Japanese_Is_Being_Converted_And_Keep_The_Focus()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;
        var box = page.Locator("input[data-filter='deployments']");
        await box.ClickAsync();

        // 変換の途中（CDP で IME の変換中の文字を入れる）は、待っても絞り込まない。入力欄の文字とフォーカスはそのまま
        var cdp = await page.Context.NewCDPSessionAsync(page);
        await cdp.SendAsync("Input.imeSetComposition", new Dictionary<string, object> { ["text"] = "えいぎょ", ["selectionStart"] = 4, ["selectionEnd"] = 4 });
        await page.WaitForTimeoutAsync(1200);
        (await RowCount(page)).Should().Be(20);
        (await box.InputValueAsync()).Should().Be("えいぎょ");
        (await box.EvaluateAsync<bool>("b => document.activeElement === b")).Should().BeTrue();

        // 確定すると、0.4 秒後に絞り込む。絞り込んだ後もフォーカスは入力欄のまま、続けて打てる
        await cdp.SendAsync("Input.insertText", new Dictionary<string, object> { ["text"] = "営業" });
        await Assertions.Expect(page.Locator(".tt-page-text")).ToContainTextAsync("絞り込んだ 15 件");
        (await box.EvaluateAsync<bool>("b => document.activeElement === b")).Should().BeTrue();
        await page.Keyboard.TypeAsync("x");
        await Assertions.Expect(page.Locator(".tt-page-text")).ToContainTextAsync("表示するデプロイはありません");
        (await box.InputValueAsync()).Should().Be("営業x");

        // 「絞り込みをすべて外す」で入力欄も空になる
        await page.Locator("button.tt-clear").ClickAsync();
        await Assertions.Expect(box).ToHaveValueAsync("");
        await Assertions.Expect(page.Locator(".tt-page-text")).ToContainTextAsync("全 45 件");
    }

    [Fact]
    public async Task Pages_Should_Hold_Twenty_Rows_And_Change_Size()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;
        (await RowCount(page)).Should().Be(20);
        await page.Locator(".tt-pager button", new() { HasText = "次のページ" }).ClickAsync();
        await Assertions.Expect(page.Locator(".tt-page-text")).ToContainTextAsync("21〜40 件目");
        await page.Locator(".tt-pager button", new() { HasText = "次のページ" }).ClickAsync();
        await Assertions.Expect(page.Locator("tbody tr")).ToHaveCountAsync(5);
        await Assertions.Expect(page.Locator(".tt-pager button", new() { HasText = "次のページ" })).ToBeDisabledAsync();
        await page.Locator(".tt-pager select").SelectOptionAsync("50");
        await Assertions.Expect(page.Locator("tbody tr")).ToHaveCountAsync(45);
    }

    [Fact]
    public async Task Columns_Should_Resize_And_Reorder_By_Dragging_Survive_A_Restart_And_Reset()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;
        var before = await WidthOf(page, "ResourceGroup");

        // 見出しの右端のつまみをドラッグして広げる
        var grip = (await Header(page, "ResourceGroup").Locator(".tt-grip").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(grip.X + grip.Width / 2, grip.Y + grip.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(grip.X + 90, grip.Y + grip.Height / 2, new() { Steps = 5 });
        await page.Mouse.UpAsync();
        await Assertions.Expect(page.Locator("col[data-key='ResourceGroup']")).ToHaveAttributeAsync("style", $"width: {before + 86:0}px");

        // 見出しをドラッグして「状態」をいちばん左に
        await Header(page, "State").Locator(".tt-head").DragToAsync(Header(page, "Subscription"));
        await Assertions.Expect(page.Locator("thead th[data-key]").First).ToHaveAttributeAsync("data-key", "State");
        (await page.Locator("tbody tr").First.Locator("td[data-key]").First.GetAttributeAsync("data-key")).Should().Be("State");

        // 起動し直しても同じ
        await host.RestartAsync();
        page = host.Page;
        (await HeaderKeys(page)).Should().StartWith(["State", "Subscription", "ResourceGroup"]);
        (await page.Locator("col[data-key='ResourceGroup']").GetAttributeAsync("style")).Should().Be($"width: {before + 86:0}px");

        await page.Locator(".tt-reset").ClickAsync();
        await Assertions.Expect(page.Locator("thead th[data-key]").First).ToHaveAttributeAsync("data-key", "Subscription");
        (await HeaderKeys(page)).Should().Equal("Subscription", "ResourceGroup", "AppName", "Group", "State", "ExpiresAt", "DeployedAt", "Job");
        (await WidthOf(page, "ResourceGroup")).Should().BeApproximately(before, 1);
    }

    [Fact]
    public async Task Keyboard_Should_Reach_The_Headers_To_Sort_Resize_And_Move_Columns()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;
        var sort = Header(page, "ResourceGroup").Locator("button.tt-sort");
        await sort.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(Header(page, "ResourceGroup")).ToHaveAttributeAsync("aria-sort", "ascending");

        var before = await WidthOf(page, "ResourceGroup");
        await page.Keyboard.PressAsync("Alt+ArrowRight");
        await Assertions.Expect(page.Locator("col[data-key='ResourceGroup']")).ToHaveAttributeAsync("style", $"width: {before + 16:0}px");
        await page.Keyboard.PressAsync("Alt+Shift+ArrowLeft");
        await Assertions.Expect(page.Locator("thead th[data-key]").First).ToHaveAttributeAsync("data-key", "ResourceGroup");
        // Alt+← で前のページに戻らない（画面はそのまま）
        page.Url.Should().StartWith(host.Url);

        // Tab で ▽ に移り、Enter で開いて Esc で閉じる
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator(".tt-popup")).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".tt-popup")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Last_Column_Should_Fill_A_Wide_Window_And_The_Table_Should_Scroll_In_A_Narrow_One()
    {
        await using var host = await BrowserHost.StartAsync(width: 1800);
        var page = host.Page;
        var table = (await page.Locator("table.tt-table").BoundingBoxAsync())!;
        var last = (await Header(page, "Job").BoundingBoxAsync())!;
        (last.X + last.Width).Should().BeApproximately(table.X + table.Width, 1);
        last.Width.Should().BeGreaterThan(400);

        await page.SetViewportSizeAsync(900, 1000);
        await Assertions.Expect(Header(page, "Job")).ToHaveCSSAsync("min-width", "160px");
        (await page.Locator(".tt-table-scroll").EvaluateAsync<bool>("s => s.scrollWidth > s.clientWidth")).Should().BeTrue();
        (await WidthOf(page, "Job")).Should().BeGreaterThanOrEqualTo(159);
        (await WidthOf(page, "Subscription")).Should().BeApproximately(200, 1);
    }

    [Fact]
    public async Task Long_Results_Should_Be_Cut_At_Six_Lines_And_Show_A_Tooltip_Only_When_Cut()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;
        await page.Locator("[data-id='Nav.Jobs']").ClickAsync();
        var cell = page.Locator("tbody tr").First.Locator("td[data-key='Result'] .tt-cell");
        await cell.WaitForAsync();
        var lines = await cell.EvaluateAsync<double>("c => c.clientHeight / parseFloat(getComputedStyle(c).lineHeight)");
        lines.Should().BeApproximately(6, 0.2);
        (await cell.EvaluateAsync<bool>("c => c.scrollHeight > c.clientHeight")).Should().BeTrue();

        await cell.HoverAsync();
        (await cell.GetAttributeAsync("title")).Should().StartWith("長い結果の文字の 1 番目の文です。");
        var shortCell = page.Locator("tbody tr").First.Locator("td[data-key='Kind'] .tt-cell");
        await shortCell.HoverAsync();
        (await shortCell.GetAttributeAsync("title")).Should().BeNull();
    }

    [Fact]
    public async Task Copy_Should_Put_A_Cell_Rows_And_Markdown_On_The_Clipboard()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;
        var rows = page.Locator("tbody tr");
        var name = await rows.Nth(0).Locator("td[data-key='AppName']").TextContentAsync();

        await rows.Nth(0).Locator("td[data-key='AppName']").ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator(".tt-menu button", new() { HasText = "この文字をコピー" }).ClickAsync();
        await Assertions.Expect(page.Locator(".tt-status")).ToHaveTextAsync("文字をコピーしました。");
        (await host.ClipboardAsync()).Should().Be(name);

        // チェックボックスで 2 行を選び、Ctrl+Shift+C（見出しつきのタブ区切り）
        await rows.Nth(0).Locator("td.tt-check input").CheckAsync();
        await rows.Nth(1).Locator("td.tt-check input").CheckAsync();
        await page.Locator(".tt-table-scroll").FocusAsync();
        await page.Keyboard.PressAsync("Control+Shift+C");
        await Assertions.Expect(page.Locator(".tt-status")).ToHaveTextAsync("2 行をコピーしました。");
        var tsv = (await host.ClipboardAsync()).Split("\r\n");
        tsv.Should().HaveCount(3);
        tsv[0].Should().StartWith("サブスクリプション\tリソースグループ\tアプリ名");

        await rows.Nth(5).Locator("td[data-key='State']").ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator(".tt-menu button", new() { HasText = "Markdown" }).ClickAsync();
        await Assertions.Expect(page.Locator(".tt-status")).ToHaveTextAsync("2 行を Markdown 形式でコピーしました。");
        var markdown = await host.ClipboardAsync();
        markdown.Should().StartWith("| サブスクリプション | リソースグループ |").And.Contain(name);
    }

    [Fact]
    public async Task Corner_Checkbox_Should_Select_The_Page_And_Show_A_Partial_Selection()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;
        var corner = page.Locator("thead input[type=checkbox]");

        await page.Locator("tbody tr").Nth(3).ClickAsync();
        await Assertions.Expect(page.Locator("[data-id='Deployments.Selection']")).ToHaveTextAsync("1 件を選んでいます。");
        await Assertions.Expect(corner).ToHaveJSPropertyAsync("indeterminate", true);

        await corner.ClickAsync();
        await Assertions.Expect(page.Locator("[data-id='Deployments.Selection']")).ToHaveTextAsync("20 件を選んでいます。");
        await Assertions.Expect(corner).ToBeCheckedAsync();
        await Assertions.Expect(corner).ToHaveJSPropertyAsync("indeterminate", false);
        await corner.ClickAsync();
        await Assertions.Expect(page.Locator("[data-id='Deployments.Selection']")).ToHaveTextAsync("行を選ぶと、まとめて操作できます。");
    }

    [Fact]
    public async Task Double_Click_On_A_Job_Should_Open_Its_Details()
    {
        await using var host = await BrowserHost.StartAsync();
        var page = host.Page;
        await page.Locator("[data-id='Nav.Jobs']").ClickAsync();
        var row = page.Locator("tbody tr").Nth(2);
        var target = await row.Locator("td[data-key='Target']").TextContentAsync();
        await row.DblClickAsync();
        await Assertions.Expect(page.Locator("[data-id='Panel.Status']")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-id='Job.Title']")).ToContainTextAsync(target!.Split(" / ")[1]);
        await page.Locator("[data-id='Status.BackToList']").ClickAsync();
        await Assertions.Expect(page.Locator("[data-id='Panel.Jobs']")).ToBeVisibleAsync();
    }
}
