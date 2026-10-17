using Azure;
using FluentAssertions;
using Timetracker.Application.Jobs;
using Timetracker.Controller.Desktop.Core.ViewModels;
using Timetracker.Domain.Azure;
using Timetracker.Infrastructure.Azure;
using Moq;
using Xunit;

namespace Timetracker.Controller.Desktop.Core.Tests;

public class DeploymentViewModelTests
{
    [Fact]
    public async Task Plan_Should_Not_Read_Azure_When_Settings_Are_Wrong()
    {
        var h = new Harness();
        var vm = h.Main.Deployment;

        await vm.PlanCommand.ExecuteAsync();

        vm.Error.Should().StartWith("設定に誤りがあります:\n- subscription は必須です。");
        vm.HasPlan.Should().BeFalse();
        h.Azure.Credentials.Should().BeEmpty();
    }

    [Fact]
    public async Task A_New_Deployment_Should_Be_Planned_And_Run_To_The_End()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        var vm = h.Main.Deployment;

        await vm.PlanCommand.ExecuteAsync();

        vm.Error.Should().BeNull();
        vm.HasPlan.Should().BeTrue();
        vm.Operations.Should().Equal(
            new OperationRow("リソースグループ", "rg", "作成する", false),
            new OperationRow("Container Apps Environment", "demo-env", "作成する", false),
            new OperationRow("Container App", "demo-db", "作成する", false),
            new OperationRow("Container App", "demo-redis", "作成する", false),
            new OperationRow("Container App", "demo-tt", "作成する", false));
        vm.PermissionText.Should().StartWith("権限: 確認できませんでした（リソースグループ 'rg' がまだ無い");
        vm.Warnings.Should().ContainSingle();
        vm.EstimatedDuration.Should().Contain("Environment を新規作成");
        vm.ExpiresAt.Should().NotBeNull();
        vm.RequiresDataLossAck.Should().BeFalse();
        vm.CanStart.Should().BeTrue();

        await vm.StartCommand.ExecuteAsync();

        vm.HasPlan.Should().BeFalse();
        var job = h.Main.CurrentJob!;
        job.IsRunning.Should().BeFalse();
        job.StatusText.Should().Be("完了");
        job.Title.Should().Be("デプロイ（rg / demo）");
        job.Endpoint.Should().Be(new Uri("https://demo-tt.example.io"));
        job.DeletionSummary.Should().BeNull();
        job.Error.Should().BeNull();
        h.Main.Jobs.Should().Equal(job);
        h.Main.HasRunningJob.Should().BeFalse();

        await h.Main.OpenEndpointCommand.ExecuteAsync();
        h.Launcher.Opened.Should().Equal("https://demo-tt.example.io/");
    }

    [Fact]
    public async Task Updating_The_Db_Should_Require_The_Data_Loss_Check()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        var vm = h.Main.Deployment;

        await vm.PlanCommand.ExecuteAsync();

        vm.Operations.Select(o => o.Action).Should().Equal(
            "既存のものを使う（変更しない）", "既存のものを使う（変更しない）", "更新する（DB のデータが失われるおそれがあります）", "更新する", "更新する");
        vm.PermissionText.Should().Be("権限: 確認しました（不足はありません）");
        vm.RequiresDataLossAck.Should().BeTrue();
        vm.CanStart.Should().BeFalse();
        vm.StartCommand.CanExecute(null).Should().BeFalse();

        vm.DataLossAcknowledged = true;
        vm.DataLossAcknowledged = true;
        vm.StartCommand.CanExecute(null).Should().BeTrue();
        await vm.StartCommand.ExecuteAsync();

        h.Main.CurrentJob!.StatusText.Should().Be("完了");
        vm.DataLossAcknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_Permissions_Should_Block_The_Plan()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Permissions[0] = new PermissionSet(["*/read"], []);
        var vm = h.Main.Deployment;

        await vm.PlanCommand.ExecuteAsync();

        vm.PermissionText.Should().StartWith("権限: 不足しています（Microsoft.App/containerApps/write");
        vm.Blockers.Should().ContainSingle().Which.Should().StartWith("権限が不足しています");
        vm.CanStart.Should().BeFalse();
        vm.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Azure_Failures_While_Planning_Should_Be_Shown()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.GatewayMock.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(403, "拒否"));

        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        h.Main.Deployment.Error.Should().StartWith("Azure がエラーを返しました（403");
    }

    [Fact]
    public async Task Changes_After_The_Plan_Should_Stop_The_Run_And_Discard_The_Plan()
    {
        var h = await new Harness().ReadyAsync();
        var vm = h.Main.Deployment;
        await vm.PlanCommand.ExecuteAsync();
        h.Azure.Apps.Add(new ContainerAppInfo("demo-redis", FakeAzure.EnvId, FakeAzure.Managed));

        await vm.StartCommand.ExecuteAsync();

        vm.Error.Should().Be(DesktopMessages.Changed);
        vm.HasPlan.Should().BeFalse();
        h.Main.CurrentJob.Should().BeNull();
    }

    [Fact]
    public async Task Changing_The_Target_Or_Settings_Should_Discard_The_Plan()
    {
        var h = await new Harness().ReadyAsync();
        var vm = h.Main.Deployment;

        await vm.PlanCommand.ExecuteAsync();
        h.Main.Target.AppName = "demo2";
        vm.HasPlan.Should().BeFalse();

        h.Main.Target.AppName = "demo";
        await vm.PlanCommand.ExecuteAsync();
        h.Main.Settings.DbName = "other";
        vm.HasPlan.Should().BeFalse();
        vm.PermissionText.Should().BeNull();
    }
}

