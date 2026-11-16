using FluentAssertions;
using Microsoft.Playwright;
using TTDeployTool.Controller.Desktop.Fixtures;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// 左のメニューの幅（つまみのドラッグ・←/→・ダブルクリック）と、アイコンだけに畳む・アカウントの 2 行の表示とメニュー（オーナーの依頼、2026-11-16。
/// オーナーの判断: 幅の変更・畳むの両方、メニューは Azure Portal・コピー・切り替え・サインアウト）を、本物のブラウザーで確かめる。幅と畳んだかは、起動し直しても残る。
/// </summary>
public sealed class NavigationPaneUiTests
{
    private static async Task<double> WidthAsync(IPage page) => (await page.Id("Nav").BoundingBoxAsync())!.Width;

    [Fact]
    public async Task The_Navigation_Pane_Should_Resize_Collapse_And_Remember_It()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        (await WidthAsync(page)).Should().Be(240);

        // つまみのドラッグで広げる（範囲は 180〜420）
        var grip = (await page.Id("Nav.Grip").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(grip.X + grip.Width / 2, grip.Y + 200);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(grip.X + grip.Width / 2 + 80, grip.Y + 200, new() { Steps = 6 });
        await page.Mouse.UpAsync();
        await WaitUntilAsync(async () => await WidthAsync(page) == 320, "ドラッグした幅になる");
        await page.Mouse.MoveAsync(grip.X + 80, grip.Y + 200);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(grip.X + 900, grip.Y + 200, new() { Steps = 4 });
        await page.Mouse.UpAsync();
        await WaitUntilAsync(async () => await WidthAsync(page) == 420, "広げすぎない");

        // キーボード（つまみで ←/→）と、ダブルクリックで既定の幅
        await page.Id("Nav.Grip").FocusAsync();
        await page.Keyboard.PressAsync("ArrowLeft");
        await WaitUntilAsync(async () => await WidthAsync(page) == 410, "← で 10 狭める");
        await page.Keyboard.PressAsync("Enter");
        await page.Id("Nav.Grip").DblClickAsync();
        await WaitUntilAsync(async () => await WidthAsync(page) == 240, "既定の幅に戻る");
        await page.Id("Nav.Grip").FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await WaitUntilAsync(async () => await WidthAsync(page) == 250, "→ で 10 広げる");

        // 畳むと、アイコンだけ（名前はマウスを置くと出る）。起動し直しても畳んだまま。広げると前の幅に戻る
        await page.ClickIdAsync("Nav.Toggle");
        await WaitUntilAsync(async () => await WidthAsync(page) == 60, "畳んだ幅");
        await Assertions.Expect(page.Locator("[data-id='Nav.Deployments'] .tt-nav-label")).ToBeHiddenAsync();
        await Assertions.Expect(page.Id("Nav.Jobs")).ToHaveAttributeAsync("title", "ジョブの一覧");
        (await page.Id("Nav.Grip").CountAsync()).Should().Be(0, "畳んでいる間は幅を変えない");
        if (Environment.GetEnvironmentVariable("TT_SCREENSHOT_DIR") is { Length: > 0 } dir)
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "nav-collapsed.png") });
        await host.RestartAsync();
        await WaitUntilAsync(async () => await WidthAsync(page = host.Page) == 60, "起動し直しても畳んだまま");
        await page.ClickIdAsync("Nav.Toggle");
        await WaitUntilAsync(async () => await WidthAsync(page) == 250, "広げると前の幅");
    }

    [Fact]
    public async Task The_Account_Should_Show_Two_Lines_And_Offer_Its_Menu()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;

        // サインインする前: 「サインインする」だけ
        await page.ClickIdAsync("Nav.Account");
        await Assertions.Expect(page.Id("Account.Menu")).ToBeVisibleAsync();
        (await page.Locator("[data-id='Account.Menu'] [role=menuitem]").AllTextContentsAsync()).Should().Equal("サインインする");
        await page.ClickIdAsync("Menu.Account.SignIn");
        await page.ExpectTextAsync("SignIn.Account", "mock-user@example.com");

        // 名前は 2 行（ユーザー名・@ドメイン）。マウスを置くと全文
        await page.ExpectTextAsync("Nav.Account", "mock-user");
        await Assertions.Expect(page.Locator("[data-id='Nav.Account'] .tt-account-domain")).ToHaveTextAsync("@example.com");
        await Assertions.Expect(page.Id("Nav.Account")).ToHaveAttributeAsync("title", "mock-user@example.com");

        // 右クリックでメニュー（頭に全文。最初の項目にフォーカス）。↓ で次へ、Esc で閉じてアカウントの欄に戻る
        await page.Id("Nav.Account").ClickAsync(new() { Button = MouseButton.Right });
        await page.ExpectTextAsync("Account.Name", "mock-user@example.com");
        (await page.Locator("[data-id='Account.Menu'] [role=menuitem]").AllTextContentsAsync()).Should()
            .Equal("Azure Portal を開く", "アカウント名をコピー", "アカウントを切り替える…", "サインアウト…");
        await WaitUntilAsync(async () => await page.EvaluateAsync<string>("document.activeElement.textContent") == "Azure Portal を開く", "最初の項目にフォーカス");
        if (Environment.GetEnvironmentVariable("TT_SCREENSHOT_DIR") is { Length: > 0 } dir)
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "account-menu.png") });
        await page.Keyboard.PressAsync("ArrowDown");
        (await page.EvaluateAsync<string>("document.activeElement.textContent")).Should().Be("アカウント名をコピー");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Id("Account.Menu")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Id("Nav.Account")).ToBeFocusedAsync();

        // Azure Portal（サインインしたテナント）・アカウント名のコピー
        await page.ClickIdAsync("Nav.Account");
        await page.ClickIdAsync("Menu.Account.Portal");
        await WaitUntilAsync(() => Task.FromResult(host.Events().Contains((FixtureEvents.OpenUri, $"https://portal.azure.com/#@{FixtureSamples.TenantId}"))), "Azure Portal を開く");
        await page.ClickIdAsync("Nav.Account");
        await page.ClickIdAsync("Menu.Account.Copy");
        await WaitUntilAsync(async () => await host.ClipboardAsync() == "mock-user@example.com", "アカウント名をコピー");

        // メニューの外を押すと閉じる。「サインアウト…」でサインアウトし、サインインの画面に戻る
        await page.ClickIdAsync("Nav.Account");
        await page.Locator(".tt-overlay").ClickAsync(new() { Position = new() { X = 900, Y = 500 } });
        await Assertions.Expect(page.Id("Account.Menu")).ToHaveCountAsync(0);
        await page.ClickIdAsync("Nav.Account");
        await page.ClickIdAsync("Menu.Account.SignOut");
        await Assertions.Expect(page.Locator("[data-id='Nav.Account'] .tt-account-user")).ToHaveTextAsync("サインインしていません");
        await Assertions.Expect(page.Id("Panel.Target")).ToBeVisibleAsync();
    }
}
