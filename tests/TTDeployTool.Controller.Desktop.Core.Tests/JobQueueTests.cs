using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>ジョブの順番待ち・予定・同時実行の上限・一時停止・取り消し・停止（詳細設計 desktop-jobs.md の 4）。</summary>
public sealed class JobQueueTests : IDisposable
{
    private static readonly TimeSpan Check = TimeSpan.FromSeconds(15);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAppPreferences _preferences = new();
    private readonly JobManager _jobs;
    private readonly JobQueue _queue;

    public JobQueueTests()
    {
        _jobs = new JobManager(new InMemoryJobStore(), _time, NullLogger<JobManager>.Instance);
        _queue = new JobQueue(_time, TimeSpan.FromSeconds(1), Check, _preferences, NullLogger.Instance);
    }

    public void Dispose() => _queue.Dispose();

    /// <summary>手順を 2 つ持ち、<see cref="Release"/> まで 1 つ目の手順で止まるジョブ。</summary>
    private sealed class Work
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts { get; private set; }

        public Func<Task<Job>> Starter(JobManager jobs, JobTarget target, JobKind kind = JobKind.Deploy) => () =>
        {
            Starts++;
            return Task.FromResult(jobs.TryStart(kind, target, "local", async ctx =>
            {
                ctx.ReportStep(1, 2, "1 つ目");
                await Release.Task;
                ctx.ReportStep(2, 2, "2 つ目");
                return null;
            }).Job!);
        };
    }

    private static JobTarget Target(string app) => new("sub", "rg", app);

    // バックグラウンドで続く処理（ジョブの開始・状態の読み直し）を待つ
    internal static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        condition().Should().BeTrue();
    }

    private JobViewModel Register(string app, Work work, DateTimeOffset? at = null) =>
        _queue.Register(JobKind.Deploy, Target(app), at, work.Starter(_jobs, Target(app)));

    [Fact]
    public async Task Jobs_Should_Run_Up_To_The_Limit_And_Then_Wait()
    {
        var a = new Work();
        var b = new Work();
        var c = new Work();
        _queue.MaxConcurrency.Should().Be(JobQueue.DefaultConcurrency);
        _queue.Summary.Should().BeEmpty();
        _queue.HasActive.Should().BeFalse();

        var first = Register("a", a);
        var second = Register("b", b);
        var third = Register("c", c);

        first.State.Should().Be(DesktopJobState.Running);
        second.State.Should().Be(DesktopJobState.Running);
        third.State.Should().Be(DesktopJobState.Waiting);
        third.StatusText.Should().StartWith("待機中");
        third.StepText.Should().BeNull();
        c.Starts.Should().Be(0);
        _queue.Summary.Should().Be("実行中 2・待機中 1");
        _queue.HasActive.Should().BeTrue();
        _queue.Jobs.Should().Equal(third, second, first);

        // 1 つ終われば、待機中のものを始める
        a.Release.SetResult();
        await first.Finished;
        await Until(() => third.State == DesktopJobState.Running);
        first.State.Should().Be(DesktopJobState.Succeeded);
        first.IsActive.Should().BeFalse();

        b.Release.SetResult();
        c.Release.SetResult();
        await _queue.WhenIdleAsync();
        _queue.Summary.Should().BeEmpty();
        _queue.ClearFinished();
        _queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Raising_The_Limit_Should_Start_Waiting_Jobs_And_Be_Saved()
    {
        var works = Enumerable.Range(0, 3).Select(_ => new Work()).ToList();
        _queue.MaxConcurrency = 1;
        var jobs = works.Select((w, i) => Register($"app{i}", w)).ToList();
        jobs.Select(j => j.State).Should().Equal(DesktopJobState.Running, DesktopJobState.Waiting, DesktopJobState.Waiting);

        _queue.MaxConcurrency = 3;

        jobs.Select(j => j.State).Should().AllBeEquivalentTo(DesktopJobState.Running);
        _preferences.MaxConcurrentJobs.Should().Be(3);
        // 範囲の外は丸める。同じ値なら何もしない
        _queue.MaxConcurrency = 0;
        _queue.MaxConcurrency.Should().Be(1);
        _queue.MaxConcurrency = 9;
        _queue.MaxConcurrency.Should().Be(4);
        var changed = 0;
        _queue.PropertyChanged += (_, e) => changed += e.PropertyName == nameof(JobQueue.MaxConcurrency) ? 1 : 0;
        _queue.MaxConcurrency = 4;
        changed.Should().Be(0);
        JobQueue.ConcurrencyChoices.Should().Equal(1, 2, 3, 4);

        works.ForEach(w => w.Release.SetResult());
        await _queue.WhenIdleAsync();
    }

    [Fact]
    public async Task The_Same_Target_Should_Not_Be_Registered_Twice()
    {
        var work = new Work();
        var job = Register("demo", work);

        _queue.ActiveFor("SUB", "RG", "Demo").Should().BeSameAs(job);
        _queue.ActiveFor("sub", "rg", "other").Should().BeNull();
        var again = () => Register("demo", new Work());
        again.Should().Throw<InvalidOperationException>().WithMessage("この対象のジョブ（デプロイ（rg / demo）: 実行中）があるため、計画・実行できません。*");

        work.Release.SetResult();
        await job.Finished;
        _queue.ActiveFor("sub", "rg", "demo").Should().BeNull();
    }

    [Fact]
    public async Task Scheduled_Jobs_Should_Start_At_Their_Time()
    {
        var work = new Work();
        var at = _time.GetUtcNow().AddMinutes(10);
        var job = Register("demo", work, at);

        job.State.Should().Be(DesktopJobState.Scheduled);
        job.ScheduledAt.Should().Be(at);
        job.StatusText.Should().Be($"予定（{at.ToLocalTime():MM/dd HH:mm}）");
        _queue.Summary.Should().Be("予定 1");

        _time.Advance(TimeSpan.FromMinutes(9));
        job.State.Should().Be(DesktopJobState.Scheduled);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Until(() => job.State == DesktopJobState.Running);
        job.Note.Should().BeNull("遅れていない");

        work.Release.SetResult();
        await job.Finished;
    }

    [Fact]
    public async Task Jobs_Past_Their_Time_Should_Run_When_Resumed_And_Say_They_Were_Late()
    {
        var work = new Work();
        var at = _time.GetUtcNow().AddMinutes(1);
        var job = Register("demo", work, at);
        job.PauseCommand.CanExecute(null).Should().BeTrue();
        job.ResumeCommand.CanExecute(null).Should().BeFalse();
        job.StopCommand.CanExecute(null).Should().BeFalse();

        await job.PauseCommand.ExecuteAsync();
        job.State.Should().Be(DesktopJobState.Paused);
        job.StatusText.Should().Be("一時停止");
        _queue.Summary.Should().Be("一時停止 1");
        job.PauseCommand.CanExecute(null).Should().BeFalse();
        // 予定の時刻を過ぎても、一時停止のまま
        _time.Advance(TimeSpan.FromMinutes(5));
        job.State.Should().Be(DesktopJobState.Paused);

        await job.ResumeCommand.ExecuteAsync();

        await Until(() => job.State == DesktopJobState.Running);
        job.Note.Should().StartWith($"予定（{at.ToLocalTime():MM/dd HH:mm}）より 4 分遅れて実行しました");
        work.Release.SetResult();
        await job.Finished;
    }

    [Fact]
    public async Task Resuming_Before_The_Time_Should_Wait_For_It_And_Rescheduling_Should_Move_It()
    {
        var work = new Work();
        var job = Register("demo", work, _time.GetUtcNow().AddMinutes(30));
        await job.PauseCommand.ExecuteAsync();

        // 一時停止中に予定を変えても、一時停止のまま
        job.EditDate = _time.GetUtcNow();
        job.EditTime = _time.GetUtcNow().TimeOfDay + TimeSpan.FromMinutes(5);
        job.RescheduleCommand.CanExecute(null).Should().BeTrue();
        await job.RescheduleCommand.ExecuteAsync();
        job.State.Should().Be(DesktopJobState.Paused);
        job.ScheduledAt.Should().Be(_time.GetUtcNow().AddMinutes(5));

        await job.ResumeCommand.ExecuteAsync();
        job.State.Should().Be(DesktopJobState.Scheduled);

        job.EditTime = _time.GetUtcNow().TimeOfDay + TimeSpan.FromMinutes(20);
        await job.RescheduleCommand.ExecuteAsync();
        job.ScheduledAt.Should().Be(_time.GetUtcNow().AddMinutes(20));
        _time.Advance(TimeSpan.FromMinutes(10));
        job.State.Should().Be(DesktopJobState.Scheduled, "変えた予定の時刻までは待つ");

        _time.Advance(TimeSpan.FromMinutes(10));
        await Until(() => job.State == DesktopJobState.Running);
        job.RescheduleCommand.CanExecute(null).Should().BeFalse();
        work.Release.SetResult();
        await job.Finished;
    }

    [Fact]
    public async Task Waiting_Jobs_Can_Be_Paused_And_Canceled_And_Canceled_Ones_Never_Run()
    {
        _queue.MaxConcurrency = 1;
        var running = new Work();
        var waiting = new Work();
        var first = Register("a", running);
        var second = Register("b", waiting);
        second.CancelCommand.CanExecute(null).Should().BeTrue();
        second.PauseCommand.CanExecute(null).Should().BeTrue();
        second.RescheduleCommand.CanExecute(null).Should().BeFalse();
        first.CancelCommand.CanExecute(null).Should().BeFalse("実行中は取り消せない（停止する）");

        await second.PauseCommand.ExecuteAsync();
        await second.ResumeCommand.ExecuteAsync();
        second.State.Should().Be(DesktopJobState.Waiting);
        await second.CancelCommand.ExecuteAsync();
        second.State.Should().Be(DesktopJobState.Canceled);
        second.StatusText.Should().Be("取り消し");
        second.CancelCommand.CanExecute(null).Should().BeFalse();
        await second.Finished;

        running.Release.SetResult();
        await _queue.WhenIdleAsync();
        waiting.Starts.Should().Be(0);
        // 取り消した対象は、また登録できる
        Register("b", new Work()).Should().NotBeSameAs(second);
    }

    [Fact]
    public async Task Running_Jobs_Should_Stop_After_The_Current_Step()
    {
        var work = new Work();
        var job = Register("demo", work);
        // 状態は読み直しの間隔（1 秒）ごとに読む
        await Until(() =>
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            return job.StepText == "1 / 2: 1 つ目";
        });
        job.StopCommand.CanExecute(null).Should().BeTrue();

        await job.StopCommand.ExecuteAsync();

        job.State.Should().Be(DesktopJobState.Stopping);
        job.StatusText.Should().StartWith("停止中");
        job.IsRunning.Should().BeTrue();
        job.StopCommand.CanExecute(null).Should().BeFalse();
        work.Release.SetResult();
        await job.Finished;

        job.State.Should().Be(DesktopJobState.Stopped);
        job.StatusText.Should().Be("停止");
        job.Error.Should().StartWith("1 つ目（手順 1/2）の後で停止しました。");
        // 終わったジョブに停止を求めても、何もしない
        _queue.Stop(job);
        job.State.Should().Be(DesktopJobState.Stopped);
    }

    [Fact]
    public async Task Jobs_That_Cannot_Start_Should_Fail_Without_Running()
    {
        var job = _queue.Register(JobKind.Delete, Target("demo"), null,
            () => Task.FromException<Job>(new TTDeployTool.Application.Workflows.WorkflowException(TTDeployTool.Application.Workflows.WorkflowFailure.Changed)));

        await job.Finished;

        job.State.Should().Be(DesktopJobState.Failed);
        job.Title.Should().Be("削除（rg / demo）");
        job.Error.Should().Be("実行しませんでした: " + DesktopMessages.Changed);
        job.StatusText.Should().Be("失敗");
    }

    [Fact]
    public void Jobs_That_Have_Not_Started_Should_Have_No_Result()
    {
        var job = Register("demo", new Work(), _time.GetUtcNow().AddHours(1));

        (job.Error, job.Endpoint, job.HasEndpoint, job.PersistenceSummary, job.DeletionSummary, job.StepText)
            .Should().Be(((string?)null, (Uri?)null, false, (string?)null, (string?)null, (string?)null));
    }

    [Fact]
    public async Task Every_Status_Of_A_Started_Job_Should_Be_Shown()
    {
        Task<Job> Start(string app, Func<JobContext, Task<object?>> work) =>
            Task.FromResult(_jobs.TryStart(JobKind.Deploy, Target(app), "local", work).Job!);
        var succeeded = _queue.Register(JobKind.Deploy, Target("ok"), null, () => Start("ok", _ => Task.FromResult<object?>(null)));
        var failed = _queue.Register(JobKind.Deploy, Target("ng"), null, () => Start("ng", _ => throw new InvalidOperationException("失敗した")));
        await Task.WhenAll(succeeded.Finished, failed.Finished);

        (succeeded.State, succeeded.StatusText).Should().Be((DesktopJobState.Succeeded, "完了"));
        (failed.State, failed.StatusText).Should().Be((DesktopJobState.Failed, "失敗"));
        failed.Error.Should().StartWith("失敗した\n");
    }

    [Fact]
    public void Jobs_Shown_Without_A_Queue_Should_Not_Offer_Controls()
    {
        var job = new JobViewModel(_jobs.TryStart(JobKind.Deploy, Target("demo"), "local", _ => Task.FromResult<object?>(null)).Job!);

        new[] { job.CancelCommand, job.PauseCommand, job.ResumeCommand, job.RescheduleCommand, job.StopCommand }
            .Should().OnlyContain(c => !c.CanExecute(null));
    }

    [Fact]
    public void The_Starting_State_Should_Explain_The_Last_Check()
    {
        var pending = new TaskCompletionSource<Job>();
        var job = _queue.Register(JobKind.Deploy, Target("demo"), null, () => pending.Task);

        job.State.Should().Be(DesktopJobState.Starting);
        job.StatusText.Should().Be("実行の準備中");
        job.StepText.Should().StartWith("実行の直前の確認をしています");
        job.IsRunning.Should().BeTrue();
        _queue.Summary.Should().Be("実行中 1");
        pending.SetException(new InvalidOperationException("やめた"));
    }

    [Fact]
    public void The_Timer_Should_Post_To_The_Context_It_Was_Created_On()
    {
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            using var queue = new JobQueue(_time, TimeSpan.FromSeconds(1), Check, _preferences, NullLogger.Instance);
            _time.Advance(Check);
            context.Posts.Should().Be(1);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void Schedule_Inputs_Should_Combine_The_Date_And_Time_And_Reject_The_Past()
    {
        var input = new ScheduleInput(_time);
        var local = _time.GetLocalNow().AddHours(1);
        input.Date.Date.Should().Be(local.Date);
        input.TimeOfDay.Should().Be(new TimeSpan(local.Hour, local.Minute, 0));
        input.Error.Should().BeNull();

        input.Date = _time.GetLocalNow();
        input.TimeOfDay = TimeSpan.Zero;
        input.Error.Should().StartWith("予定の時刻（").And.EndWith("）は、今より後にしてください。");

        ScheduleTime.Combine(new DateTimeOffset(2026, 10, 21, 23, 59, 0, TimeSpan.FromHours(9)), new TimeSpan(8, 30, 0))
            .Should().Be(new DateTimeOffset(2026, 10, 21, 8, 30, 0, TimeSpan.FromHours(9)));
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Posts++;
            d(state);
        }
    }
}

