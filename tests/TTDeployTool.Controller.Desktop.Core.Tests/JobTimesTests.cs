using FluentAssertions;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>ジョブの一覧の日時の列（予定・登録・変更・実行開始・実行終了・処理時間）と、その並べ替え・絞り込み（docs/design/desktop-jobs.md の 6.8）。</summary>
public class JobTimesTests
{
    private readonly MovableTime _time = new();

    private static JobViewModel Pending(Harness h, string app, DateTimeOffset? at, Func<Task<Job>>? starter = null) =>
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", app), at, starter ?? (() => new TaskCompletionSource<Job>().Task));

    private static JobRecord Record(string app, DateTimeOffset registered, DateTimeOffset? changed, DateTimeOffset? started, DateTimeOffset finished,
        DateTimeOffset? scheduled = null) =>
        new(JobKind.Deploy, "sub", "rg", app, false, scheduled, finished, DesktopJobState.Succeeded, null, null, null, null, null, null, [], registered, changed, started);

    [Fact]
    public async Task Registering_Rescheduling_Starting_And_Finishing_Should_Be_Recorded()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var start = _time.Now;
        var job = Pending(h, "a", start.AddHours(1));
        (job.RegisteredAt, job.ChangedAt, job.StartedAt, job.FinishedAt).Should().Be((start, null, null, null));
        job.RegisteredText.Should().Be($"{start.ToLocalTime():MM/dd HH:mm}");
        (job.ChangedText, job.StartedText, job.FinishedText, job.DurationText).Should().Be((null, null, null, null));
        job.TimesText.Should().Be($"登録: {start.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        // 変更日時は予定の時刻を変えた日時。一時停止・再開では変えない
        _time.Now = start.AddMinutes(10);
        job.EditDate = start.AddHours(2);
        await job.RescheduleCommand.ExecuteAsync();
        job.ChangedAt.Should().Be(start.AddMinutes(10));
        _time.Now = start.AddMinutes(15);
        await job.PauseCommand.ExecuteAsync();
        await job.ResumeCommand.ExecuteAsync();
        job.ChangedAt.Should().Be(start.AddMinutes(10));
        job.ChangedText.Should().Be($"{start.AddMinutes(10).ToLocalTime():MM/dd HH:mm}");

        // 取り消したものは、実行開始日時が無く、実行終了日時は取り消した日時
        _time.Now = start.AddMinutes(20);
        await job.CancelCommand.ExecuteAsync();
        (job.StartedAt, job.FinishedAt, job.Duration).Should().Be((null, start.AddMinutes(20), null));
        job.TimesText.Should().Contain("予定の変更: ").And.Contain("実行終了: ").And.NotContain("処理時間");

        // 実行の直前の確認から実行開始日時に数える
        var failing = new TaskCompletionSource<Job>();
        var now = Pending(h, "b", null, () => failing.Task);
        now.StartedAt.Should().Be(start.AddMinutes(20));
        _time.Now = start.AddMinutes(23).AddSeconds(7);
        failing.SetException(new InvalidOperationException("失敗しました"));
        await now.Finished;
        (now.FinishedAt, now.Duration, now.DurationText).Should().Be((start.AddMinutes(23).AddSeconds(7), TimeSpan.FromSeconds(187), "3 分 7 秒"));
    }

    [Fact]
    public void Long_Durations_Should_Be_Shown_In_Hours()
    {
        var finished = _time.Now;
        var history = new InMemoryJobHistory { Records = [Record("long", finished.AddHours(-3), null, finished.AddHours(-2).AddMinutes(-5), finished)] };

        new Harness(time: _time, history: history).Main.Jobs.Single().DurationText.Should().Be("2 時間 5 分");
    }

