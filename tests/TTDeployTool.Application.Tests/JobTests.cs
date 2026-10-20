using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TTDeployTool.Application.Jobs;
using Xunit;

namespace TTDeployTool.Application.Tests;

/// <summary>テストで時刻を進められる TimeProvider。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

public class InMemoryPlanStoreTests
{
    private readonly ManualTimeProvider _time = new();

    private sealed record PlanA(string Value);
    private sealed record PlanB(string Value);

    [Fact]
    public void Plans_Should_Be_Taken_Only_Once_By_Their_Caller()
    {
        var store = new InMemoryPlanStore(_time);
        var plan = new PlanA("a");

        var saved = store.Save(plan, "alice");

        saved.PlanId.Should().MatchRegex("^pln_[0-9a-f]{32}$");
        saved.ExpiresAt.Should().Be(_time.Now + TimeSpan.FromMinutes(15));
        store.Find<PlanA>(saved.PlanId, "bob").Status.Should().Be(PlanLookupStatus.NotFound);
        store.Take<PlanA>(saved.PlanId, "bob").Status.Should().Be(PlanLookupStatus.NotFound);
        store.Take<PlanB>(saved.PlanId, "alice").Status.Should().Be(PlanLookupStatus.NotFound);
        store.Find<PlanA>(saved.PlanId, "alice").Should().Be(new PlanLookup<PlanA>(PlanLookupStatus.Found, plan));
        store.Take<PlanA>(saved.PlanId, "alice").Should().Be(new PlanLookup<PlanA>(PlanLookupStatus.Found, plan));
        store.Take<PlanA>(saved.PlanId, "alice").Status.Should().Be(PlanLookupStatus.NotFound);
    }

    [Fact]
    public void Expired_Plans_Should_Not_Be_Returned_And_Should_Be_Cleaned_Up()
    {
        var store = new InMemoryPlanStore(_time, TimeSpan.FromMinutes(1));
        var first = store.Save(new PlanA("a"), "alice");
        var second = store.Save(new PlanA("b"), "alice");

        _time.Now += TimeSpan.FromMinutes(1);

        store.Find<PlanA>(first.PlanId, "alice").Should().Be(new PlanLookup<PlanA>(PlanLookupStatus.Expired, null));
        store.Take<PlanA>(first.PlanId, "alice").Status.Should().Be(PlanLookupStatus.Expired);
        // 次に保存したときに、期限切れの計画を捨てる
        store.Save(new PlanA("c"), "alice");
        store.Take<PlanA>(second.PlanId, "alice").Status.Should().Be(PlanLookupStatus.NotFound);
    }

    [Fact]
    public void LocalCallerContext_Should_Use_A_Fixed_Id()
    {
        new LocalCallerContext().CallerId.Should().Be("local");
    }
}

