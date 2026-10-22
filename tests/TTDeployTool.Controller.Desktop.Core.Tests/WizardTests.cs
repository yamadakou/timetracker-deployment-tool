using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>デプロイのウィザード（docs/design/desktop-wizard.md の 3）。</summary>
public class DeployWizardViewModelTests
{
    [Theory]
    [InlineData("subscription は必須です。", WizardStep.Target)]
    [InlineData("resource-group は必須です。", WizardStep.Target)]
    [InlineData("アプリ名が空です。", WizardStep.Target)]
    [InlineData("db-type は postgresql または sqlserver を指定してください。", WizardStep.Db)]
    [InlineData("tt-tag（timetracker イメージタグ）が空です。", WizardStep.Db)]
    [InlineData("不適切な組み合わせ: db-type='postgresql'", WizardStep.Db)]
    [InlineData("Azure 上の SQL Server 構成ではサンプル DB を作成できません", WizardStep.Db)]
    [InlineData("persist-db は Azure 向けの設定のため", WizardStep.Db)]
    [InlineData("tracker-password が不足または短すぎます。", WizardStep.Passwords)]
    [InlineData("db-password が SQL Server のパスワード要件を満たしていません。", WizardStep.Passwords)]
    [InlineData("tt-cpu には数値を指定してください（小数点は「.」）: 'x'", WizardStep.Spec)]
    [InlineData("tt-memory が 2Gi です。", WizardStep.Spec)]
    [InlineData("db-cpu には正の数値を指定してください: 0", WizardStep.Spec)]
    [InlineData("db-memory には正の数値を指定してください: 0", WizardStep.Spec)]
    [InlineData("redis-cpu には正の数値を指定してください: 0", WizardStep.Spec)]
    [InlineData("redis-memory には正の数値を指定してください: 0", WizardStep.Spec)]
    [InlineData("expires-on には yyyy-MM-dd の日付か none を指定してください: x", WizardStep.Spec)]
    [InlineData("利用可能期限日（2026-10-21）は、今日以降の日付を指定してください。", WizardStep.Spec)]
    [InlineData("ほかの注意", WizardStep.Confirm)]
    public void Messages_Should_Be_Shown_On_The_Step_Of_The_Item(string message, WizardStep step) =>
        DeployWizardViewModel.StepOf(message).Should().Be(step);

    [Fact]
    public void Before_Signing_In_The_First_Step_Should_Not_Go_Next()
    {
        var vm = new Harness().Main.Wizard;

        (vm.Step, vm.StepText, vm.IsTarget).Should().Be((WizardStep.Target, "① 対象（1 / 5）", true));
        vm.BackCommand.CanExecute(null).Should().BeFalse();
        vm.CanGoNext.Should().BeFalse();
        vm.NextCommand.CanExecute(null).Should().BeFalse();
        vm.StepErrors.Should().Equal("subscription は必須です。", "resource-group は必須です。");
        vm.BlockedText.Should().Be("誤りが 2 件あるため、次へ進めません。");
        DeployWizardViewModel.Titles.Should().HaveCount(5);
    }

    [Fact]
    public async Task Each_Step_Should_Block_Until_Its_Errors_Are_Fixed_And_The_Last_Should_Plan()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        var vm = h.Main.Wizard;
        var settings = h.Main.Settings;
        var changes = new ChangeRecorder(vm);
        vm.CanGoNext.Should().BeTrue();
        vm.BlockedText.Should().BeNull();

        await vm.NextCommand.ExecuteAsync();
        (vm.Step, vm.IsDb).Should().Be((WizardStep.Db, true));
        changes.Names.Should().Contain(nameof(DeployWizardViewModel.IsTarget)).And.Contain(nameof(DeployWizardViewModel.StepText));
        settings.TtTag = "11.0.1-linux-mssql";
        vm.CanGoNext.Should().BeFalse();
        vm.StepErrors.Should().ContainSingle().Which.Should().StartWith("不適切な組み合わせ");
        vm.StepWarnings.Should().ContainSingle().Which.Should().StartWith("tt-tag");
        settings.TtTag = "";
        vm.CanGoNext.Should().BeTrue();

