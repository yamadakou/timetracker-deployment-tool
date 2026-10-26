using Azure;
using FluentAssertions;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Infrastructure.Azure;
using Xunit;
using static TTDeployTool.Controller.Desktop.Core.Tests.DeploymentListViewModelTests;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>デプロイの一覧の表（見出しでの並べ替え・絞り込み・複数の選択とまとめての操作。docs/design/desktop-wizard.md の 7、No.63）。</summary>
public class DeploymentTableTests
{
    private readonly MovableTime _time = new();

    private async Task<Harness> LoadedAsync(params IEnumerable<ManagedResourceRecord>[] deployments)
    {
        var h = new Harness(time: _time);
        h.Azure.RecordsByTenant["t1"] = deployments.SelectMany(d => d).ToList();
        await h.ReadyAsync();
        return h;
    }

    private static void Never(Harness h) =>
        h.Azure.GatewayMock.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

    [Fact]
    public async Task Headers_Should_Sort_And_Show_The_Direction()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "b"), Deployment("sub", "rg", "a"), Deployment("sub", "rg", "c"));
        var vm = h.Main.Deployments;
        vm.SubscriptionHeader.Should().Be("サブスクリプション ▲");

        vm.SortBy(DeploymentSortKey.AppName);
        vm.Rows.Select(r => r.AppName).Should().Equal("a", "b", "c");
        (vm.AppNameHeader, vm.SubscriptionHeader).Should().Be(("アプリ名 ▲", "サブスクリプション"));
        vm.SortBy(DeploymentSortKey.AppName);
        vm.Rows.Select(r => r.AppName).Should().Equal("c", "b", "a");
        vm.AppNameHeader.Should().Be("アプリ名 ▼");

        vm.SortBy(DeploymentSortKey.ExpiresAt);
        (vm.ExpiryHeader, vm.ResourceGroupHeader, vm.DeployedHeader).Should().Be(("利用可能期限日 ▲", "リソースグループ", "デプロイ日"));
        vm.SortBy(DeploymentSortKey.ResourceGroup);
        vm.ResourceGroupHeader.Should().Be("リソースグループ ▲");
        vm.SortBy(DeploymentSortKey.DeployedAt);
        vm.DeployedHeader.Should().Be("デプロイ日 ▲");

        // アプリ名の絞り込み（中間一致）
        vm.AppNameFilter = " B ";
        vm.Rows.Select(r => r.AppName).Should().Equal("b");
    }

    [Fact]
    public async Task Selecting_Rows_Should_Enable_The_Batch_Operations_And_Show_The_Current_Row()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "a"), Deployment("sub", "rg", "b"));
        var vm = h.Main.Deployments;
        (vm.HasCurrent, vm.SelectionText).Should().Be((false, "行を選ぶと、まとめて操作できます。"));
        vm.ChangeSelectedExpiryCommand.CanExecute(null).Should().BeFalse();
        vm.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();

        vm.SetSelection([vm.Rows[0]]);
        vm.Current.Should().BeSameAs(vm.Rows[0]);
        vm.SetSelection([vm.Rows[0]]);
        vm.SetSelection(vm.Rows);
        (vm.Current, vm.SelectionText).Should().Be((null, "2 件を選んでいます。"));
        vm.ChangeSelectedExpiryCommand.CanExecute(null).Should().BeTrue();

        // 読み直すと行を作り直すため、選択も消える
        await vm.LoadCommand.ExecuteAsync();
        vm.Selected.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Selected_Expiries_Should_Change_Together_Skipping_Rows_With_Jobs()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo", "2026-11-23T14:59:59Z"), Deployment("sub", "rg", "busy"), Deployment("sub", "rg", "gone"));
        Never(h);
        h.Azure.Apps.Add(new ContainerAppInfo("demo-tt", FakeAzure.EnvId, FakeAzure.Managed));
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "busy"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);
        var vm = h.Main.Deployments;
        vm.BatchExpiryDate.Should().Be(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.FromHours(9)), "既定は今日から 32 日後");
        vm.SetSelection(vm.Rows);
        vm.BatchNoExpiry = true;

        await vm.ChangeSelectedExpiryCommand.ExecuteAsync();

        vm.BatchMessage.Should().Be(
            "1 件の利用可能期限を変えました（demo）。 ジョブがあるため変えなかったもの: busy。 変えられなかったもの: gone（リソースグループ rg に、アプリ名 gone の、本ツールで作った Container App とストレージアカウントがありません（削除された可能性があります）。一覧を読み直してください。）");
        h.Main.Deployments.Rows.Single(r => r.AppName == "demo").ExpiryText.Should().Be("期限なし");

        // 日付で変える
        vm.SetSelection(vm.Rows.Where(r => r.AppName == "demo"));
        vm.BatchNoExpiry = false;
        vm.BatchExpiryDate = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(9));
        await vm.ChangeSelectedExpiryCommand.ExecuteAsync();
        vm.BatchMessage.Should().Be("1 件の利用可能期限を変えました（demo）。");
        h.Main.Deployments.Rows.Single(r => r.AppName == "demo").ExpiryText.Should().Be("2026-12-01");
        // 期限を変えて表示し直しても、残っている行の選択は保つ
        vm.Selected.Select(r => r.AppName).Should().Equal("demo");

        // ジョブのある行だけなら、何も変えない
        vm.SetSelection(vm.Rows.Where(r => r.AppName == "busy"));
        await vm.ChangeSelectedExpiryCommand.ExecuteAsync();
        vm.BatchMessage.Should().Be("ジョブがあるため変えなかったもの: busy。");
    }

    [Fact]
    public async Task Deleting_One_Row_Should_Open_The_Delete_Page_Unless_It_Has_A_Job()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo"), Deployment("sub", "rg", "busy"));
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "busy"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);
        var vm = h.Main.Deployments;

        vm.SetSelection(vm.Rows.Where(r => r.AppName == "busy"));
        await vm.DeleteSelectedCommand.ExecuteAsync();
        vm.BatchMessage.Should().Be("busy にはジョブがあるため、削除できません。ジョブが終わるか、取り消してから削除してください。");
        h.Navigations.Should().NotContain("delete");

        vm.SetSelection(vm.Rows.Where(r => r.AppName == "demo"));
        await vm.DeleteSelectedCommand.ExecuteAsync();
        vm.BatchMessage.Should().BeNull();
        h.Navigations.Should().EndWith("delete");
        h.Main.Target.AppName.Should().Be("demo");
    }
}