public class JobManagerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly JobManager _jobs;

    private static readonly JobTarget Target = new("sub", "rg", "demo");

    public JobManagerTests()
    {
        _jobs = new JobManager(new InMemoryJobStore(), _time, NullLogger<JobManager>.Instance);
    }

    [Fact]
    public async Task A_Job_Should_Record_Steps_Logs_And_The_Result()
    {
        var release = new TaskCompletionSource();
        var started = _jobs.TryStart(JobKind.Deploy, Target, "local", async ctx =>
        {
            ctx.ReportStep(1, 3, "リソースグループ rg");
            ctx.Logger.Info("info");
            ctx.Logger.Verbose("verbose");
            ctx.Logger.Warn("warn");
            ctx.Logger.Success("success");
            ctx.Logger.Error("error");
            ctx.Logger.Exception(new InvalidOperationException("exception"));
            ctx.Logger.Info("デバイスコード認証: https://microsoft.com/devicelogin でコード ABC を入力してください");
            await release.Task;
            return "result";
        });
        var job = started.Job!;
        started.ConflictingJobId.Should().BeNull();
        job.Id.Should().MatchRegex("^job_[0-9a-f]{32}$");
        (job.Kind, job.Target, job.CallerId, job.CreatedAt).Should().Be((JobKind.Deploy, Target, "local", _time.Now));

        // 実行中は、同じ対象のジョブ（大文字小文字は区別しない）を始められない
        var conflict = _jobs.TryStart(JobKind.Delete, new JobTarget("SUB", "RG", "Demo"), "local", _ => Task.FromResult<object?>(null));
        conflict.Should().Be(new JobStartResult(null, job.Id));

        release.SetResult();
        await job.Completion;

        var snapshot = job.Snapshot();
        snapshot.Status.Should().Be(JobStatus.Succeeded);
        snapshot.Result.Should().Be("result");
        snapshot.Error.Should().BeNull();
        snapshot.CompletedAt.Should().Be(_time.Now);
        snapshot.CurrentStep.Should().Be(new JobStep(1, 3, "リソースグループ rg"));
        snapshot.Logs.Select(l => (l.Level, l.Message)).Should().Equal(
            ("info", "info"), ("verbose", "verbose"), ("warn", "warn"), ("success", "success"), ("error", "error"), ("error", "exception"),
            ("info", "デバイスコード認証: https://microsoft.com/devicelogin でコード ABC を入力してください"));
        snapshot.DeviceCodeMessage.Should().StartWith("デバイスコード認証:");
        snapshot.NextLogOffset.Should().Be(7);
        // 続きから取得する（範囲外の位置は丸める）
        job.Snapshot(5).Logs.Should().HaveCount(2);
        job.Snapshot(99).Logs.Should().BeEmpty();
        job.Snapshot(-1).Logs.Should().HaveCount(7);

        // 終わった後は、同じ対象のジョブを始められる
        var next = _jobs.TryStart(JobKind.Delete, Target, "local", _ => Task.FromResult<object?>(null));
        next.Job.Should().NotBeNull();
        await next.Job!.Completion;
    }

    [Fact]
    public async Task A_Failed_Job_Should_Record_The_Error_And_The_Step()
    {
        var logger = new Mock<ILogger<JobManager>>();
        var jobs = new JobManager(new InMemoryJobStore(), _time, logger.Object);

        var beforeStep = jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "a"), "local", _ => throw new InvalidOperationException("boom")).Job!;
        var duringStep = jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "b"), "local", ctx =>
        {
            ctx.ReportStep(2, 3, "Container Apps Environment b-env");
            throw new InvalidOperationException("quota");
        }).Job!;
        await Task.WhenAll(beforeStep.Completion, duringStep.Completion);

        beforeStep.Snapshot().Should().Match<JobSnapshot>(s => s.Status == JobStatus.Failed && s.Error == "boom");
        duringStep.Snapshot().Error.Should().Be("Container Apps Environment b-env（手順 2/3）で失敗しました: quota");
        logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_Stop_Request_Should_Stop_The_Job_Before_The_Next_Step()
    {
        var firstStep = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var job = _jobs.TryStart(JobKind.Deploy, Target, "local", async ctx =>
        {
            ctx.ReportStep(1, 3, "リソースグループ rg");
            firstStep.SetResult();
            await release.Task;
            ctx.ReportStep(2, 3, "Container Apps Environment demo-env");
            return "done";
        }).Job!;
        await firstStep.Task;

        job.RequestStop().Should().BeTrue();
        job.StopRequested.Should().BeTrue();
        job.Snapshot().StopRequested.Should().BeTrue();
        job.Snapshot().Status.Should().Be(JobStatus.Running, "今の手順は止めない");
        release.SetResult();
        await job.Completion;

        var snapshot = job.Snapshot();
        snapshot.Status.Should().Be(JobStatus.Stopped);
        snapshot.CurrentStep.Should().Be(new JobStep(1, 3, "リソースグループ rg"));
        snapshot.Error.Should().Be("リソースグループ rg（手順 1/3）の後で停止しました。");
        snapshot.Result.Should().BeNull();
        snapshot.CompletedAt.Should().Be(_time.Now);
        // 終わったジョブは停止を受け付けない
        job.RequestStop().Should().BeFalse();
    }

    [Fact]
    public async Task A_Stop_Before_The_First_Step_Should_Say_So()
    {
        var release = new TaskCompletionSource();
        var job = _jobs.TryStart(JobKind.Delete, Target, "local", async ctx =>
        {
            await release.Task;
            ctx.ReportStep(1, 2, "demo-tt の削除");
            return null;
        }).Job!;

        job.RequestStop();
        release.SetResult();
        await job.Completion;

        job.Snapshot().Should().Match<JobSnapshot>(s => s.Status == JobStatus.Stopped && s.Error == "最初の手順の前に停止しました。" && s.CurrentStep == null);
    }

    [Fact]
    public async Task Jobs_Should_Be_Visible_Only_To_Their_Caller_Newest_First()
    {
        var first = _jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "a"), "alice", _ => Task.FromResult<object?>(null)).Job!;
        await first.Completion;
        _time.Now += TimeSpan.FromMinutes(1);
        var release = new TaskCompletionSource();
        var second = _jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "b"), "alice", async _ => { await release.Task; return null; }).Job!;
        var other = _jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "c"), "bob", _ => Task.FromResult<object?>(null)).Job!;

        _jobs.Get(first.Id, "alice").Should().BeSameAs(first);
        _jobs.Get(first.Id, "bob").Should().BeNull();
        _jobs.Get("job_missing", "alice").Should().BeNull();
        _jobs.List("alice").Should().Equal(second, first);
        _jobs.List("alice", JobStatus.Running).Should().Equal(second);
        _jobs.List("alice", JobStatus.Succeeded).Should().Equal(first);

        release.SetResult();
        await Task.WhenAll(second.Completion, other.Completion);
    }
}