/// <summary>デプロイ・削除の画面からのジョブの登録（予定・同じ対象・計画の有効期限の後の実行）。</summary>
public class JobRegistrationTests
{
    private static async Task<Harness> ReadyAsync(FakeTimeProvider time) => await new Harness(time: time).ReadyAsync();

    private static FakeTimeProvider Time() => new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_Scheduled_Deployment_Should_Replan_After_The_Plan_Expired_And_Run()
    {
        var time = Time();
        var h = await ReadyAsync(time);
        var vm = h.Main.Deployment;
        await vm.PlanCommand.ExecuteAsync();
        vm.ScheduleCommand.CanExecute(null).Should().BeTrue();
        vm.Schedule.Date = time.GetUtcNow();
        vm.Schedule.TimeOfDay = time.GetUtcNow().TimeOfDay + TimeSpan.FromHours(1);

        await vm.ScheduleCommand.ExecuteAsync();

        vm.HasPlan.Should().BeFalse();
        var job = h.Main.CurrentDeployment!;
        job.State.Should().Be(DesktopJobState.Scheduled);
        h.Main.HasRunningJob.Should().BeTrue();
        // 同じ対象は、ジョブが終わるまで計画できない
        await vm.PlanCommand.ExecuteAsync();
        vm.Error.Should().StartWith("この対象のジョブ（デプロイ（rg / demo）: 予定（");
        vm.HasPlan.Should().BeFalse();

        time.Advance(TimeSpan.FromHours(1));
        await h.Main.Queue.WhenIdleAsync();

        job.State.Should().Be(DesktopJobState.Succeeded);
        job.HasEndpoint.Should().BeTrue();
        h.Main.OpenEndpointCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task A_Late_Deployment_Should_Not_Run_When_Azure_Changed_Or_Blocks_It()
    {
        var time = Time();
        var h = await ReadyAsync(time);
        var vm = h.Main.Deployment;

        // 計画の後に、別の PC などでリソースが作られた
        await vm.PlanCommand.ExecuteAsync();
        h.Main.Queue.MaxConcurrency = 1;
        var blocker = h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "blocker"), null, () => new TaskCompletionSource<Job>().Task);
        await vm.StartCommand.ExecuteAsync();
        var changed = h.Main.CurrentDeployment!;
        changed.State.Should().Be(DesktopJobState.Waiting);
        time.Advance(TimeSpan.FromMinutes(16));
        h.Azure.Apps.Add(new ContainerAppInfo("demo-redis", FakeAzure.EnvId, FakeAzure.Managed));
        await blocker.CancelCommand.ExecuteAsync();
        h.Main.Queue.MaxConcurrency = 2;
        await changed.Finished;
        changed.Error.Should().Be("実行しませんでした: " + DesktopMessages.Changed);

