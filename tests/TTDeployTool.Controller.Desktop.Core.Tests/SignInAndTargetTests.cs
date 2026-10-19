using Azure;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>画面全体を、Azure とサインインの代わりで組み立てる。</summary>
internal sealed class Harness
{
    public FakeSignIn SignIn { get; } = new();
    public FakeAzure Azure { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeLauncher Launcher { get; } = new();
    public MainViewModel Main { get; }

    public Harness(Microsoft.Extensions.Logging.ILoggerFactory? loggers = null)
    {
        Main = new MainViewModel(SignIn, Azure, Dialogs, Launcher, TimeProvider.System, loggers ?? NullLoggerFactory.Instance, TimeSpan.FromMilliseconds(5));
    }

    /// <summary>サインインして、対象（sub / rg / demo）と正しい設定を入れる。</summary>
    public async Task<Harness> ReadyAsync()
    {
        await Main.SignIn.SignInCommand.ExecuteAsync();
        Main.Target.ResourceGroup = "rg";
        Main.Target.AppName = "demo";
        Main.Settings.DbPassword = "DbPassw0rd!";
        Main.Settings.TrackerPassword = "TrackerPass!";
        return this;
    }
}

public class SignInViewModelTests
{
    [Fact]
    public async Task Restore_Should_Sign_In_Silently_And_Load_Subscriptions()
    {
        var h = new Harness();
        h.SignIn.Restored = FakeSignIn.Alice;
        var vm = h.Main.SignIn;
        vm.AccountDisplay.Should().StartWith("サインインしていません");

        await h.Main.InitializeAsync();

        vm.IsSignedIn.Should().BeTrue();
        vm.Account.Should().Be(FakeSignIn.Alice);
        vm.AccountDisplay.Should().Be("alice@example.com");
        vm.SignInCommand.CanExecute(null).Should().BeFalse();
        vm.SignOutCommand.CanExecute(null).Should().BeTrue();
        h.Main.Target.Subscriptions.Should().ContainSingle();
    }

    [Fact]
    public async Task Restore_Should_Stay_Signed_Out_When_Nothing_Was_Saved()
    {
        var h = new Harness();

        await h.Main.InitializeAsync();

        h.Main.SignIn.IsSignedIn.Should().BeFalse();
        h.Main.SignIn.Error.Should().BeNull();
    }

    [Fact]
    public async Task Restore_Failures_Should_Be_Shown()
    {
        var h = new Harness();
        h.SignIn.RestoreError = new RequestFailedException("x");

        await h.Main.InitializeAsync();

        h.Main.SignIn.Error.Should().StartWith("Azure がエラーを返しました");
    }

    [Fact]
    public async Task Sign_In_Should_Ignore_Cancellation_And_Show_Failures()
    {
        var h = new Harness();
        var vm = h.Main.SignIn;

        h.SignIn.SignInError = new OperationCanceledException();
        await vm.SignInCommand.ExecuteAsync();
        vm.Error.Should().BeNull();

        h.SignIn.SignInError = new InvalidOperationException("WAM が使えません");
        await vm.SignInCommand.ExecuteAsync();
        vm.Error.Should().Be("エラーが発生しました: WAM が使えません");
        vm.IsSignedIn.Should().BeFalse();
    }