/// <summary>まとめての削除（Q10: 計画を並べ、件数を入力して確認）。</summary>
public class BatchDeletionTests
{
    private readonly MovableTime _time = new();

    private async Task<Harness> SelectedAsync(params string[] apps)
    {
        var h = new Harness(time: _time);
        h.Azure.RecordsByTenant["t1"] = apps.SelectMany(a => Deployment("sub", a.StartsWith("x", StringComparison.Ordinal) ? "rg-" + a : "rg", a)).ToList();
        h.Azure.Deployed();
        foreach (var app in apps.Where(a => a != "demo" && !a.StartsWith('x')))
            foreach (var suffix in new[] { "-tt", "-db", "-redis" })
                h.Azure.Apps.Add(new ContainerAppInfo(app + suffix, FakeAzure.EnvId, TTDeployTool.Domain.Deployment.ManagedTags.For(app)));
        await h.ReadyAsync();
        var list = h.Main.Deployments;
        list.SetSelection(list.Rows);
        await list.DeleteSelectedCommand.ExecuteAsync();
        return h;
    }

    [Fact]
    public async Task Selected_Deployments_Should_Be_Planned_And_Deleted_After_Typing_The_Count()
    {
        var h = await SelectedAsync("demo", "other");
        var vm = h.Main.BatchDeletion;

        h.Navigations.Should().EndWith("batchdelete");
        vm.Items.Select(i => (i.Title, i.WillRun)).Should().Equal(("demo（rg）", true), ("other（rg）", true));
        // Environment には other のアプリも入っているため残す
        vm.Items[0].Summary.Should().Be("削除する: demo-tt、demo-redis、demo-db。 残す: demo-env。 DB アプリを削除すると DB のデータはすべて失われます。 リソースグループ rg は残します。");
        vm.Items[1].Summary.Should().StartWith("削除する: other-tt、other-redis、other-db。 DB アプリ");
        vm.RunnableCount.Should().Be(2);
        vm.ConfirmText.Should().Be("2 件を削除します（元に戻せません）。確かめたら、削除する件数（2）を入力してください。");
        vm.CanStart.Should().BeFalse();

        vm.ConfirmCount = "1";
        vm.StartCommand.CanExecute(null).Should().BeFalse();
        vm.ConfirmCount = " 2 ";
        vm.StartCommand.CanExecute(null).Should().BeTrue();
        await vm.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        h.Navigations.Should().EndWith("jobs");
        h.Main.Jobs.Select(j => (j.Title, j.State)).Should().BeEquivalentTo(new[]
        {
            ("削除（rg / other）", DesktopJobState.Succeeded), ("削除（rg / demo）", DesktopJobState.Succeeded),
        });
        vm.Items.Should().BeEmpty("登録した計画は使ったものとして外す");
        vm.PlanCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Deployments_That_Cannot_Be_Deleted_Should_Be_Excluded()
    {
        var h = new Harness(time: _time);
        h.Azure.RecordsByTenant["t1"] = [.. Deployment("sub", "rg", "demo"), .. Deployment("sub", "rg", "busy"), .. Deployment("sub", "rg-gone", "gone"), .. Deployment("sub", "rg-err", "err")];
        h.Azure.Deployed();
        h.Azure.GatewayMock.Setup(g => g.GetResourceGroupAsync("sub", "rg-err", It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(500, "失敗"));
        await h.ReadyAsync();
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "busy"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);
        h.Main.Deployments.SetSelection(h.Main.Deployments.Rows);
        await h.Main.Deployments.DeleteSelectedCommand.ExecuteAsync();
        var vm = h.Main.BatchDeletion;

        vm.Items.Select(i => (i.Entry.AppName, i.IsExcluded)).Should().BeEquivalentTo(new[] { ("busy", true), ("demo", false), ("err", true), ("gone", true) });
        vm.Items.Single(i => i.Entry.AppName == "busy").Excluded.Should().StartWith("ジョブ（デプロイ（rg / busy）: 予定（");
        vm.Items.Single(i => i.Entry.AppName == "gone").Excluded.Should().Be("削除するものが無いため外します（既に削除されています）。");
        vm.Items.Single(i => i.Entry.AppName == "err").Excluded.Should().StartWith("計画を作れなかったため外します: Azure がエラーを返しました（500");
        vm.RunnableCount.Should().Be(1);

        // 権限が無ければ、計画に誤りがあるとして外す
        h.Azure.Permissions.Clear();
        await vm.PlanCommand.ExecuteAsync();
        vm.Items.Single(i => i.Entry.AppName == "demo").Excluded.Should().StartWith("実行できないため外します: ");
        (vm.RunnableCount, vm.ConfirmText, vm.CanStart).Should().Be((0, null, false));
    }

    [Fact]
    public async Task Options_Should_Clear_The_Plans_And_Apply_To_Every_Deployment()
    {
        var h = await SelectedAsync("demo", "other");
        var vm = h.Main.BatchDeletion;
        h.Azure.HasStorageAccount = true;
        vm.ConfirmCount = "2";

        vm.KeepDbData = true;
        vm.Items.Should().BeEmpty();
        vm.ConfirmCount.Should().BeEmpty();
        vm.DeleteResourceGroup = true;
        vm.KeepDbData = true;
        await vm.PlanCommand.ExecuteAsync();

        vm.Items[0].Summary.Should().Contain($"残す: demo-env、{FakeAzure.StorageAccountName}。").And.Contain("リソースグループ rg も削除します（元に戻せません）。");
    }

    [Fact]
    public async Task Scheduling_Should_Register_Each_And_Report_Those_That_Could_Not_Be()
    {
        var h = await SelectedAsync("demo", "other");
        var vm = h.Main.BatchDeletion;
        vm.ConfirmCount = "2";
        vm.Schedule.TimeOfDay = TimeSpan.Zero;
        vm.Schedule.Date = _time.GetUtcNow().AddDays(-1);

        await vm.ScheduleCommand.ExecuteAsync();
        vm.Error.Should().StartWith("予定の時刻（");
        vm.HasError.Should().BeTrue();

        // 計画の後に、同じ対象のジョブが登録された
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "other"), _time.GetUtcNow().AddHours(5), () => new TaskCompletionSource<Job>().Task);
        vm.Schedule.Date = _time.GetUtcNow().AddDays(1);
        await vm.ScheduleCommand.ExecuteAsync();