        // 前の画面の誤りがあれば、この画面に誤りが無くても進めない
        h.Main.Target.ResourceGroup = "";
        vm.CanGoNext.Should().BeFalse();
        vm.StepErrors.Should().BeEmpty();
        vm.BlockedText.Should().Be("前の画面に誤りがあるため、次へ進めません。「戻る」で直してください。");
        h.Main.Target.ResourceGroup = "rg";

        await vm.NextCommand.ExecuteAsync();
        vm.IsPasswords.Should().BeTrue();
        settings.DbPassword = "short";
        vm.StepErrors.Should().Equal("db-password が不足または短すぎます。");
        vm.NextCommand.CanExecute(null).Should().BeFalse();
        settings.DbPassword = "DbPassw0rd!";

        await vm.NextCommand.ExecuteAsync();
        vm.IsSpec.Should().BeTrue();
        settings.TtMemoryGi = "2";
        vm.StepErrors.Should().BeEmpty();
        vm.StepWarnings.Should().ContainSingle().Which.Should().StartWith("tt-memory");
        settings.TtCpu = "x";
        vm.CanGoNext.Should().BeFalse();
        settings.TtCpu = "1.5";

        await vm.NextCommand.ExecuteAsync();
        (vm.IsConfirm, vm.CanGoNext, vm.BlockedText).Should().Be((true, false, (string?)null));
        vm.StepWarnings.Should().ContainSingle("確認の画面には、すべての警告を出す");
        h.Main.Deployment.HasPlan.Should().BeTrue("確認の画面を開くと計画を作る");

        await vm.BackCommand.ExecuteAsync();
        vm.IsSpec.Should().BeTrue();
        await h.Main.OpenAsync("wizard");
        vm.IsTarget.Should().BeTrue();
        h.Navigations.Should().Equal("wizard");
    }

    [Fact]
    public async Task Running_Now_Should_Open_The_Execution_Status_And_Read_Azure_After_The_Job()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        h.Main.HasStatusJob.Should().BeFalse();
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        await h.Main.StatusRefresh;

        h.Navigations.Should().Equal("status");
        h.Main.StatusJob.Should().Be(h.Main.CurrentJob);
        h.Main.HasStatusJob.Should().BeTrue();
        h.Main.StatusJob!.StatusText.Should().Be("完了");
        h.Main.Status.Summary.Should().NotBeNull("ジョブが終わった後に Azure の状態を読み直す");
    }

    [Fact]
    public async Task Scheduling_Should_Open_The_Jobs_And_Not_Refresh_The_Status()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        await h.Main.Deployment.ScheduleCommand.ExecuteAsync();

        h.Navigations.Should().Equal("jobs");
        h.Main.StatusJob.Should().BeNull();
        await h.Main.CurrentJob!.CancelCommand.ExecuteAsync();
        h.Main.Status.Summary.Should().BeNull();
    }

    [Fact]
    public async Task Deleting_From_The_List_Should_Plan_And_Then_Show_The_Execution_Status()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();

        await h.Main.OpenAsync("delete");
        h.Main.Deletion.HasPlan.Should().BeTrue("削除の画面を開くと計画を作る");
        h.Main.Deletion.ConfirmAppName = "demo";
        await h.Main.Deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        await h.Main.StatusRefresh;

        h.Navigations.Should().Equal("delete", "status");
        h.Main.StatusJob!.Title.Should().Be("削除（rg / demo）");
    }

    [Fact]
    public async Task Opening_The_Status_Should_Show_The_Latest_Job_Of_The_Target()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        var job = h.Main.CurrentJob;

        h.Main.Target.AppName = "other";
        await h.Main.OpenAsync("status");
        h.Main.StatusJob.Should().BeNull("この対象のジョブは無い");

        h.Main.Target.AppName = "demo";
        await h.Main.OpenAsync("status");
        h.Main.StatusJob.Should().Be(job);

        await h.Main.OpenAsync("jobs");
        h.Navigations.Should().EndWith("jobs");
    }
}
