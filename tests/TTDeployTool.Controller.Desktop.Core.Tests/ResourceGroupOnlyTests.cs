using FluentAssertions;
using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// 管理タグの残ったリソースグループ（本ツールのアプリのリソースが無いもの）を一覧に出し、リソースグループだけを削除する（オーナーの依頼、2026-11-13。案 A）。
/// </summary>
public sealed class ResourceGroupOnlyTests
{
    private static ManagedResourceRecord Group(string rg) =>
        new($"/subscriptions/sub/resourceGroups/{rg}", rg, ManagedResourceRecord.ResourceGroupType, "sub", rg, "japaneast", ManagedTags.ForResourceGroup());

    private static async Task<(Harness H, DeploymentRowViewModel Row)> ListedAsync(bool createdByTool = true)
    {
        var h = new Harness();
        h.Azure.RecordsByTenant["t1"] = [Group("rg")];
        h.Azure.ResourceGroup = new ResourceGroupInfo("rg", "japaneast", createdByTool ? ManagedTags.ForResourceGroup() : null);
        await h.ReadyAsync();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        return (h, h.Main.Deployments.Rows.Single());
    }

    [Fact]
    public async Task A_Resource_Group_Only_Row_Should_Offer_Only_The_Deletion()
    {
        var (h, row) = await ListedAsync();

        (row.AppName, row.State, row.Level).Should().Be(("", DeploymentState.ResourceGroupOnly, ExpiryLevel.None));
        row.StateText.Should().StartWith("リソースグループだけ（本ツールのリソースはありません");
        (row.CheckStatusCommand.CanExecute(null), row.ShowPasswordsCommand.CanExecute(null), row.ChangeExpiryCommand.CanExecute(null), row.RedeployCommand.CanExecute(null))
            .Should().Be((false, false, false, false));
        row.DeleteCommand.CanExecute(null).Should().BeTrue();
        h.Main.Deployments.HasExpiringDeployments.Should().BeFalse("期限での自動の削除の対象にしない");
    }

    [Fact]
    public async Task The_Resource_Group_Should_Be_Deleted_After_Confirming_Its_Name()
    {
        var (h, row) = await ListedAsync();
        await row.DeleteCommand.ExecuteAsync();
        var vm = h.Main.Deletion;

        (vm.IsResourceGroupOnly, vm.ConfirmLabel, vm.Error).Should().Be((true, "リソースグループ名", (string?)null));
        vm.Targets.Should().ContainSingle().Which.Should().Be(new DeletionRow("rg", "リソースグループ", "削除する", null, true));
        (vm.AppNameToConfirm, vm.KeptText).Should().Be(("rg", (string?)null));
        vm.BlockedText.Should().Be("リソースグループ名「rg」を入力すると、「今すぐ削除」と「予定に追加」が押せます。");
        vm.Warnings.Should().Contain("リソースグループ rg も削除します。元に戻せません。");

        vm.ConfirmAppName = "rg";
        await vm.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        var job = h.Main.Jobs.Single();
        (job.Title, job.TargetText, job.State).Should().Be(("削除（rg（リソースグループだけ））", "rg（リソースグループだけ）", DesktopJobState.Succeeded));
        h.Azure.GatewayMock.Verify(g => g.DeleteResourceGroupAsync("sub", "rg", Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
        h.Main.Deployments.Rows.Should().BeEmpty("削除したリソースグループは一覧から消す");
    }

    [Fact]
    public async Task A_Resource_Group_Not_Made_By_The_Tool_Should_Not_Be_Deletable()
    {
        var (h, row) = await ListedAsync(createdByTool: false);
        await row.DeleteCommand.ExecuteAsync();
        var vm = h.Main.Deletion;

        (vm.NeedsAppNameConfirmation, vm.KeptText).Should().Be((false, (string?)null));
        vm.Blockers.Should().Equal($"リソースグループ rg は削除できません（{DeletionPlanner.NotCreatedByToolReason}）。");
    }

    [Fact]
    public async Task The_Resource_Group_Only_Deletion_Should_Be_Scheduled_And_Survive_A_Restart()
    {
        var (h, row) = await ListedAsync();
        await row.DeleteCommand.ExecuteAsync();
        var vm = h.Main.Deletion;
        vm.ConfirmAppName = "rg";
        vm.Schedule.Date = DateTimeOffset.Now.AddDays(1);
        await vm.ScheduleCommand.ExecuteAsync();

        var saved = h.Preferences.SavedJobs.Should().ContainSingle().Subject;
        (saved.AppName, saved.Deletion!.ConfirmedAppName, saved.Deletion.DeletesResourceGroup).Should().Be(("", "", true));
    }

    [Fact]
    public async Task An_Empty_Resource_Group_Name_Should_Be_Told_Without_Checking_The_App_Name()
    {
        var (h, row) = await ListedAsync();
        await row.DeleteCommand.ExecuteAsync();
        h.Main.Target.ResourceGroup = "";
        await h.Main.Deletion.PlanCommand.ExecuteAsync();
        h.Main.Deletion.Error.Should().Be("リソースグループ名を入力してください。");
    }
}