        // 計画し直すと、データ消失の同意が新たに要る（DB アプリができた）
        h.Azure.Apps.Clear();
        await vm.PlanCommand.ExecuteAsync();
        vm.Schedule.Date = time.GetUtcNow();
        vm.Schedule.TimeOfDay = time.GetUtcNow().TimeOfDay + TimeSpan.FromMinutes(30);
        await vm.ScheduleCommand.ExecuteAsync();
        var needsAck = h.Main.CurrentDeployment!;
        h.Azure.Deployed();
        time.Advance(TimeSpan.FromMinutes(30));
        await needsAck.Finished;
        needsAck.Error.Should().Be("実行しませんでした: " + DesktopMessages.Changed);

        // 計画し直すと、権限が足りない
        h.Azure.Apps.Clear();
        h.Azure.Environment = null;
        await vm.PlanCommand.ExecuteAsync();
        vm.Schedule.TimeOfDay = time.GetUtcNow().TimeOfDay + TimeSpan.FromMinutes(30);
        await vm.ScheduleCommand.ExecuteAsync();
        var blocked = h.Main.CurrentDeployment!;
        h.Azure.ResourceGroup = new ResourceGroupInfo("rg", "japaneast");
        h.Azure.Permissions[0] = new PermissionSet(["*/read"], []);
        time.Advance(TimeSpan.FromMinutes(30));
        await blocked.Finished;
        blocked.Error.Should().StartWith("実行しませんでした: ");
        blocked.State.Should().Be(DesktopJobState.Failed);
    }

    [Fact]
    public async Task An_Acknowledged_Data_Loss_Should_Still_Allow_A_Late_Update()
    {
        var time = Time();
        var h = await ReadyAsync(time);
        h.Azure.Deployed();
        var vm = h.Main.Deployment;
        await vm.PlanCommand.ExecuteAsync();
        vm.RequiresDataLossAck.Should().BeTrue();
        vm.DataLossAcknowledged = true;
        vm.Schedule.Date = time.GetUtcNow();
        vm.Schedule.TimeOfDay = time.GetUtcNow().TimeOfDay + TimeSpan.FromHours(1);

        await vm.ScheduleCommand.ExecuteAsync();
        var job = h.Main.CurrentDeployment!;
        time.Advance(TimeSpan.FromHours(1));
        await job.Finished;

        // 計画し直しても同じ操作で、同意も得ているため実行する
        job.State.Should().Be(DesktopJobState.Succeeded);
    }

    [Fact]
    public async Task Scheduling_In_The_Past_Or_For_A_Busy_Target_Should_Be_Refused()
    {
        var time = Time();
        var h = await ReadyAsync(time);
        var vm = h.Main.Deployment;
        await vm.PlanCommand.ExecuteAsync();
        vm.Schedule.Date = time.GetUtcNow();
        vm.Schedule.TimeOfDay = TimeSpan.Zero;

        await vm.ScheduleCommand.ExecuteAsync();

        vm.Error.Should().EndWith("は、今より後にしてください。");
        vm.HasPlan.Should().BeTrue("予定の誤りでは、計画を使わない");

        // 計画の後に、同じ対象のジョブが（別の画面などから）登録された
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "demo"), time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);
        await vm.StartCommand.ExecuteAsync();
        vm.Error.Should().StartWith("この対象のジョブ（削除（rg / demo）: 予定（");
        vm.HasPlan.Should().BeTrue();
    }

    [Fact]
    public async Task A_Scheduled_Deletion_Should_Replan_After_The_Plan_Expired()
    {
        var time = Time();
        var h = await ReadyAsync(time);
        h.Azure.Deployed();
        var vm = h.Main.Deletion;
        await vm.PlanCommand.ExecuteAsync();
        vm.ConfirmAppName = "demo";
        vm.Schedule.Date = time.GetUtcNow();
        vm.Schedule.TimeOfDay = time.GetUtcNow().TimeOfDay + TimeSpan.FromHours(2);
        vm.ScheduleCommand.CanExecute(null).Should().BeTrue();

        await vm.ScheduleCommand.ExecuteAsync();

        var job = h.Main.CurrentDeletion!;
        job.State.Should().Be(DesktopJobState.Scheduled);
        await vm.PlanCommand.ExecuteAsync();
        vm.Error.Should().StartWith("この対象のジョブ（削除（rg / demo）: 予定（");

        time.Advance(TimeSpan.FromHours(2));
        await job.Finished;
        job.State.Should().Be(DesktopJobState.Succeeded);
        job.DeletionSummary.Should().StartWith("削除したもの: demo-tt, demo-redis, demo-db, demo-env。");
    }

    [Fact]
    public async Task A_Late_Deletion_Should_Not_Run_When_Azure_Changed_Or_Blocks_It()
    {
        var time = Time();
        var h = await ReadyAsync(time);
        var vm = h.Main.Deletion;

        h.Azure.Deployed();
        await vm.PlanCommand.ExecuteAsync();
        vm.ConfirmAppName = "demo";
        vm.Schedule.Date = time.GetUtcNow();
        vm.Schedule.TimeOfDay = time.GetUtcNow().TimeOfDay + TimeSpan.FromHours(1);
        await vm.ScheduleCommand.ExecuteAsync();
        var changed = h.Main.CurrentDeletion!;
        h.Azure.Apps.RemoveAt(0);
        time.Advance(TimeSpan.FromHours(1));
        await changed.Finished;
        changed.Error.Should().Be("実行しませんでした: " + DesktopMessages.Changed);

        h.Azure.Apps.Clear();
        h.Azure.Deployed();
        await vm.PlanCommand.ExecuteAsync();
        vm.ConfirmAppName = "demo";
        vm.Schedule.TimeOfDay = time.GetUtcNow().TimeOfDay + TimeSpan.FromHours(1);
        await vm.ScheduleCommand.ExecuteAsync();
        var blocked = h.Main.CurrentDeletion!;
        h.Azure.Permissions[0] = new PermissionSet(["*/read"], []);
        time.Advance(TimeSpan.FromHours(1));
        await blocked.Finished;
        blocked.Error.Should().StartWith("実行しませんでした: ");

        // 予定の誤りと、同じ対象のジョブ
        h.Azure.Permissions[0] = new PermissionSet(["*"], []);
        await vm.PlanCommand.ExecuteAsync();
        vm.ConfirmAppName = "demo";
        vm.Schedule.TimeOfDay = TimeSpan.Zero;
        vm.Schedule.Date = time.GetUtcNow().AddDays(-1);
        await vm.ScheduleCommand.ExecuteAsync();
        vm.Error.Should().EndWith("は、今より後にしてください。");
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "demo"), time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);
        await vm.StartCommand.ExecuteAsync();
        vm.Error.Should().StartWith("この対象のジョブ（デプロイ（rg / demo）: 予定（");
    }

    [Fact]
    public async Task Closing_Should_Warn_About_Every_Unfinished_Job()
    {
        var time = Time();
        var h = await ReadyAsync(time);
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        h.Main.Deployment.Schedule.Date = time.GetUtcNow();
        h.Main.Deployment.Schedule.TimeOfDay = time.GetUtcNow().TimeOfDay + TimeSpan.FromHours(1);
        var notified = new List<string?>();
        h.Main.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        await h.Main.Deployment.ScheduleCommand.ExecuteAsync();

        notified.Should().Contain(nameof(MainViewModel.HasRunningJob));
        (await h.Main.ConfirmExitAsync()).Should().BeFalse();
        h.Dialogs.Messages.Should().ContainSingle().Which.Should().Contain("ジョブ（予定 1）があります。");
        h.Main.Dispose();
    }
}

