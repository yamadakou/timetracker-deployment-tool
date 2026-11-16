using FluentAssertions;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// 左のメニューの幅と畳む・アカウントの 2 行の表示とメニュー（オーナーの依頼、2026-11-16。オーナーの判断: 幅の変更と畳むの両方、
/// メニューは Azure Portal・コピー・切り替え・サインアウト）。
/// </summary>
public sealed class AccountAndNavigationTests
{
    [Fact]
    public void The_Pane_Should_Start_With_The_Default_Or_Saved_Width()
    {
        var empty = new NavigationPaneViewModel(new InMemoryAppPreferences());
        (empty.Width, empty.IsCollapsed, empty.CurrentWidth, empty.ToggleText).Should().Be((240d, false, 240d, "メニューを畳む（アイコンだけにする）"));

        var saved = new NavigationPaneViewModel(new InMemoryAppPreferences { NavigationPane = new(300.4, true) });
        (saved.Width, saved.IsCollapsed, saved.CurrentWidth, saved.ToggleText).Should().Be((300d, true, 60d, "メニューを広げる"));

        // 壊れた値・範囲の外は、既定の幅か範囲の端
        new NavigationPaneViewModel(new InMemoryAppPreferences { NavigationPane = new(double.NaN, false) }).Width.Should().Be(240);
        new NavigationPaneViewModel(new InMemoryAppPreferences { NavigationPane = new(10, false) }).Width.Should().Be(180);
        new NavigationPaneViewModel(new InMemoryAppPreferences { NavigationPane = new(9999, false) }).Width.Should().Be(420);
    }

    [Fact]
    public void The_Pane_Should_Resize_Collapse_And_Save()
    {
        var preferences = new InMemoryAppPreferences();
        var pane = new NavigationPaneViewModel(preferences);
        var changes = new List<string?>();
        pane.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        pane.Resize(310.6);
        (pane.Width, preferences.NavigationPane).Should().Be((311d, new NavigationPaneLayout(311, false)));
        changes.Should().Equal(nameof(pane.Width), nameof(pane.IsCollapsed), nameof(pane.CurrentWidth), nameof(pane.ToggleText));
        pane.Nudge(-1);
        pane.Width.Should().Be(301);
        pane.Nudge(1);
        pane.Width.Should().Be(311);

        // 同じ値なら知らせない・保存しない
        changes.Clear();
        preferences.NavigationPane = null;
        pane.Resize(311);
        changes.Should().BeEmpty();
        preferences.NavigationPane.Should().BeNull();

        // 畳んでも幅は覚えておき、ドラッグか広げると戻る
        pane.Toggle();
        (pane.IsCollapsed, pane.CurrentWidth, preferences.NavigationPane).Should().Be((true, 60d, new NavigationPaneLayout(311, true)));
        pane.Toggle();
        pane.CurrentWidth.Should().Be(311);
        pane.Toggle();
        pane.Resize(200);
        (pane.IsCollapsed, pane.CurrentWidth).Should().Be((false, 200d));
        pane.Reset();
        pane.Width.Should().Be(240);
    }

