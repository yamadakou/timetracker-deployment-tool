using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>アプリを終了しても残す予定（docs/design/desktop/desktop-jobs.md の 6.6、D23・D24）。</summary>
public class ScheduledJobTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));

    private DateTimeOffset InAnHour => _time.GetUtcNow().AddHours(1);

    // サインインしたときに前回の予定を戻すため、Azure の状態を先に決める
    private async Task<Harness> ReadyAsync(InMemoryAppPreferences? preferences = null)
    {
        var h = new Harness(time: _time, preferences: preferences);
        h.Azure.Deployed();
        return await h.ReadyAsync();
    }

    private void SetSchedule(ScheduleInput schedule, DateTimeOffset at)
    {
        schedule.Date = at;
        schedule.TimeOfDay = at.TimeOfDay;
    }

    private async Task<JobViewModel> ScheduleDeletionAsync(Harness h)
    {
        var vm = h.Main.Deletion;
        await vm.PlanCommand.ExecuteAsync();
        vm.ConfirmAppName = "demo";
        SetSchedule(vm.Schedule, InAnHour);
        await vm.ScheduleCommand.ExecuteAsync();
        vm.Error.Should().BeNull();
        return h.Main.CurrentDeletion!;
    }

    private static async Task ResumeAllAsync(Harness h)
    {
        foreach (var job in h.Main.Queue.Jobs.ToList())
            await job.ResumeCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
    }

    private static SavedJob Saved(string? account, JobKind kind = JobKind.Delete, string app = "demo", bool paused = false, DateTimeOffset? at = null) =>
        new(kind, "sub", "rg", app, at, paused, account, kind == JobKind.Delete
            ? new SavedDeletion(false, false, false, app, [new($"{app}-tt", true), new($"{app}-redis", true), new($"{app}-db", true), new($"{app}-env", true),
                new(FakeAzure.StorageAccountName, false)], false)
            : null);

    [Fact]
    public async Task Jobs_That_Have_Not_Started_Should_Be_Saved_Without_Passwords()
    {
        var h = await ReadyAsync();
        var deletion = await ScheduleDeletionAsync(h);

        var saved = h.Preferences.SavedJobs.Should().ContainSingle().Subject;
        saved.Should().BeEquivalentTo(new { Kind = JobKind.Delete, Subscription = "sub", ResourceGroup = "rg", AppName = "demo", ScheduledAt = InAnHour, Paused = false,
            Account = FakeSignIn.Alice.UserName });
        saved.Deletion!.ConfirmedAppName.Should().Be("demo");
        saved.Deletion.Targets.Should().Contain(new SavedDeletionTarget("demo-tt", true));
        saved.Text.Should().Be($"rg / demo {InAnHour.ToLocalTime():MM/dd HH:mm}");

        // 一時停止・予定の変更を残し、取り消したら消す
        await deletion.PauseCommand.ExecuteAsync();
        h.Preferences.SavedJobs.Single().Paused.Should().BeTrue();
        deletion.EditTime += TimeSpan.FromHours(1);
        await deletion.RescheduleCommand.ExecuteAsync();
        h.Preferences.SavedJobs.Single().ScheduledAt.Should().Be(InAnHour.AddHours(1));
        await deletion.CancelCommand.ExecuteAsync();
        h.Preferences.SavedJobs.Should().BeEmpty();

        // デプロイは対象と時刻だけ（パスワードは保存しない。新しいアプリにして、データの消失の同意を要らなくする）
        h.Main.Target.AppName = "web";
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        SetSchedule(h.Main.Deployment.Schedule, InAnHour);
        await h.Main.Deployment.ScheduleCommand.ExecuteAsync();
        h.Preferences.SavedJobs.Should().ContainSingle().Which.Should().Match<SavedJob>(j => j.Kind == JobKind.Deploy && j.Deletion == null);
    }

    [Fact]
    public async Task Started_Jobs_And_Expiry_Deletions_Should_Not_Be_Saved()
    {
        var h = await ReadyAsync();
        var gate = new TaskCompletionSource<Job>();
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "now"), null, () => gate.Task, saved: Saved(null, app: "now").Deletion);
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "expired"), InAnHour, () => gate.Task, expiryDeletion: true);

        h.Preferences.SavedJobs.Should().BeEmpty("始めたジョブは続きから戻せず、期限での削除は次の確認で登録し直す");
    }

    [Fact]
    public async Task Saved_Deletions_Should_Come_Back_After_Signing_In_And_Lost_Deployments_Should_Be_Told()
    {
        var preferences = new InMemoryAppPreferences
        {
            SavedJobs = [Saved(FakeSignIn.Alice.UserName, at: InAnHour), Saved(null, app: "paused", paused: true), Saved(FakeSignIn.Alice.UserName, JobKind.Deploy, "web", at: InAnHour),
                Saved("bob@example.com", app: "bobs")],
        };
        var h = new Harness(time: _time, preferences: preferences);
        h.Main.ScheduledJobs.PendingJobs.Should().Be(3);

        await h.ReadyAsync();

        h.Main.Queue.Jobs.Select(j => (j.Target.AppName, j.State, j.ScheduledAt)).Should().BeEquivalentTo(new[]
        {
            ("demo", DesktopJobState.Scheduled, (DateTimeOffset?)InAnHour), ("paused", DesktopJobState.Paused, null),
        });
        h.Notifications.Shown.Select(n => (n.Title, n.Page)).Should().Contain([("前回の予定を戻しました", Pages.Jobs), ("前回のデプロイの予定は消えました", Pages.Wizard)]);
        h.Notifications.Shown.Single(n => n.Title == "前回の予定を戻しました").Message.Should().Contain("一時停止で戻しました");
        h.Notifications.Shown.Single(n => n.Title == "前回のデプロイの予定は消えました").Message.Should().Contain("rg / web");
        // 違うアカウントのものは残す
        h.Main.ScheduledJobs.PendingJobs.Should().Be(1);
        h.Preferences.SavedJobs.Select(j => j.AppName).Should().Equal("demo", "paused", "bobs");

        // サインインし直しても、戻したものは重ねない
        h.Notifications.Shown.Clear();
        h.Main.ScheduledJobs.Restore();
        h.Notifications.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Saved_Deletion_Should_Not_Come_Back_Over_A_Job_For_The_Same_Target()
    {
        var h = new Harness(time: _time, preferences: new InMemoryAppPreferences { SavedJobs = [Saved(null, at: InAnHour)] });
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "demo"), InAnHour, () => new TaskCompletionSource<Job>().Task);

        await h.ReadyAsync();

        h.Main.Queue.Jobs.Should().ContainSingle().Which.IsDeployment.Should().BeTrue();
        h.Notifications.Shown.Should().NotContain(n => n.Title == "前回の予定を戻しました");
    }

    [Fact]
    public async Task A_Restored_Deletion_Should_Run_Only_When_The_Plan_Is_Unchanged()
    {
        // 予定の時刻を過ぎたもの・待機中だったものは一時停止で戻す。「再開」で実行する（D27・D28）
        var h = await ReadyAsync(new InMemoryAppPreferences { SavedJobs = [Saved(null, at: _time.GetUtcNow().AddHours(-1)), Saved(null, app: "other")] });
        h.Main.Queue.Jobs.Should().OnlyContain(j => j.State == DesktopJobState.Paused);
        await ResumeAllAsync(h);

        var jobs = h.Main.Queue.Jobs.ToDictionary(j => j.Target.AppName);
        jobs["demo"].Error.Should().BeNull();
        jobs["demo"].State.Should().Be(DesktopJobState.Succeeded);
        jobs["demo"].DeletionSummary.Should().StartWith("削除したもの: demo-tt, demo-redis, demo-db, demo-env。");
        // 別のアプリは Azure に無く、削除するものが無い（計画から始められない）
        jobs["other"].State.Should().Be(DesktopJobState.Failed);
        jobs["other"].Error.Should().Be("実行しませんでした: 削除するものはありませんでした（既に削除されています）。");
        h.Preferences.SavedJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Restored_Deletion_Should_Fail_When_The_Plan_Changed()
    {
        // 確かめた計画には無かったストレージアカウントが、今は計画に入る
        var saved = Saved(null);
        saved = saved with { Deletion = saved.Deletion! with { Targets = [.. saved.Deletion.Targets.SkipLast(1)] } };
        var h = await ReadyAsync(new InMemoryAppPreferences { SavedJobs = [saved] });
        await ResumeAllAsync(h);

        var job = h.Main.Queue.Jobs.Single();
        job.State.Should().Be(DesktopJobState.Failed);
        job.Error.Should().Contain("計画");
        h.Azure.Apps.Should().NotBeEmpty("削除していない");
    }

    [Fact]
    public async Task A_Restored_Deletion_Should_Not_Run_When_The_Resource_Group_Can_No_Longer_Be_Deleted()
    {
        // リソースグループも消す予定だったが、今はほかのリソースが入っている
        var saved = Saved(null);
        saved = saved with { Deletion = saved.Deletion! with { DeleteResourceGroup = true, DeletesResourceGroup = true } };
        var h = new Harness(time: _time, preferences: new InMemoryAppPreferences { SavedJobs = [saved] });
        h.Azure.Deployed();
        h.Azure.Resources.Add(new TTDeployTool.Infrastructure.Azure.AzureResourceInfo("vm1", "Microsoft.Compute/virtualMachines"));

        await h.ReadyAsync();
        await ResumeAllAsync(h);

        var job = h.Main.Queue.Jobs.Single();
        job.State.Should().Be(DesktopJobState.Failed);
        job.Error.Should().Contain("リソースグループ rg は削除できません");
        h.Azure.Apps.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Restoring_Without_Notifications_Should_Still_Register_The_Jobs()
    {
        var preferences = new InMemoryAppPreferences { SavedJobs = [Saved(null, at: InAnHour), Saved(null, JobKind.Deploy, "web", at: InAnHour)] };
        var h = new Harness(time: _time, withNotifications: false, preferences: preferences);

        await h.ReadyAsync();

        h.Main.Queue.Jobs.Should().ContainSingle().Which.Target.AppName.Should().Be("demo");
        h.Preferences.SavedJobs.Select(j => j.AppName).Should().Equal("demo");
        h.Main.Integration.ScheduledText.Should().Be(
            "まだ始めていないジョブ: 1 件は、次にアプリを起動してサインインしたときに戻します（予定の時刻を過ぎていたら一時停止で戻します）。");
    }

    // デプロイの予定を、パスワードを暗号化して残し、次の起動で戻す（オーナーの依頼、2026-10-27。6.7・D25〜D28）
    private async Task<Harness> ScheduledDeploymentAsync(Action<Harness>? arrange = null, string app = "demo", DateTimeOffset? at = null)
    {
        var h = new Harness(time: _time);
        h.Azure.ResourceGroup = null;
        arrange?.Invoke(h);
        await h.ReadyAsync();
        h.Main.Target.AppName = app;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        h.Main.Deployment.DataLossAcknowledged = true;
        SetSchedule(h.Main.Deployment.Schedule, at ?? InAnHour);
        await h.Main.Deployment.ScheduleCommand.ExecuteAsync();
        return h;
    }

    // アプリを終了して起動し直す（前のアプリのタイマーを止めてから。時刻を進めても前のアプリでは始めない）
    private async Task<Harness> RestartedAsync(Harness before, Action<Harness>? arrange = null, bool withSecrets = true)
    {
        before.Main.Dispose();
        var h = new Harness(time: _time, preferences: before.Preferences, withSecrets: withSecrets);
        h.Azure.ResourceGroup = null;
        arrange?.Invoke(h);
        return await h.ReadyAsync();
    }

    [Fact]
    public async Task A_Scheduled_Deployment_Should_Be_Saved_With_Encrypted_Passwords_And_Run_After_A_Restart()
    {
        var h = await ScheduledDeploymentAsync();

        var saved = h.Preferences.SavedJobs.Should().ContainSingle().Subject;
        var deployment = saved.Deployment!;
        (saved.Kind, deployment.Options.DbPassword, deployment.Options.TrackerPassword, deployment.Options.AppName).Should().Be((JobKind.Deploy, "", "", "demo"));
        deployment.ProtectedDbPassword.Should().StartWith("enc:");
        System.Text.Json.JsonSerializer.Serialize(h.Preferences.SavedJobs).Should().NotContain("DbPassw0rd!").And.NotContain("TrackerPass!");
        deployment.Operations.Should().NotBeEmpty();
        var options = deployment.Restore(h.Secrets);
        (options.DbPassword, options.TrackerPassword, deployment.Options.DbPassword).Should().Be(("DbPassw0rd!", "TrackerPass!", ""), "復号は複製に行い、保存したものは空のまま");

        var restarted = await RestartedAsync(h);
        var job = restarted.Main.Queue.Jobs.Should().ContainSingle().Subject;
        (job.IsDeployment, job.State, job.ScheduledAt, job.SavedDeployment).Should().Be((true, DesktopJobState.Scheduled, InAnHour, deployment));
        restarted.Notifications.Shown.Should().Contain(n => n.Title == "前回の予定を戻しました" && !n.Message.Contains("一時停止"));

        // 予定の時刻になると、復号した設定で計画を作り直して実行する。始めたら保存から消す
        _time.Advance(TimeSpan.FromHours(2));
        await restarted.Main.Queue.WhenIdleAsync();
        job.Error.Should().BeNull();
        job.State.Should().Be(DesktopJobState.Succeeded);
        job.Result.Should().BeOfType<TTDeployTool.Application.Deployment.DeploymentResult>();
        restarted.Preferences.SavedJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task An_Overdue_Deployment_Should_Come_Back_Paused_And_Fail_When_The_Plan_Changed()
    {
        var h = await ScheduledDeploymentAsync();
        h.Main.Dispose();
        _time.Advance(TimeSpan.FromHours(3));

        // 前回は無かったリソースグループが、今はある（計画の操作が変わる）
        var restarted = await RestartedAsync(h, r => r.Azure.ResourceGroup = new ResourceGroupInfo("rg", "japaneast", new Dictionary<string, string>()));
        var job = restarted.Main.Queue.Jobs.Single();
        job.State.Should().Be(DesktopJobState.Paused);
        restarted.Notifications.Shown.Single(n => n.Title == "前回の予定を戻しました").Message.Should().Contain("一時停止で戻しました");

        await ResumeAllAsync(restarted);
        job.State.Should().Be(DesktopJobState.Failed);
        job.Error.Should().Contain("計画");
    }

    [Fact]
    public async Task A_Restored_Deployment_Should_Not_Lose_Data_Without_The_Consent()
    {
        // 既存の DB アプリを更新する計画（データ消失の同意が要る）で、保存した同意が無い
        void Existing(Harness b)
        {
            b.Azure.ResourceGroup = new ResourceGroupInfo("rg", "japaneast");
            b.Azure.Deployed();
        }

        var h = await ScheduledDeploymentAsync(Existing);
        h.Preferences.SavedJobs.Single().Deployment!.Operations.Should().Contain(o => o.DataLoss);
        var saved = h.Preferences.SavedJobs.Single();
        saved.Deployment!.DataLossAcknowledged.Should().BeTrue();
        h.Preferences.SavedJobs = [saved with { Deployment = saved.Deployment with { DataLossAcknowledged = false } }];

        var restarted = await RestartedAsync(h, Existing);
        _time.Advance(TimeSpan.FromHours(2));
        await restarted.Main.Queue.WhenIdleAsync();

        restarted.Main.Queue.Jobs.Single().State.Should().Be(DesktopJobState.Failed);
    }

    [Fact]
    public async Task A_Restored_Deployment_Should_Not_Run_When_It_Can_No_Longer_Be_Run()
    {
        var h = await ScheduledDeploymentAsync();

        // 起動し直したときには、保存した利用可能期限日が過ぎている（計画に誤りが出る）
        h.Preferences.SavedJobs.Single().Deployment!.Options.ExpiresOn = "2026-01-01";
        var restarted = await RestartedAsync(h);
        _time.Advance(TimeSpan.FromHours(2));
        await restarted.Main.Queue.WhenIdleAsync();

        var job = restarted.Main.Queue.Jobs.Single();
        job.State.Should().Be(DesktopJobState.Failed);
        job.Error.Should().Contain("2026-01-01");
    }

    [Fact]
    public async Task Deployments_Should_Not_Be_Kept_When_The_Owner_Chooses_Not_To()
    {
        var h = new Harness(time: _time);
        var vm = h.Main.Deployment;
        (vm.CanKeepSchedule, vm.KeepScheduleAfterRestart).Should().Be((true, true));
        vm.ScheduleNote.Should().StartWith("予定に追加したデプロイは、アプリを終了しても、次にアプリを起動してサインインしたときに戻します");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.KeepScheduleAfterRestart = false;
        vm.KeepScheduleAfterRestart = false;
        raised.Should().Equal(nameof(DeploymentViewModel.KeepScheduleAfterRestart), nameof(DeploymentViewModel.ScheduleNote));
        vm.ScheduleNote.Should().StartWith("予定に追加したデプロイは、アプリを終了するか、Windows を再起動・サインアウトすると消えます");

        h = await ScheduledDeploymentAsync(b => b.Main.Deployment.KeepScheduleAfterRestart = false);
        h.Preferences.SavedJobs.Single().Deployment.Should().BeNull();
        var restarted = await RestartedAsync(h);
        restarted.Main.Queue.Jobs.Should().BeEmpty();
        restarted.Notifications.Shown.Single(n => n.Title == "前回のデプロイの予定は消えました").Message.Should().Contain("rg / demo");
    }

    [Fact]
    public async Task Deployments_Run_Now_Should_Not_Be_Saved()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.Azure.ResourceGroup = null;
        h.Main.Queue.MaxConcurrency = 1;
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "busy"), null, () => new TaskCompletionSource<Job>().Task);
        h.Main.Target.AppName = "web";
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();

        // 待機中（同時に実行できる数に達している）でも、今すぐ実行したものはパスワードを保存しない
        h.Main.CurrentDeployment!.State.Should().Be(DesktopJobState.Waiting);
        h.Preferences.SavedJobs.Should().ContainSingle().Which.Deployment.Should().BeNull();
    }

    [Fact]
    public async Task Without_Encryption_Deployments_Should_Not_Be_Kept()
    {
        var h = new Harness(time: _time, withSecrets: false);
        (h.Main.Deployment.CanKeepSchedule, h.Main.Deployment.ScheduleNote.Contains("消えます")).Should().Be((false, true));

        // 保存したものがあっても、復号の仕組みが無ければ戻さない
        var saved = await ScheduledDeploymentAsync();
        var restarted = await RestartedAsync(saved, withSecrets: false);
        restarted.Main.Queue.Jobs.Should().BeEmpty();
        restarted.Notifications.Shown.Should().Contain(n => n.Title == "前回のデプロイの予定は消えました");
    }

    [Fact]
    public async Task Encryption_Failures_Should_Be_Told_And_Not_Lose_The_Schedule_In_This_Run()
    {
        var h = await ScheduledDeploymentAsync(b => b.Secrets.Fail = true);

        h.Main.CurrentDeployment!.State.Should().Be(DesktopJobState.Scheduled);
        h.Main.Deployment.Error.Should().StartWith("予定は登録しましたが、アプリを終了しても残すための保存ができませんでした（暗号化できません。）");
        h.Preferences.SavedJobs.Single().Deployment.Should().BeNull();

        // 別の PC・Windows ユーザーで保存したもの（復号できない）は戻さない
        var ok = await ScheduledDeploymentAsync();
        var restarted = await RestartedAsync(ok, r => r.Secrets.Fail = true);
        restarted.Main.Queue.Jobs.Should().BeEmpty();
        restarted.Notifications.Shown.Should().Contain(n => n.Title == "前回のデプロイの予定は消えました");
    }

    [Fact]
    public void Nothing_Saved_Should_Be_Read_From_A_Missing_File() =>
        new FilePreferences(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "preferences.json")).SavedJobs.Should().BeEmpty();

    [Fact]
    public void A_Saved_Plan_Should_Match_Only_The_Same_Deletions()
    {
        var plan = new TTDeployTool.Application.Deletion.DeletionPlan("sub", "rg", "demo",
            [new("demo-tt", TTDeployTool.Application.Deletion.DeletionResourceKind.ContainerApp, true, true, true, null)],
            new TTDeployTool.Application.Permissions.PermissionCheckResult(TTDeployTool.Application.Permissions.PermissionCheckStatus.Ok, [], null),
            ResourceGroupTarget: new TTDeployTool.Application.Deletion.ResourceGroupDeletion(true, true, null, []));
        var saved = SavedDeletion.From(plan, "demo");

        saved.Should().BeEquivalentTo(new { DeleteResourceGroup = true, DeletesResourceGroup = true, ConfirmedAppName = "demo" });
        saved.Matches(plan).Should().BeTrue();
        saved.Matches(plan with { ResourceGroupTarget = null }).Should().BeFalse();
        saved.Matches(plan with { Targets = [plan.Targets[0] with { WillDelete = false }] }).Should().BeFalse();
    }
}
