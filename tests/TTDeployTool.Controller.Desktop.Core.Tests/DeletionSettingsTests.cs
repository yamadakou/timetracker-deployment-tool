using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// まだ始めていない削除のジョブの「リソースグループも削除する」を変える（オーナーの依頼、2026-11-13。案 A）。計画し直し、広げるときは確かめ、
/// ジョブの始め方と保存した予定を入れ替える。
/// </summary>
public sealed class DeletionSettingsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static async Task<(Harness H, FakeTimeProvider Time, JobViewModel Job)> ScheduledAsync(bool deleteResourceGroup = false)
    {
        var time = new FakeTimeProvider(Start);
        var h = await new Harness(time: time).ReadyAsync();
        h.Azure.Deployed();
        var vm = h.Main.Deletion;
        vm.DeleteResourceGroup = deleteResourceGroup;
        await vm.PlanCommand.ExecuteAsync();
        vm.ConfirmAppName = "demo";
        vm.Schedule.Date = Start.AddDays(1);
        await vm.ScheduleCommand.ExecuteAsync();
        return (h, time, h.Main.Jobs.Single(j => !j.IsRecord));
    }

    [Fact]
    public async Task Turning_On_The_Resource_Group_Deletion_Should_Ask_And_Be_Used_When_The_Job_Runs()
    {
        var (h, time, job) = await ScheduledAsync();
        (job.CanChangeDeletion, job.EditDeleteResourceGroup, job.DeletionSettingsText).Should().Be((true, false, "リソースグループは残す"));
        h.Dialogs.Confirm = true;
        time.Advance(TimeSpan.FromMinutes(5));

        job.EditDeleteResourceGroup = true;
        await job.ChangeDeletionCommand.ExecuteAsync();

        h.Dialogs.Confirmations.Should().Equal("リソースグループも削除しますか？");
        (job.DeletionMessage, job.DeletionSettingsText, job.ChangedAt).Should().Be(("削除の設定を変えました（リソースグループも削除する）。", "リソースグループも削除する", Start.AddMinutes(5)));
        h.Preferences.SavedJobs.Should().ContainSingle().Which.Deletion!.DeleteResourceGroup.Should().BeTrue("アプリを終了しても、変えた設定で残す");

        // 予定の時刻になると、変えた設定で削除する
        time.Advance(TimeSpan.FromDays(1));
        await h.Main.Queue.WhenIdleAsync();
        job.State.Should().Be(DesktopJobState.Succeeded);
        h.Azure.GatewayMock.Verify(g => g.DeleteResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Turning_Off_The_Resource_Group_Deletion_Should_Not_Ask()
    {
        var (h, time, job) = await ScheduledAsync(deleteResourceGroup: true);
        job.EditDeleteResourceGroup.Should().BeTrue();

        job.EditDeleteResourceGroup = false;
        await job.ChangeDeletionCommand.ExecuteAsync();

        h.Dialogs.Confirmations.Should().BeEmpty("狭めるときは確かめない");
        job.DeletionSettingsText.Should().Be("リソースグループは残す");
        time.Advance(TimeSpan.FromDays(2));
        await h.Main.Queue.WhenIdleAsync();
        job.State.Should().Be(DesktopJobState.Succeeded);
        h.Azure.GatewayMock.Verify(g => g.DeleteResourceGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_Canceled_Confirmation_Or_The_Same_Value_Should_Not_Change_It()
    {
        var (h, _, job) = await ScheduledAsync();
        await job.ChangeDeletionCommand.ExecuteAsync();
        job.DeletionMessage.Should().Be("削除の設定は変わっていません。");

        h.Dialogs.Confirm = false;
        job.EditDeleteResourceGroup = true;
        await job.ChangeDeletionCommand.ExecuteAsync();
        (job.DeletionMessage, job.EditDeleteResourceGroup, job.Saved!.DeleteResourceGroup).Should().Be(("削除の設定は変えていません。", false, false));
    }

    [Fact]
    public async Task A_Resource_Group_That_Cannot_Be_Deleted_Should_Be_Told()
    {
        var (h, _, job) = await ScheduledAsync();
        h.Azure.Resources.Add(new AzureResourceInfo("vm1", "Microsoft.Compute/virtualMachines"));

        job.EditDeleteResourceGroup = true;
        await job.ChangeDeletionCommand.ExecuteAsync();

        job.DeletionMessage.Should().StartWith("リソースグループ rg は削除できません（ほかのリソースが残ります: vm1").And.Contain("「リソースグループも削除する」のチェックを外して");
        (job.EditDeleteResourceGroup, job.Saved!.DeleteResourceGroup).Should().Be((false, false));
        h.Dialogs.Confirmations.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_To_Delete_Or_An_Azure_Error_Should_Be_Told()
    {
        var (h, _, job) = await ScheduledAsync();
        h.Azure.Apps.Clear();
        h.Azure.Environment = null;
        job.EditDeleteResourceGroup = true;
        h.Azure.ResourceGroup = null;
        await job.ChangeDeletionCommand.ExecuteAsync();
        job.DeletionMessage.Should().Be("削除するものはありません（既に削除されています）。");

        h.Azure.GatewayMock.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("読めない"));
        job.EditDeleteResourceGroup = true;
        await job.ChangeDeletionCommand.ExecuteAsync();
        job.DeletionMessage.Should().NotBeNullOrEmpty().And.NotStartWith("削除の設定を変えました");
    }

    [Fact]
    public async Task Jobs_That_Cannot_Change_The_Deletion_Should_Say_So()
    {
        var (h, _, job) = await ScheduledAsync();
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg2", "app2"), Start.AddDays(1), () => new TaskCompletionSource<Job>().Task)
            .CanChangeDeletion.Should().BeFalse("デプロイ");
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg3", "app3"), null, () => new TaskCompletionSource<Job>().Task, expiryDeletion: true)
            .CanChangeDeletion.Should().BeFalse("期限での削除は、保存した計画が無い");
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg4", ""), Start.AddDays(1), () => new TaskCompletionSource<Job>().Task,
            saved: job.Saved).CanChangeDeletion.Should().BeFalse("リソースグループだけの削除");
        new JobViewModel(JobKind.Delete, new JobTarget("sub", "rg5", "app5"), Start.AddDays(1), null, null) { Saved = job.Saved }.CanChangeDeletion.Should().BeFalse("JobQueue が無い");
        job.DeletionSettingsText.Should().NotBeNull();
        h.Main.Jobs.First(j => j.IsDeployment).DeletionSettingsText.Should().BeNull();

        // 始まったジョブは入れ替えない（計画し直している間に始まった場合）
        await job.CancelCommand.ExecuteAsync();
        job.CanChangeDeletion.Should().BeFalse();
        h.Main.Queue.ReplaceDeletion(job, () => new TaskCompletionSource<Job>().Task, job.Saved!).Should().Be("ジョブが始まったため、削除の設定は変えていません。");
    }

    [Fact]
    public async Task A_Saved_Deletion_Restored_After_A_Restart_Should_Be_Changeable()
    {
        var (h, time, job) = await ScheduledAsync();
        h.Main.Dispose();
        var restarted = await new Harness(time: time, preferences: h.Preferences).ReadyAsync();
        restarted.Azure.Deployed();
        var restored = restarted.Main.Jobs.Single(j => !j.IsRecord);
        restored.CanChangeDeletion.Should().BeTrue();
        restarted.Dialogs.Confirm = true;
        restored.EditDeleteResourceGroup = true;
        await restored.ChangeDeletionCommand.ExecuteAsync();
        restored.DeletionSettingsText.Should().Be("リソースグループも削除する");
        job.Saved!.DeleteResourceGroup.Should().BeFalse("前のアプリのジョブは変えない");
    }
}