    [Fact]
    public void The_Pane_Should_Be_Saved_In_The_Preferences_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tt-nav-{Guid.NewGuid():N}.json");
        try
        {
            new FilePreferences(path).NavigationPane = new NavigationPaneLayout(280, true);
            new FilePreferences(path).NavigationPane.Should().Be(new NavigationPaneLayout(280, true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_Account_Should_Be_Shown_In_Two_Lines_With_Its_Portal()
    {
        var h = new Harness();
        var signIn = h.Main.SignIn;
        (signIn.UserLine, signIn.DomainLine, signIn.PortalUri.ToString()).Should().Be(("サインインしていません", (string?)null, "https://portal.azure.com/"));

        var changes = new List<string?>();
        signIn.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        await signIn.SignInCommand.ExecuteAsync();
        (signIn.UserLine, signIn.DomainLine, signIn.PortalUri.ToString()).Should().Be(("alice", "@example.com", "https://portal.azure.com/#@home"));
        changes.Should().Contain([nameof(signIn.UserLine), nameof(signIn.DomainLine), nameof(signIn.PortalUri)]);
    }

    [Theory]
    [InlineData("user", "user", null, null)]
    [InlineData("@example.com", "@example.com", null, "")]
    [InlineData("a@b@example.com", "a@b", "@example.com", null)]
    public async Task Names_Without_A_Domain_Should_Stay_On_One_Line(string name, string user, string? domain, string? tenant)
    {
        var h = new Harness();
        h.SignIn.Restored = new SignedInAccount(name, tenant);
        await h.Main.SignIn.RestoreAsync();
        (h.Main.SignIn.UserLine, h.Main.SignIn.DomainLine).Should().Be((user, domain));
        h.Main.SignIn.PortalUri.ToString().Should().Be("https://portal.azure.com/", "テナントが分からなければ既定のディレクトリー");
    }

    [Fact]
    public async Task The_Account_Menu_Should_Offer_Sign_In_Or_The_Account_Operations()
    {
        var h = new Harness(preferences: new InMemoryAppPreferences { NavigationPane = new(300, false) });
        h.Main.NavigationPane.Width.Should().Be(300, "保存した左のメニューの幅で始める");
        h.Main.AccountMenu().Select(i => (i.Id, i.Label, i.IsEnabled)).Should().Equal(("Account.SignIn", "サインインする", true));
        await h.Main.AccountMenu()[0].Run();
        h.Main.SignIn.IsSignedIn.Should().BeTrue();

        var menu = h.Main.AccountMenu();
        menu.Select(i => (i.Id, i.Label, i.IsEnabled, i.StartsGroup)).Should().Equal(
            ("Account.Portal", "Azure Portal を開く", true, false), ("Account.Copy", "アカウント名をコピー", true, false),
            ("Account.Switch", "アカウントを切り替える…", true, true), ("Account.SignOut", "サインアウト…", true, false));
        await menu[0].Run();
        h.Launcher.Opened.Should().Equal("https://portal.azure.com/#@home");
        await menu[1].Run();
        h.Clipboard.Copied.Should().Equal("alice@example.com");

        // 切り替え: サインアウトしてから、サインインし直す（ジョブが無ければ尋ねない）
        await menu[2].Run();
        (h.SignIn.SignOuts, h.Main.SignIn.IsSignedIn, h.Dialogs.Confirmations.Count).Should().Be((1, true, 0));
        await h.Main.AccountMenu()[3].Run();
        (h.SignIn.SignOuts, h.Main.SignIn.IsSignedIn).Should().Be((2, false));

        // コピーの先が無ければ、コピーは押せない
        var noClipboard = new Harness(withClipboard: false);
        await noClipboard.Main.SignIn.SignInCommand.ExecuteAsync();
        noClipboard.Main.AccountMenu()[1].IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Signing_Out_Or_Switching_With_Unfinished_Jobs_Should_Ask_First()
    {
        var h = await new Harness().ReadyAsync();
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "demo"), DateTimeOffset.UtcNow.AddHours(1), () => new TaskCompletionSource<Job>().Task);
        string? message = null;
        h.Dialogs.Confirm = false;

        // 「いいえ」なら、サインアウトも切り替えもしない
        await h.Main.SignIn.SwitchAccountCommand.ExecuteAsync();
        await h.Main.SignIn.SignOutCommand.ExecuteAsync();
        (h.SignIn.SignOuts, h.Main.SignIn.IsSignedIn).Should().Be((0, true));
        h.Dialogs.Confirmations.Should().Equal("終わっていないジョブがあります", "終わっていないジョブがあります");
        message = h.Dialogs.Messages[^2];
        message.Should().Be("ジョブ（予定 1）があります。サインアウトすると、これらのジョブは Azure を操作できずに失敗することがあります" +
                            "（切り替えた後のアカウントに権限が無い場合も失敗します）。アカウントを切り替えますか？");
        h.Dialogs.Messages[^1].Should().EndWith("失敗することがあります。サインアウトしますか？");

        h.Dialogs.Confirm = true;
        await h.Main.SignIn.SwitchAccountCommand.ExecuteAsync();
        (h.SignIn.SignOuts, h.Main.SignIn.IsSignedIn).Should().Be((1, true));
        await h.Main.SignIn.SignOutCommand.ExecuteAsync();
        (h.SignIn.SignOuts, h.Main.SignIn.IsSignedIn).Should().Be((2, false));
        h.Main.SignIn.SwitchAccountCommand.CanExecute(null).Should().BeFalse("サインインしていなければ切り替えられない");
    }
}
