using Azure;
using FluentAssertions;
using Moq;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

public class DeletionViewModelTests
{
    [Fact]
    public async Task Plan_Should_Not_Read_Azure_When_The_Target_Is_Wrong()
    {
        var h = new Harness();

        await h.Main.Deletion.PlanCommand.ExecuteAsync();

        h.Main.Deletion.Error.Should().Be("サブスクリプションを選んでください。");
        h.Main.Deletion.KeptText.Should().BeNull();
        h.Azure.Credentials.Should().BeEmpty();
    }

    [Fact]
    public async Task Deletion_Should_Require_The_App_Name_And_Run_To_The_End()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        h.Main.Target.AppName = "Demo";
        var vm = h.Main.Deletion;

        await vm.PlanCommand.ExecuteAsync();

        vm.Targets.Select(t => (t.Name, t.Resource, t.Decision)).Should().Equal(
            ("demo-tt", "Container App", "削除する"), ("demo-redis", "Container App", "削除する"),
            ("demo-db", "Container App", "削除する"), ("demo-env", "Container Apps Environment", "削除する"),
            (FakeAzure.StorageAccountName, "ストレージアカウント（DB のデータ）", "削除しない"));
        vm.Warnings.Should().Equal(DeletionViewModel.DbDataLossWarning);
        vm.KeptText.Should().StartWith("リソースグループ rg は削除しません");
        vm.AppNameToConfirm.Should().Be("demo");
        vm.Note.Should().BeNull();
        vm.CanStart.Should().BeFalse();

        vm.ConfirmAppName = "Demo";
        vm.StartCommand.CanExecute(null).Should().BeFalse();
        vm.ConfirmAppName = "demo";
        vm.ConfirmAppName = "demo";
        vm.StartCommand.CanExecute(null).Should().BeTrue();
        await vm.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        var job = h.Main.CurrentJob!;
        job.Title.Should().Be("削除（rg / demo）");
        job.StatusText.Should().Be("完了");
        job.DeletionSummary.Should().Be("削除したもの: demo-tt, demo-redis, demo-db, demo-env。既に無かったもの: なし。リソースグループ rg は残しています。");
        job.Logs.Should().Contain(l => l.EndsWith("削除中: demo-tt"));
        vm.HasPlan.Should().BeFalse();
        vm.ConfirmAppName.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Resource_Group_Should_Be_Deleted_Last_When_Chosen()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        var vm = h.Main.Deletion;
        await vm.PlanCommand.ExecuteAsync();
        vm.KeptText.Should().Contain("「リソースグループも削除する」にチェックを入れて");

        vm.DeleteResourceGroup = true;
        vm.DeleteResourceGroup = true;
        vm.HasPlan.Should().BeFalse("選び直したら計画し直す");
        await vm.PlanCommand.ExecuteAsync();

        vm.Targets.Last().Should().Be(new DeletionRow("rg", "リソースグループ", "削除する", null, true));
        vm.Warnings.Should().Contain("リソースグループ rg も削除します。元に戻せません。");
        vm.KeptText.Should().BeNull();
        vm.ConfirmAppName = "demo";
        await vm.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        var job = h.Main.CurrentJob!;
        job.StatusText.Should().Be("完了");
        job.DeletionSummary.Should().EndWith("リソースグループ rg も削除しました。");
        h.Azure.GatewayMock.Verify(g => g.DeleteResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_Resource_Group_Should_Not_Be_Deleted_With_Other_Resources()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        h.Azure.Resources.Add(new AzureResourceInfo("vm1", "Microsoft.Compute/virtualMachines"));
        var vm = h.Main.Deletion;
        vm.DeleteResourceGroup = true;

        await vm.PlanCommand.ExecuteAsync();

        vm.Targets.Last().Should().Be(new DeletionRow("rg", "リソースグループ", "削除しない", "ほかのリソースが残ります: vm1（Microsoft.Compute/virtualMachines）", false));
        vm.Blockers.Should().ContainSingle().Which.Should().EndWith("「リソースグループも削除する」のチェックを外してください。");
        vm.AppNameToConfirm.Should().BeNull("実行できない");
        vm.KeptText.Should().StartWith("リソースグループ rg は削除しません");
    }

