using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>デプロイのウィザード（docs/design/desktop/desktop-wizard.md の 3）。</summary>
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

    // 「予定に追加」の後に「新しくデプロイ」を開き直すと、⑤ のまま計画も無く、新しく始められなかった（オーナーの実機、2026-10-27。No.70）。
    // 登録しても画面を移らず、完了の表示にする（Q4）。開き直しても、続けて・新規を押すまでそのまま（Q13）
    private static async Task<Harness> RegisteredAsync(bool scheduled, Action<Harness>? arrange = null)
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        arrange?.Invoke(h);
        var vm = h.Main.Wizard;
        for (var i = 0; i < 4; i++)
            await vm.NextCommand.ExecuteAsync();
        vm.IsConfirm.Should().BeTrue();
        (vm.IsCompleted, vm.IsEditing, vm.CompletedText, vm.ShowJobCommand.CanExecute(null)).Should().Be((false, true, (string?)null, false));
        vm.ShowJobText.Should().Be("実行状況（ログ）を見る");
        h.Navigations.Clear();
        if (scheduled)
        {
            h.Main.Deployment.Schedule.Date = DateTimeOffset.Now.AddDays(1);
            await h.Main.Deployment.ScheduleCommand.ExecuteAsync();
        }
        else
        {
            await h.Main.Deployment.StartCommand.ExecuteAsync();
        }
        return h;
    }

    [Fact]
    public async Task A_Scheduled_Deployment_Should_Stay_On_The_Completed_View_Until_Continued()
    {
        var h = await RegisteredAsync(scheduled: true);
        var vm = h.Main.Wizard;
        var job = h.Main.CurrentDeployment!;

        h.Navigations.Should().BeEmpty("画面を移らない");
        (vm.IsCompleted, vm.IsEditing, vm.StepText, vm.RegisteredJob).Should().Be((true, false, "完了", job));
        new[] { vm.IsTarget, vm.IsDb, vm.IsPasswords, vm.IsSpec, vm.IsConfirm }.Should().AllBeEquivalentTo(false);
        vm.CompletedText.Should().Be($"デプロイ（rg / demo）を予定に追加しました（{job.ScheduledAt!.Value.ToLocalTime():MM/dd HH:mm} に実行します）。");
        vm.ShowJobText.Should().Be("ジョブの一覧を見る");
        await vm.ShowJobCommand.ExecuteAsync();
        h.Navigations.Should().Equal(Pages.Jobs);

        // メニューから開き直しても、完了の表示のまま
        await h.Main.PageShownAsync(Pages.Wizard);
        vm.IsCompleted.Should().BeTrue();

        // 続けてデプロイ: 入力をすべて残して ① から。同じ対象のジョブがあることを ① で知らせる
        await vm.ContinueCommand.ExecuteAsync();
        (vm.IsTarget, vm.IsCompleted, h.Main.Target.AppName, h.Main.Settings.DbPassword).Should().Be((true, false, "demo", "DbPassw0rd!"));
        vm.ActiveJobNotice.Should().Be($"この対象（rg / demo）には「デプロイ（rg / demo）: {job.StatusText}」があります。終わるまでは計画できないため、アプリ名かリソースグループを変えてください。");
        vm.HasActiveJobNotice.Should().BeTrue();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        h.Main.Target.AppName = "demo2";
        (vm.ActiveJobNotice, vm.HasActiveJobNotice).Should().Be(((string?)null, false));
        raised.Should().Contain(nameof(DeployWizardViewModel.HasActiveJobNotice));
        h.Main.Target.AppName = "demo";
        await job.CancelCommand.ExecuteAsync();
        vm.HasActiveJobNotice.Should().BeFalse("ジョブが終わったら消す");
    }

    [Fact]
    public async Task A_Deployment_Run_Now_Should_Show_Its_Progress_And_Open_Its_Status()
    {
        var h = await RegisteredAsync(scheduled: false);
        var vm = h.Main.Wizard;
        var job = h.Main.CurrentDeployment!;

        h.Navigations.Should().BeEmpty();
        vm.CompletedText.Should().Be("デプロイ（rg / demo）を実行しました。");
        vm.ShowJobText.Should().Be("実行状況（ログ）を見る");
        await vm.ShowJobCommand.ExecuteAsync();
        (h.Navigations.Last(), h.Main.StatusJob, h.Main.ReturnText).Should().Be((Pages.Status, job, "新しくデプロイに戻る"));

        // 新規デプロイ: 確かめずに初期値に戻して ① から
        h.Dialogs.Confirm = false;
        await vm.NewDeploymentCommand.ExecuteAsync();
        h.Dialogs.Confirmations.Should().NotContain("初期値に戻しますか？");
        (vm.IsTarget, vm.IsCompleted, h.Main.Target.ResourceGroup, h.Main.Settings.DbPassword).Should().Be((true, false, "", ""));

        // 一覧の「対象に入れる」でも完了の表示をやめて ① から
        await h.Main.Queue.WhenIdleAsync();
        h.Main.Target.ResourceGroup = "rg";
        h.Main.Settings.DbPassword = "DbPassw0rd!";
        h.Main.Settings.TrackerPassword = "TrackerPass!";
        for (var i = 0; i < 4; i++)
            await vm.NextCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        vm.IsCompleted.Should().BeTrue();
        await h.Main.OpenAsync(Pages.Wizard);
        (vm.IsTarget, vm.IsCompleted).Should().Be((true, false));
    }

    [Fact]
    public async Task Deletions_Should_Be_Registered_Without_A_Window_Listening()
    {
        var h = await new Harness().ReadyAsync();
        h.Main.NavigationRequested -= h.Navigations.Add;
        h.Azure.Deployed();
        var vm = h.Main.Deletion;
        await vm.PlanCommand.ExecuteAsync();
        vm.ConfirmAppName = "demo";
        await vm.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        h.Azure.Deployed();
        await vm.PlanCommand.ExecuteAsync();
        vm.ConfirmAppName = "demo";
        vm.Schedule.Date = DateTimeOffset.Now.AddDays(1);
        await vm.ScheduleCommand.ExecuteAsync();

        h.Main.Jobs.Select(j => j.State).Should().BeEquivalentTo([DesktopJobState.Succeeded, DesktopJobState.Scheduled]);
        h.Navigations.Should().BeEmpty();
    }

    [Fact]
    public async Task Deletions_Should_Still_Open_The_Status_Or_The_Job_List()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        await h.Main.Deletion.PlanCommand.ExecuteAsync();
        h.Main.Deletion.ConfirmAppName = "demo";
        await h.Main.Deletion.StartCommand.ExecuteAsync();

        h.Navigations.Should().EndWith(Pages.Status);
        h.Main.Wizard.IsCompleted.Should().BeFalse("削除はウィザードと関係ない");
    }

    [Fact]
    public async Task Saving_A_Template_On_The_Last_Step_Should_Keep_The_Plan()
    {
        // 確認の画面でテンプレートを保存すると計画が消え、「今すぐ実行」が無くなっていた（Desktop System Test、2026-10-22）
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        var settings = h.Main.Settings;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        settings.TemplateName = "保存";
        await settings.SaveTemplateCommand.ExecuteAsync();
        settings.IncludePasswordsInCopy = true;
        await settings.CopyMarkdownCommand.ExecuteAsync();
        settings.SelectedTemplate = null;
        h.Main.Deployment.HasPlan.Should().BeTrue("入力でないものの変更では計画を消さない");

        settings.DbName = "changed";
        h.Main.Deployment.HasPlan.Should().BeFalse("入力を変えたら計画を消す");
    }

    [Fact]
    public async Task Running_Now_Should_Stay_On_The_Wizard_And_The_Status_Should_Read_Azure_After_The_Job()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        h.Main.HasStatusJob.Should().BeFalse();
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        // 今すぐ実行しても、ウィザードの完了の表示に留まる（Q4・No.70）。「実行状況（ログ）を見る」で開く
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        h.Navigations.Should().BeEmpty();
        await h.Main.Wizard.ShowJobCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        await h.Main.StatusRefresh;

        h.Navigations.Should().Equal("status");
        h.Main.StatusJob.Should().Be(h.Main.CurrentJob);
        h.Main.HasStatusJob.Should().BeTrue();
        h.Main.StatusJob!.StatusText.Should().Be("完了");
        h.Main.Status.Summary.Should().NotBeNull("ジョブが終わった後に Azure の状態を読み直す");
    }

    [Fact]
    public async Task Scheduling_Should_Stay_On_The_Wizard_And_Not_Refresh_The_Status()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        await h.Main.Deployment.ScheduleCommand.ExecuteAsync();

        h.Navigations.Should().BeEmpty("ウィザードの完了の表示に留まる（Q4・No.70）");
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