        vm.Error.Should().StartWith("登録できなかったもの:\n- other（rg）: この対象のジョブ");
        h.Main.Jobs.Count(j => !j.IsDeployment && j.State == DesktopJobState.Scheduled).Should().Be(1);
        h.Navigations.Should().EndWith("jobs");
    }
}

/// <summary>ジョブの一覧の表（並べ替え・絞り込み・まとめての操作）と、詳細・戻る（No.63）。</summary>
public class JobTableTests
{
    private readonly MovableTime _time = new();

    private static JobViewModel Pending(Harness h, string app, DateTimeOffset? at, JobKind kind = JobKind.Deploy) =>
        h.Main.Queue.Register(kind, new JobTarget("sub", "rg", app), at, () => new TaskCompletionSource<Job>().Task);

    [Fact]
    public async Task Rows_Should_Show_The_Kind_Target_Time_And_Result()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var scheduled = Pending(h, "a", _time.GetUtcNow().AddHours(1), JobKind.Delete);
        var failing = new TaskCompletionSource<Job>();
        var now = h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "b"), null, () => failing.Task);

        (scheduled.KindText, scheduled.TargetText).Should().Be(("削除", "rg / a"));
        scheduled.TimeText.Should().MatchRegex(@"^\d\d/\d\d \d\d:\d\d$");
        (now.KindText, now.TimeText, now.ResultText).Should().Be(("デプロイ", "すぐ実行", null));
        failing.SetException(new InvalidOperationException("失敗しました"));
        await now.Finished;
        now.ResultText.Should().Be("実行しませんでした: エラーが発生しました: 失敗しました");
        var list = h.Main.JobList;
        list.Items.Should().Equal(now, scheduled);
        list.CountText.Should().Be("2 件");
    }

    [Fact]
    public async Task The_List_Should_Filter_By_State_And_Target_And_Sort_By_Headers()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var list = h.Main.JobList;
        list.CountText.Should().Be("ジョブはありません。");
        var late = Pending(h, "beta", _time.GetUtcNow().AddHours(3));
        var early = Pending(h, "alpha", _time.GetUtcNow().AddHours(1), JobKind.Delete);
        var now = Pending(h, "gamma", null);
        await early.PauseCommand.ExecuteAsync();

        list.Filter = JobListViewModel.Filters.Single(f => f.Mode == JobFilterMode.Scheduled);
        list.Items.Should().Equal(late);
        list.CountText.Should().Be("1 件（全 3 件）");
        list.Filter = JobListViewModel.Filters.Single(f => f.Mode == JobFilterMode.Active);
        list.Items.Should().HaveCount(3);
        list.Filter = JobListViewModel.Filters.Single(f => f.Mode == JobFilterMode.Finished);
        list.Items.Should().BeEmpty();
        list.Filter = JobListViewModel.Filters[0];
        list.Filter.ToString().Should().Be("すべて");
        list.TargetFilter = " ALPHA ";
        list.Items.Should().Equal(early);
        list.TargetFilter = "";

        list.SortBy(JobSortKey.Time);
        list.Items.Should().Equal(early, late, now);
        (list.TimeHeader, list.KindHeader).Should().Be(("時刻 ▲", "種類"));
        list.SortBy(JobSortKey.Time);
        list.TimeHeader.Should().Be("時刻 ▼");
        // 予定の無いものは最後
        list.Items.Should().Equal(late, early, now);
        list.SortBy(JobSortKey.Target);
        list.Items.Should().Equal(early, late, now);
        list.TargetHeader.Should().Be("リソースグループ / アプリ名 ▲");
        list.SortBy(JobSortKey.Kind);
        list.Items.Select(j => j.KindText).Should().BeInAscendingOrder();
        list.SetSelection([early]);
        list.SortBy(JobSortKey.State);
        list.StateHeader.Should().Be("状態 ▲");
        // 並べ直しても選択は保つ
        list.Selected.Should().Equal(early);
        list.Filter = JobListViewModel.Filters.Single(f => f.Mode == JobFilterMode.Scheduled);
        list.Selected.Should().BeEmpty("絞り込みで消えた行は選択から外す");
        list.Filter = JobListViewModel.Filters[0];
        list.SortBy(JobSortKey.Registered);
        // 登録した順（昇順）
        list.Items.Should().Equal(late, early, now);
        list.SortBy(JobSortKey.Registered);
        list.Items.Should().Equal(now, early, late);
    }

    [Fact]
    public async Task Selected_Jobs_Should_Be_Changed_Together_Where_Possible()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var list = h.Main.JobList;
        var a = Pending(h, "a", _time.GetUtcNow().AddHours(1));
        var b = Pending(h, "b", _time.GetUtcNow().AddHours(2));
        list.CancelSelectedCommand.CanExecute(null).Should().BeFalse();
        list.SetSelection([a, b]);
        list.SetSelection([a, b]);
        (list.SelectionText, list.HasCurrent).Should().Be(("2 件を選んでいます。", false));

        await list.PauseSelectedCommand.ExecuteAsync();
        list.Message.Should().Be("2 件の一時停止をしました。");
        await list.PauseSelectedCommand.ExecuteAsync();
        list.Message.Should().Be("0 件の一時停止をしました（2 件は、今の状態ではできないため、そのままです）。");
        await list.ResumeSelectedCommand.ExecuteAsync();
        list.Message.Should().Be("2 件の再開をしました。");

        list.Schedule.Date = _time.GetUtcNow().AddDays(1);
        list.Schedule.TimeOfDay = new TimeSpan(9, 30, 0);
        await list.RescheduleSelectedCommand.ExecuteAsync();
        list.Message.Should().StartWith("2 件の予定の変更（");
        a.ScheduledAt.Should().Be(b.ScheduledAt);
        list.Schedule.Date = _time.GetUtcNow().AddDays(-1);
        await list.RescheduleSelectedCommand.ExecuteAsync();
        list.Message.Should().StartWith("予定の時刻（");

        list.SetSelection([a]);
        list.Current.Should().Be(a);
        await list.CancelSelectedCommand.ExecuteAsync();
        a.State.Should().Be(DesktopJobState.Canceled);
    }

    [Fact]
    public async Task The_Detail_Should_Open_The_Status_And_Return_To_The_Job_List()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var job = Pending(h, "demo", _time.GetUtcNow().AddHours(1));
        var other = h.Main.Queue.Register(JobKind.Deploy, new JobTarget("unknown", "rg", "x"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);

        await h.Main.OpenJobAsync(job);

        h.Navigations.Should().EndWith("status");
        (h.Main.StatusJob, h.Main.ReturnPage, h.Main.ReturnText).Should().Be((job, "jobs", "ジョブの一覧に戻る"));
        h.Main.Status.Summary.Should().NotBeNull("その対象の Azure の状態を読む");

        // サブスクリプションが一覧に無ければ、ジョブだけを出す
        await h.Main.OpenJobAsync(other);
        h.Main.StatusJob.Should().Be(other);

        // 一覧の行の「状態を確認」から開いたら、デプロイの一覧に戻る
        await h.Main.OpenAsync("status");
        h.Main.ReturnText.Should().Be("デプロイの一覧に戻る");
    }

    [Fact]
    public async Task The_Pages_Should_Work_Without_A_Window_Listening()
    {
        // 画面の切り替えを受け取る側がいなくても、処理は続ける
        var h = new Harness(time: _time);
        h.Azure.RecordsByTenant["t1"] = [.. Deployment("sub", "rg", "demo"), .. Deployment("sub", "rg", "other")];
        h.Main.NavigationRequested -= h.Navigations.Add;
        h.Azure.Deployed();
        await h.ReadyAsync();
        var job = Pending(h, "x", _time.GetUtcNow().AddHours(1));
        await h.Main.OpenJobAsync(job);
        h.Main.Deployments.SetSelection(h.Main.Deployments.Rows);
        await h.Main.Deployments.DeleteSelectedCommand.ExecuteAsync();
        h.Main.BatchDeletion.Items.Should().HaveCount(2);
        // other は Azure に無いため外れ、demo だけを削除する
        h.Main.BatchDeletion.ConfirmCount = "1";
        await h.Main.BatchDeletion.ScheduleCommand.ExecuteAsync();
        h.Azure.ResourceGroup = null;
        h.Main.Target.AppName = "fresh";
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.ScheduleCommand.ExecuteAsync();
        h.Main.Target.AppName = "fresh2";
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        h.Main.Jobs.Should().HaveCount(4);
        foreach (var page in new[] { "wizard", "status", "delete", "settings" })
            await h.Main.OpenAsync(page);
        await h.Main.SignIn.SignOutCommand.ExecuteAsync();
        h.Navigations.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Selection_And_Target_Filter_Should_Be_Readable()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var list = h.Main.JobList;
        var job = Pending(h, "demo", _time.GetUtcNow().AddHours(1));
        list.TargetFilter = "dem";
        list.TargetFilter.Should().Be("dem");
        list.Items.Should().Equal(job);
        list.SetSelection([job]);
        list.Selected.Should().Equal(job);
        list.SetSelection([]);
        list.SelectionText.Should().Be("行を選ぶと、まとめて操作できます。");
        list.TargetFilter = "none";
        list.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Signing_Out_Should_Return_To_The_Sign_In_Page()
    {
        var h = await new Harness(time: _time).ReadyAsync();

        await h.Main.SignIn.SignOutCommand.ExecuteAsync();

        h.Navigations.Should().EndWith("target");
    }
}