    [Fact]
    public async Task Untagged_Resources_Should_Be_Kept_Unless_Included()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Apps.Add(new ContainerAppInfo("demo-tt", FakeAzure.EnvId, new Dictionary<string, string>()));
        h.Azure.GatewayMock.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var vm = h.Main.Deletion;

        await vm.PlanCommand.ExecuteAsync();
        vm.Targets[0].Decision.Should().Be("削除しない");
        vm.Targets[0].Reason.Should().Be("管理タグがありません");
        vm.Warnings.Should().Equal(DeletionViewModel.UnmanagedWarning);
        vm.Note.Should().Be("削除するリソースはありません。");
        vm.AppNameToConfirm.Should().BeNull();

        vm.IncludeUntagged = true;
        vm.IncludeUntagged = true;
        vm.HasPlan.Should().BeFalse();
        await vm.PlanCommand.ExecuteAsync();
        vm.Targets[0].WillDelete.Should().BeTrue();
        vm.ConfirmAppName = "demo";
        await vm.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        h.Main.CurrentJob!.DeletionSummary.Should().StartWith("削除したもの: なし。既に無かったもの: demo-tt。");
    }

    [Fact]
    public async Task Permission_Problems_Should_Be_Shown()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        var vm = h.Main.Deletion;

        h.Azure.PermissionsError = new RequestFailedException(403, "拒否");
        await vm.PlanCommand.ExecuteAsync();
        vm.Warnings.Should().Contain(w => w.StartsWith("権限を事前に確認できませんでした"));

        h.Azure.PermissionsError = null;
        h.Azure.Permissions[0] = new PermissionSet(["*/read"], []);
        await vm.PlanCommand.ExecuteAsync();
        vm.Blockers.Should().ContainSingle().Which.Should().StartWith("権限が不足しています");
        vm.AppNameToConfirm.Should().BeNull();
    }

    [Fact]
    public async Task Failures_Should_Be_Shown_And_Discard_The_Plan()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        var vm = h.Main.Deletion;
        await vm.PlanCommand.ExecuteAsync();
        h.Azure.Apps.RemoveAt(0);

        vm.ConfirmAppName = "demo";
        await vm.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        vm.HasPlan.Should().BeFalse();
        h.Main.CurrentDeletion!.Error.Should().Be("実行しませんでした: " + DesktopMessages.Changed);

        h.Azure.GatewayMock.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(500, "障害"));
        await vm.PlanCommand.ExecuteAsync();
        vm.Error.Should().StartWith("Azure がエラーを返しました（500");
    }

    [Fact]
    public async Task Changing_The_Target_Should_Discard_The_Plan()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        await h.Main.Deletion.PlanCommand.ExecuteAsync();

        h.Main.Target.ResourceGroup = "rg2";

        h.Main.Deletion.HasPlan.Should().BeFalse();
    }
}

public class StatusViewModelTests
{
    private static ContainerAppDetails App(string name, string? fqdn = null) =>
        new(name, "Succeeded", "Running", "img:1", 1.5, "3Gi", name + "--r1", fqdn, FakeAzure.Managed);