public class JobViewModelTests
{
    [Fact]
    public async Task Running_Jobs_Should_Show_Progress_Until_They_End()
    {
        var release = new TaskCompletionSource();
        var h = await new Harness().ReadyAsync();
        h.Azure.Release = release.Task;
        h.Azure.DeployedUrl = null;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        var start = h.Main.Deployment.StartCommand.ExecuteAsync();
        var job = h.Main.CurrentJob!;
        job.IsRunning.Should().BeTrue();
        h.Main.HasRunningJob.Should().BeTrue();
        job.StatusText.Should().Be("実行中");
        // 実行中は、閉じる前に確認する
        (await h.Main.ConfirmCloseAsync()).Should().BeFalse();
        h.Dialogs.Confirmations.Should().Equal("実行中の処理があります");
        // 画面の項目は、読み直しの間隔（5 ms）ごとに更新される
        for (var i = 0; i < 500 && job.StepText == null; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        job.StepText.Should().Be("1 / 3: リソースグループ rg");
        release.SetResult();
        await start;

        job.StepText.Should().StartWith("3 / 3");
        job.Endpoint.Should().BeNull();
        h.Main.OpenEndpointCommand.CanExecute(null).Should().BeFalse();
        (await h.Main.ConfirmCloseAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Failed_Jobs_Should_Explain_What_To_Do()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.GatewayMock.Setup(g => g.EnsureResourceGroupAsync("sub", "rg", "japaneast", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("作成できません"));
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        await h.Main.Deployment.StartCommand.ExecuteAsync();

        var job = h.Main.CurrentJob!;
        job.StatusText.Should().Be("失敗");
        job.Error.Should().Be("リソースグループ rg（手順 1/3）で失敗しました: 作成できません\n途中まで作成・削除したリソースは、もう一度実行すると続きから処理します（状態は「状態の確認」で見られます）。");
    }

    [Fact]
    public async Task Watching_Should_Stop_When_Cancelled()
    {
        var release = new TaskCompletionSource();
        var h = await new Harness().ReadyAsync();
        h.Azure.Release = release.Task;
        var workflow = new Timetracker.Application.Workflows.DeploymentWorkflow(
            new InMemoryPlanStore(TimeProvider.System), new JobManager(new InMemoryJobStore(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobManager>.Instance),
            new LocalCallerContext(), new DesktopDeploymentLogger(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        var planId = (await workflow.PlanAsync(h.Main.Target.CreateSession(), h.Main.Settings.Build(Timetracker.Domain.Deployment.ValidationTarget.Azure).Options!,
            cancellationToken: TestContext.Current.CancellationToken)).Saved!.PlanId;
        var job = new JobViewModel(await workflow.StartAsync(planId, false, _ => h.Main.Target.CreateSession(), TestContext.Current.CancellationToken));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var act = () => job.WatchAsync(TimeProvider.System, TimeSpan.FromMinutes(1), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
    }
}
