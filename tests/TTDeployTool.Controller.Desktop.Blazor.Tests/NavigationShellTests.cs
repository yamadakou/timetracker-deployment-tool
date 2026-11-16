using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using TTDeployTool.Controller.Desktop.Blazor.Shell;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>
/// 左のメニューの幅・畳む・アカウントの 2 行とメニュー（オーナーの依頼、2026-11-16）の画面の側。
/// </summary>
public sealed class NavigationShellTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private IRenderedComponent<DesktopShell> Render() => _app.Context.Render<DesktopShell>();

    private static IElement Id(IRenderedComponent<DesktopShell> shell, string id) => shell.Find($"[data-id='{id}']");

    private bool Focused(string selector) => _app.Module.Invocations.Any(i => i.Identifier == "focusSelector" && (string)i.Arguments[0]! == selector);

    [Fact]
    public void The_Pane_Should_Resize_With_The_Grip_And_Collapse()
    {
        var shell = Render();
        var pane = _app.ViewModel.NavigationPane;
        _app.Module.Invocations.Should().Contain(i => i.Identifier == "attachNavGrip", "つまみのドラッグを JavaScript につなぐ");
        Id(shell, "Nav").GetAttribute("style").Should().Be("width: 240px");
        Id(shell, "Nav.Grip").GetAttribute("aria-valuenow").Should().Be("240");

        // ドラッグ（JavaScript から）・←/→（ほかのキーでは変えない）・ダブルクリックで既定の幅
        shell.InvokeAsync(() => shell.Instance.NavResized(333.3));
        Id(shell, "Nav").GetAttribute("style").Should().Be("width: 333px");
        Id(shell, "Nav.Grip").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        pane.Width.Should().Be(323);
        Id(shell, "Nav.Grip").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        Id(shell, "Nav.Grip").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        pane.Width.Should().Be(333);
        Id(shell, "Nav.Grip").DoubleClick();
        pane.Width.Should().Be(240);

        // 畳む: アイコンだけ（名前はマウスを置くと出る）、つまみは出さない。もう一度で広げる
        Id(shell, "Nav.Toggle").GetAttribute("aria-expanded").Should().Be("true");
        Id(shell, "Nav.Toggle").Click();
        Id(shell, "Nav").ClassList.Should().Contain("tt-nav-collapsed");
        Id(shell, "Nav").GetAttribute("style").Should().Be("width: 60px");
        (Id(shell, "Nav.Toggle").GetAttribute("aria-expanded"), Id(shell, "Nav.Toggle").GetAttribute("title")).Should().Be(("false", "メニューを広げる"));
        Id(shell, "Nav.Jobs").GetAttribute("title").Should().Be("ジョブの一覧");
        shell.FindAll("[data-id='Nav.Grip']").Should().BeEmpty();
        Id(shell, "Nav.Toggle").Click();
        Id(shell, "Nav.Jobs").HasAttribute("title").Should().BeFalse();
    }

    [Fact]
    public async Task The_Account_Should_Show_Two_Lines_And_Its_Menu()
    {
        var shell = Render();
        // サインインする前: 1 行（ドメインなし）。メニューは「サインインする」だけ
        shell.FindAll("[data-id='Nav.Account'] .tt-account-domain").Should().BeEmpty();
        Id(shell, "Nav.Account").Click();
        shell.FindAll("[data-id='Account.Menu'] [role=menuitem]").Select(b => b.TextContent).Should().Equal("サインインする");
        shell.WaitForAssertion(() => Focused("[data-id='Account.Menu'] [role='menuitem']:not([disabled])").Should().BeTrue());
        Id(shell, "Menu.Account.SignIn").Click();
        shell.WaitForAssertion(() => _app.ViewModel.SignIn.IsSignedIn.Should().BeTrue());
        shell.FindAll("[data-id='Account.Menu']").Should().BeEmpty();

        var account = _app.ViewModel.SignIn.Account!.UserName;
        shell.WaitForAssertion(() => Id(shell, "Nav.Account").GetAttribute("title").Should().Be(account));
        Id(shell, "Nav.Account").QuerySelector(".tt-account-user")!.TextContent.Should().Be(account[..account.IndexOf('@')]);
        Id(shell, "Nav.Account").QuerySelector(".tt-account-domain")!.TextContent.Should().Be(account[account.IndexOf('@')..]);

        // 右クリックでも開く。頭に全文、区切りの後に切り替え・サインアウト。ほかのキーでは閉じず、Esc で閉じてアカウントの欄に戻る
        await Id(shell, "Nav.Account").ContextMenuAsync(new MouseEventArgs());
        Id(shell, "Account.Name").TextContent.Should().Be(account);
        Id(shell, "Nav.Account").GetAttribute("aria-expanded").Should().Be("true");
        shell.FindAll("[data-id='Account.Menu'] .tt-menu-sep").Should().ContainSingle();
        Id(shell, "Account.Menu").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        shell.FindAll("[data-id='Account.Menu']").Should().ContainSingle();
        Id(shell, "Account.Menu").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        shell.FindAll("[data-id='Account.Menu']").Should().BeEmpty();
        shell.WaitForAssertion(() => Focused("[data-id='Nav.Account']").Should().BeTrue());

        // メニューの外を押す・右クリックで閉じる
        Id(shell, "Nav.Account").Click();
        shell.Find(".tt-overlay").Click();
        shell.FindAll("[data-id='Account.Menu']").Should().BeEmpty();
        Id(shell, "Nav.Account").Click();
        shell.Find(".tt-overlay").ContextMenu(new MouseEventArgs());
        shell.FindAll("[data-id='Account.Menu']").Should().BeEmpty();

        // アカウント名をコピー
        Id(shell, "Nav.Account").Click();
        Id(shell, "Menu.Account.Copy").Click();
        _app.Clipboard.Texts[^1].Should().Be(account);
    }

    [Fact]
    public async Task The_Shell_Should_Release_Its_Script_When_Closed()
    {
        var shell = Render();
        await shell.InvokeAsync(() => shell.Instance.DisposeAsync().AsTask());
        _app.Module.Invocations.Should().Contain(i => i.Identifier == "attachNavGrip");
    }
}
