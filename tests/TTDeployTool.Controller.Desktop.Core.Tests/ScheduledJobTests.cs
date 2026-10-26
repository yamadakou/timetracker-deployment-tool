using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>アプリを終了しても残す予定（docs/design/desktop-jobs.md の 6.6、D23・D24）。</summary>
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
        h.Main.ScheduledJobs.PendingDeletions.Should().Be(3);

        await h.ReadyAsync();

        h.Main.Queue.Jobs.Select(j => (j.Target.AppName, j.State, j.ScheduledAt)).Should().BeEquivalentTo(new[]
        {
            ("demo", DesktopJobState.Scheduled, (DateTimeOffset?)InAnHour), ("paused", DesktopJobState.Paused, null),
        });
        h.Notifications.Shown.Select(n => (n.Title, n.Page)).Should().Contain([("前回の削除の予定を戻しました", Pages.Jobs), ("前回のデプロイの予定は消えました", Pages.Wizard)]);
        h.Notifications.Shown.Single(n => n.Title == "前回のデプロイの予定は消えました").Message.Should().Contain("rg / web");
        // 違うアカウントのものは残す
        h.Main.ScheduledJobs.PendingDeletions.Should().Be(1);
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
        h.Notifications.Shown.Should().NotContain(n => n.Title == "前回の削除の予定を戻しました");
    }

    [Fact]
    public async Task A_Restored_Deletion_Should_Run_Only_When_The_Plan_Is_Unchanged()
    {
        var h = await ReadyAsync(new InMemoryAppPreferences { SavedJobs = [Saved(null), Saved(null, app: "other")] });
        await h.Main.Queue.WhenIdleAsync();

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
        await h.Main.Queue.WhenIdleAsync();

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
        await h.Main.Queue.WhenIdleAsync();

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
            "まだ始めていないジョブ: デプロイ 0 件はアプリの終了・Windows の再起動・サインアウトで消えます（パスワードを保存しないため）。削除 1 件は、次にアプリを起動してサインインしたときに戻します。");
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