    [Fact]
    public async Task The_Registration_And_Change_Times_Should_Survive_A_Restart()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var registered = _time.Now;
        var job = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "old"), registered.AddHours(1), () => new TaskCompletionSource<Job>().Task,
            saved: new SavedDeletion(false, false, false, "old", [], false));
        _time.Now = registered.AddMinutes(5);
        job.EditDate = registered.AddDays(1);
        await job.RescheduleCommand.ExecuteAsync();
        h.Preferences.SavedJobs.Single().Should().BeEquivalentTo(new { RegisteredAt = (DateTimeOffset?)registered, ChangedAt = (DateTimeOffset?)registered.AddMinutes(5) });

        _time.Now = registered.AddMinutes(30);
        var restarted = await new Harness(time: _time, preferences: h.Preferences).ReadyAsync();

        var restored = restarted.Main.Jobs.Single();
        (restored.RegisteredAt, restored.ChangedAt).Should().Be((registered, registered.AddMinutes(5)), "登録日時は最初に登録した日時のまま");
    }

    public static TheoryData<string> DateColumns => [nameof(JobListViewModel.ScheduledFilter), nameof(JobListViewModel.RegisteredFilter),
        nameof(JobListViewModel.ChangedFilter), nameof(JobListViewModel.StartedFilter), nameof(JobListViewModel.FinishedFilter)];

    [Theory]
    [MemberData(nameof(DateColumns))]
    public async Task Each_Date_Column_Should_Filter_By_A_Range_Of_Days(string column)
    {
        var day = (int i) => _time.Now.AddDays(-10 + (3 * i));
        var history = new InMemoryJobHistory
        {
            Records =
            [
                Record("first", day(0), day(0), day(0), day(0), day(0)),
                Record("second", day(1), day(1), day(1), day(1), day(1)),
                Record("third", day(2), day(2), day(2), day(2), day(2)),
                Record("none", day(2), null, null, day(2)),
            ],
        };
        var h = await new Harness(time: _time, history: history).ReadyAsync();
        var list = h.Main.JobList;
        var filter = (DateRangeFilter)typeof(JobListViewModel).GetProperty(column)!.GetValue(list)!;
        var local = (DateTimeOffset at) => new DateTimeOffset(at.ToLocalTime().Date);
        var names = () => list.Items.Select(j => j.Target.AppName).Order().ToList();
        filter.IsActive.Should().BeFalse();
        filter.ClearCommand.CanExecute(null).Should().BeFalse();

        filter.From = local(day(1));
        filter.ClearCommand.CanExecute(null).Should().BeTrue();
        names().Should().Equal(column is nameof(JobListViewModel.RegisteredFilter) or nameof(JobListViewModel.FinishedFilter)
            ? ["none", "second", "third"]
            : ["second", "third"], "その日を含む。日時の無い行は出さない");
        filter.To = local(day(1));
        names().Should().Equal("second");
        filter.From = null;
        names().Should().Equal("first", "second");
        list.CountText.Should().StartWith("絞り込んだ 2 件 / 全 4 件");

        await filter.ClearCommand.ExecuteAsync();
        (filter.From, filter.To, filter.IsActive).Should().Be((null, null, false));
        list.Items.Should().HaveCount(4);
        filter.From = null;
        list.Items.Should().HaveCount(4, "同じ値なら変えない");
    }

    [Fact]
    public void A_Filter_Without_A_Table_Should_Still_Match()
    {
        var filter = new DateRangeFilter { From = _time.Now, To = _time.Now };

        (filter.IsActive, filter.Matches(_time.Now), filter.Matches(_time.Now.AddDays(2)), filter.Matches(null)).Should().Be((true, true, false, false));
    }

    [Fact]
    public async Task Each_Date_Column_And_The_Duration_Should_Sort()
    {
        var t = _time.Now;
        var history = new InMemoryJobHistory
        {
            Records =
            [
                Record("a", t.AddDays(-3), t.AddDays(-1), t.AddHours(-5), t.AddHours(-1), t.AddDays(-2)),
                Record("b", t.AddDays(-2), null, t.AddHours(-6), t.AddHours(-5).AddMinutes(-30)),
                Record("c", t.AddDays(-4), t.AddDays(-2), null, t.AddHours(-3), t.AddDays(-1)),
            ],
        };
        var h = await new Harness(time: _time, history: history).ReadyAsync();
        var list = h.Main.JobList;
        var order = () => string.Concat(list.Items.Select(j => j.Target.AppName));

        list.SortBy(JobSortKey.RegisteredAt);
        (order(), list.RegisteredHeader).Should().Be(("cab", "登録日時 ▲"));
        list.SortBy(JobSortKey.ChangedAt);
        (order(), list.ChangedHeader).Should().Be(("cab", "変更日時 ▲"), "値の無いものは最後");
        list.SortBy(JobSortKey.StartedAt);
        (order(), list.StartedHeader).Should().Be(("bac", "実行開始日時 ▲"));
        list.SortBy(JobSortKey.Finished);
        (order(), list.FinishedHeader).Should().Be(("bca", "実行終了日時 ▲"));
        list.SortBy("Duration");
        (order(), list.DurationHeader).Should().Be(("bac", "処理時間 ▲"));
        list.SortBy(JobSortKey.Duration);
        (order(), list.DurationHeader, list.FinishedHeader).Should().Be(("abc", "処理時間 ▼", "実行終了日時"));
        list.SortBy(JobSortKey.Time);
        order().Should().Be("acb");
    }
}