public sealed class JobListAndPreferencesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ttdeploy-pref-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Clear_Finished_Should_Remove_Only_Finished_Jobs()
    {
        var h = await new Harness().ReadyAsync();
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        var waiting = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "other"), DateTimeOffset.UtcNow.AddHours(1),
            () => new TaskCompletionSource<Job>().Task);

        await h.Main.ClearFinishedCommand.ExecuteAsync();

        h.Main.Jobs.Should().Equal(waiting);
    }

    [Fact]
    public void File_Preferences_Should_Save_And_Read_Back_The_Concurrency()
    {
        var path = Path.Combine(_dir, "data", "preferences.json");
        new FilePreferences(path).MaxConcurrentJobs.Should().BeNull("ファイルが無ければ未設定");

        new FilePreferences(path) { MaxConcurrentJobs = 3 };

        new FilePreferences(path).MaxConcurrentJobs.Should().Be(3);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void File_Preferences_Should_Use_Defaults_When_The_File_Is_Unreadable(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "preferences.json");
        File.WriteAllText(path, content);

        new FilePreferences(path).MaxConcurrentJobs.Should().BeNull();
    }

    [Fact]
    public void File_Preferences_Should_Keep_The_Value_When_It_Cannot_Save()
    {
        // 保存先のフォルダーの名前で、ファイルがある（フォルダーを作れない）
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "blocked"), "");
        var preferences = new FilePreferences(Path.Combine(_dir, "blocked", "preferences.json"));

        preferences.MaxConcurrentJobs = 4;

        preferences.MaxConcurrentJobs.Should().Be(4);
    }
}