    [Fact]
    public async Task Sign_Out_Should_Clear_Subscriptions_Even_When_It_Fails()
    {
        var h = await new Harness().ReadyAsync();
        h.SignIn.SignOutError = new InvalidOperationException("消せません");

        await h.Main.SignIn.SignOutCommand.ExecuteAsync();

        h.SignIn.SignOuts.Should().Be(1);
        h.Main.SignIn.IsSignedIn.Should().BeFalse();
        h.Main.SignIn.Error.Should().Be("エラーが発生しました: 消せません");
        h.Main.Target.Subscriptions.Should().BeEmpty();
        h.Main.Target.SelectedSubscription.Should().BeNull();

        h.SignIn.SignOutError = null;
        await h.Main.SignIn.SignInCommand.ExecuteAsync();
        await h.Main.SignIn.SignOutCommand.ExecuteAsync();
        h.Main.SignIn.Error.Should().BeNull();
    }
}

public class TargetViewModelTests
{
    [Fact]
    public async Task Load_Should_List_Subscriptions_Of_Every_Tenant_And_Note_Unreadable_Tenants()
    {
        var h = new Harness();
        h.Azure.TenantList.Add(new TenantInfo("t2", null, "other.example.com"));
        h.Azure.TenantList.Add(new TenantInfo("t3", null, null));
        h.Azure.TenantList.Add(new TenantInfo("t4", "MFA が必要", null));
        h.Azure.SubscriptionsByTenant["t2"] = [new("sub2", "あ検証", "Enabled", "t2")];
        h.Azure.TenantErrors["t4"] = new AuthenticationFailedException("AADSTS50076: mfa");
        await h.Main.SignIn.SignInCommand.ExecuteAsync();
        var vm = h.Main.Target;

        vm.Subscriptions.Select(s => s.Display).Should().Equal(
            "あ検証（sub2、other.example.com）",
            "検証（sub、開発）");
        // 2 つ以上あれば選ばない
        vm.SelectedSubscription.Should().BeNull();
        vm.Notices.Should().ContainSingle().Which.Should().StartWith("テナント MFA が必要 のサブスクリプションを読めませんでした").And.Contain("多要素認証");
        vm.Error.Should().BeNull();

        vm.SelectedSubscription = vm.Subscriptions[1];
        await vm.LoadCommand.ExecuteAsync();
        vm.SelectedSubscription!.SubscriptionId.Should().Be("sub");
    }

    [Fact]
    public async Task Load_Should_Show_An_Error_When_Tenants_Cannot_Be_Read()
    {
        var h = new Harness();
        h.Azure.TenantsError = new RequestFailedException(401, "だめ");

        await h.Main.SignIn.SignInCommand.ExecuteAsync();

        h.Main.Target.Error.Should().StartWith("Azure がエラーを返しました（401");
        h.Main.Target.Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Target_Errors_Should_Be_Checked_In_Order()
    {
        var h = new Harness();
        var vm = h.Main.Target;
        vm.TargetError.Should().Be("サブスクリプションを選んでください。");

        await h.Main.SignIn.SignInCommand.ExecuteAsync();
        vm.Subscription.Should().Be("sub");
        vm.TargetError.Should().Be("リソースグループ名を入力してください。");

        vm.ResourceGroup = "  rg ";
        vm.ResourceGroup.Should().Be("rg");
        vm.AppName = "x";
        vm.TargetError.Should().NotBeNull();
        vm.AppName = "Demo";
        vm.TargetError.Should().BeNull();
        vm.Location = " japaneast ";
        vm.Location.Should().Be("japaneast");
    }

    [Fact]
    public void Setting_The_Same_Value_Should_Not_Notify()
    {
        var vm = new Harness().Main.Target;
        var changed = 0;
        vm.Changed += (_, _) => changed++;
        var changes = new ChangeRecorder(vm);

        vm.AppName = "timetracker";
        vm.ResourceGroup = "rg";

        changed.Should().Be(1);
        changes.Names.Should().Equal("ResourceGroup", "Subscription", "TargetError");
    }

    [Fact]
    public async Task Sessions_Should_Use_The_Tenant_Of_The_Selected_Subscription()
    {
        var h = await new Harness().ReadyAsync();
        var vm = h.Main.Target;

        vm.CreateSession().CreateGateway(new DesktopDeploymentLogger(NullLogger.Instance));
        vm.SelectedSubscription = new SubscriptionChoice("sub", "検証", null, null, null);
        vm.SelectedSubscription.Display.Should().Be("検証（sub）");
        vm.CreateSession().CreateGateway(new DesktopDeploymentLogger(NullLogger.Instance));

        h.Azure.Credentials[0].Should().BeOfType<TenantScopedCredential>().Which.TenantId.Should().Be("t1");
        h.Azure.Credentials[1].Should().BeSameAs(h.SignIn.Credential);
    }
}