    [Fact]
    public async Task Refresh_Should_Not_Read_Azure_When_The_Target_Is_Wrong()
    {
        var h = new Harness();

        await h.Main.Status.RefreshCommand.ExecuteAsync();

        h.Main.Status.Error.Should().Be("サブスクリプションを選んでください。");
        h.Main.Status.Summary.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_Should_Show_The_Deployed_Apps_And_The_Url()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        h.Azure.AppDetails.Add(App("demo-tt", "demo-tt.example.io"));
        h.Azure.AppDetails.Add(App("demo-db") with { Tags = new Dictionary<string, string>() });
        var vm = h.Main.Status;

        await vm.RefreshCommand.ExecuteAsync();

        vm.Summary.Should().Be("Container Apps Environment demo-env（japaneast、Succeeded）");
        vm.Endpoint.Should().Be(new Uri("https://demo-tt.example.io"));
        vm.Apps.Should().Equal(
            new AppStateRow("Timetracker", "demo-tt", "Succeeded / Running", "img:1", "1.5 vCPU / 3Gi", "demo-tt--r1", true),
            new AppStateRow("DB", "demo-db", "Succeeded / Running", "img:1", "1.5 vCPU / 3Gi", "demo-db--r1", false));

        h.Main.Target.AppName = "other";
        vm.Apps.Should().BeEmpty();
        vm.Endpoint.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_Should_Explain_Missing_Resources()
    {
        var h = await new Harness().ReadyAsync();
        var vm = h.Main.Status;

        await vm.RefreshCommand.ExecuteAsync();
        vm.Summary.Should().Be("デプロイされた環境はありません。");

        h.Azure.AppDetails.Add(App("demo-redis"));
        await vm.RefreshCommand.ExecuteAsync();
        vm.Summary.Should().Be("Container Apps Environment はありません。");
        vm.Apps.Should().ContainSingle().Which.Role.Should().Be("Redis");
    }

    [Fact]
    public async Task Refresh_Failures_Should_Be_Shown()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.GatewayMock.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(403, "拒否"));

        await h.Main.Status.RefreshCommand.ExecuteAsync();

        h.Main.Status.Error.Should().StartWith("Azure がエラーを返しました（403");
    }
}

public class InitialStateTests
{
    [Fact]
    public void Nothing_Should_Be_Runnable_Before_A_Plan()
    {
        var h = new Harness();

        h.Main.Deployment.CanStart.Should().BeFalse();
        h.Main.Deployment.RequiresDataLossAck.Should().BeFalse();
        h.Main.Deployment.EstimatedDuration.Should().BeNull();
        h.Main.Deployment.ExpiresAt.Should().BeNull();
        h.Main.Deletion.CanStart.Should().BeFalse();
        h.Main.Deletion.AppNameToConfirm.Should().BeNull();
        h.Main.OpenEndpointCommand.CanExecute(null).Should().BeFalse();
        h.Main.CurrentJob.Should().BeNull();
        SettingsViewModel.DbTypes.Should().Equal("postgresql", "sqlserver");
    }

    [Fact]
    public void The_Default_Poll_Interval_Should_Be_Used_When_Not_Given()
    {
        var main = new MainViewModel(new FakeSignIn(), new FakeAzure(), new FakeDialogs(), new FakeLauncher(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        main.Jobs.Should().BeEmpty();
        JobViewModel.DefaultPollInterval.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_Target_Without_Listeners_Should_Use_The_Account_Credential_When_Nothing_Is_Selected()
    {
        var signIn = new FakeSignIn();
        var azure = new FakeAzure();
        var target = new TargetViewModel(signIn, azure, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) { ResourceGroup = "rg" };

        target.CreateSession().CreateGateway(new DesktopDeploymentLogger(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));

        azure.Credentials.Should().Equal(signIn.Credential);
    }

    [Fact]
    public async Task Jobs_Without_Steps_Should_Have_No_Step_Text()
    {
        var release = new TaskCompletionSource();
        var jobs = new TTDeployTool.Application.Jobs.JobManager(new TTDeployTool.Application.Jobs.InMemoryJobStore(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TTDeployTool.Application.Jobs.JobManager>.Instance);
        var job = jobs.TryStart(TTDeployTool.Application.Jobs.JobKind.Deploy, new("sub", "rg", "demo"), "local", async _ =>
        {
            await release.Task;
            return null;
        }).Job!;

        var vm = new JobViewModel(job);

        vm.StepText.Should().BeNull();
        release.SetResult();
        await vm.WatchAsync(TimeProvider.System, TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        vm.StatusText.Should().Be("完了");
        vm.Endpoint.Should().BeNull();
    }
}
