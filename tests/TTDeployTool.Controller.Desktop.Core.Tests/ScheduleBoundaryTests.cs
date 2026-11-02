using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// 予定の時刻の境界（オーナーの依頼、2026-10-31: 境界値は単体テストで確かめる）。予定の時刻は分まで（秒は 0）で、今より後だけを受け付ける:
/// 今と同じ時刻・今より前は誤り（「今より後にしてください」）、今より 1 分後は受け付ける。今が分の途中なら、その分の始め（秒 0）は今より前になる。
/// </summary>
public sealed class ScheduleBoundaryTests
{
    private static readonly DateTimeOffset Minute = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>今（分の始め・分の途中・分の終わり）と、入れる時刻（今の分からの分）と、受け付けるか。</summary>
    public static TheoryData<int, int, bool> Boundaries => new()
    {
        // 今が 09:00:00 ちょうど
        { 0, -1, false },
        { 0, 0, false },
        { 0, 1, true },
        // 今が 09:00:30（分の途中）
        { 30, 0, false },
        { 30, 1, true },
        // 今が 09:00:59
        { 59, 0, false },
        { 59, 1, true },
    };

    private static (FakeTimeProvider Time, ScheduleInput Input) At(int nowSeconds, int minutes)
    {
        var time = new FakeTimeProvider(Minute.AddSeconds(nowSeconds));
        var input = new ScheduleInput(time) { Date = Minute, TimeOfDay = Minute.TimeOfDay + TimeSpan.FromMinutes(minutes) };
        return (time, input);
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Schedule_Input_Should_Accept_Only_Times_After_Now(int nowSeconds, int minutes, bool accepted)
    {
        var (_, input) = At(nowSeconds, minutes);

        input.At.Should().Be(Minute.AddMinutes(minutes));
        if (accepted)
            input.Error.Should().BeNull();
        else
            input.Error.Should().Be($"予定の時刻（{Minute.AddMinutes(minutes).ToLocalTime():MM/dd HH:mm}）は、今より後にしてください。");
    }

    [Fact]
    public void Schedule_Input_Should_Cross_The_Day_With_The_Date()
    {
        // 今が 23:59:30 なら、同じ日の 23:59 は誤り、次の日の 00:00 は受け付ける
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 23, 59, 30, TimeSpan.Zero));
        var input = new ScheduleInput(time) { Date = time.GetUtcNow(), TimeOfDay = new TimeSpan(23, 59, 0) };
        input.Error.Should().NotBeNull();
        input.Date = time.GetUtcNow().AddDays(1);
        input.TimeOfDay = TimeSpan.Zero;
        input.At.Should().Be(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));
        input.Error.Should().BeNull();
    }

    /// <summary>今（UTC。テストのタイムゾーンの差は 0 とは限らないため、ローカルで比べる）と、既定の予定（今の次の分）。</summary>
    public static TheoryData<string, string> NextMinutes => new()
    {
        { "2026-10-20T09:00:00Z", "2026-10-20T09:01:00Z" },
        { "2026-10-20T09:00:01Z", "2026-10-20T09:01:00Z" },
        { "2026-10-20T09:00:59Z", "2026-10-20T09:01:00Z" },
        { "2026-10-20T23:59:30Z", "2026-10-21T00:00:00Z" },
        { "2026-12-31T23:59:59Z", "2027-01-01T00:00:00Z" },
    };

    [Theory]
    [MemberData(nameof(NextMinutes))]
    public void Schedule_Input_Should_Start_At_The_Next_Minute(string now, string expected)
    {
        // 予定の初期値は今の次の分（オーナーの依頼、2026-11-02 の案 A）。今の分は断るため、受け付ける最も早い時刻にする
        var time = new FakeTimeProvider(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        var input = new ScheduleInput(time);
        input.At.Should().Be(NextMinuteOf(time));
        input.Error.Should().BeNull("初期値はそのまま受け付ける");
        if (time.LocalTimeZone.BaseUtcOffset == TimeSpan.Zero)
            input.At.Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset NextMinuteOf(TimeProvider time)
    {
        var now = time.GetLocalNow();
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(1);
    }

    [Fact]
    public void Schedule_Input_Should_Reset_To_The_Next_Minute_Of_The_Time_Now()
    {
        var time = new FakeTimeProvider(Minute);
        var input = new ScheduleInput(time) { Date = Minute.AddDays(3), TimeOfDay = TimeSpan.FromHours(5) };
        time.Advance(TimeSpan.FromMinutes(42.5));
        input.Error.Should().BeNull("3 日後は今より後");

        input.ResetToNextMinute();
        input.At.Should().Be(NextMinuteOf(time));
        input.Error.Should().BeNull();
    }

    [Fact]
    public async Task Showing_A_Page_With_A_Schedule_Should_Reset_It_To_The_Next_Minute()
    {
        // 予定の欄のある画面を開くたびに、今の次の分に入れ直す。実行状況の画面のジョブ 1 件の「予定を変える」は、そのジョブの予定のまま
        var time = new FakeTimeProvider(Minute);
        var h = await new Harness(time: time).ReadyAsync();
        var job = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "demo"), Minute.AddHours(3), () => new TaskCompletionSource<Job>().Task);
        var inputs = new (string Page, ScheduleInput Input)[]
        {
            (Pages.Wizard, h.Main.Deployment.Schedule), (Pages.Delete, h.Main.Deletion.Schedule), (Pages.BatchDelete, h.Main.BatchDeletion.Schedule), (Pages.Jobs, h.Main.JobList.Schedule),
        };
        foreach (var (_, input) in inputs)
            (input.Date, input.TimeOfDay) = (Minute.AddDays(1), TimeSpan.FromHours(5));
        time.Advance(TimeSpan.FromMinutes(90.5));

        foreach (var (page, input) in inputs)
        {
            await h.Main.PageShownAsync(page);
            input.At.Should().Be(NextMinuteOf(time), page);
        }
        await h.Main.PageShownAsync(Pages.Status);
        job.EditScheduledAt.Should().Be(Minute.AddHours(3), "ジョブ 1 件の予定の欄は変えない");
    }

    [Fact]
    public async Task Reaching_The_Confirm_Step_Should_Reset_The_Deployment_Schedule()
    {
        // 新しくデプロイの予定の欄は ⑤ 確認と実行にある。①〜④ の入力に時間がかかっても、⑤ に進んだときの次の分にする
        var time = new FakeTimeProvider(Minute);
        var h = await new Harness(time: time).ReadyAsync();
        h.Azure.ResourceGroup = null;
        var wizard = h.Main.Wizard;
        for (var i = 0; i < 3; i++)
            await wizard.NextCommand.ExecuteAsync();
        time.Advance(TimeSpan.FromMinutes(12.5));
        h.Main.Deployment.Schedule.Error.Should().NotBeNull("開いたときの次の分は、もう過ぎている");

        await wizard.NextCommand.ExecuteAsync();
        wizard.IsConfirm.Should().BeTrue();
        h.Main.Deployment.Schedule.At.Should().Be(NextMinuteOf(time));
        h.Main.Deployment.BlockedText.Should().BeNull("予定に追加できる");
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task Rescheduling_Selected_Jobs_Should_Change_Them_Only_After_Now(int nowSeconds, int minutes, bool accepted)
    {
        var time = new FakeTimeProvider(Minute.AddSeconds(nowSeconds));
        var h = await new Harness(time: time).ReadyAsync();
        var job = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "demo"), Minute.AddHours(1), () => new TaskCompletionSource<Job>().Task);
        var list = h.Main.JobList;
        list.SetSelection([job]);
        list.Schedule.Date = Minute;
        list.Schedule.TimeOfDay = Minute.TimeOfDay + TimeSpan.FromMinutes(minutes);

        await list.RescheduleSelectedCommand.ExecuteAsync();

        if (accepted)
        {
            job.ScheduledAt.Should().Be(Minute.AddMinutes(minutes));
            list.Message.Should().Be($"1 件の予定の変更（{Minute.AddMinutes(minutes).ToLocalTime():MM/dd HH:mm}）をしました。");
        }
        else
        {
            job.ScheduledAt.Should().Be(Minute.AddHours(1), "予定は変えない");
            list.Message.Should().EndWith("は、今より後にしてください。");
        }
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task Scheduling_A_Deployment_Should_Accept_Only_Times_After_Now(int nowSeconds, int minutes, bool accepted)
    {
        var time = new FakeTimeProvider(Minute.AddSeconds(nowSeconds));
        var h = await new Harness(time: time).ReadyAsync();
        var vm = h.Main.Deployment;
        await vm.PlanCommand.ExecuteAsync();
        vm.Schedule.Date = Minute;
        vm.Schedule.TimeOfDay = Minute.TimeOfDay + TimeSpan.FromMinutes(minutes);

        // 押す前に「予定に追加」できない理由を出し、押しても登録しない
        if (accepted)
            vm.BlockedText.Should().BeNull();
        else
            vm.BlockedText.Should().StartWith("「予定に追加」: 予定の時刻（");
        await vm.ScheduleCommand.ExecuteAsync();

        if (accepted)
            h.Main.CurrentDeployment!.ScheduledAt.Should().Be(Minute.AddMinutes(minutes));
        else
        {
            vm.Error.Should().EndWith("は、今より後にしてください。");
            h.Main.Queue.HasActive.Should().BeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task Rescheduling_One_Job_Should_Change_It_Only_After_Now(int nowSeconds, int minutes, bool accepted)
    {
        // 実行状況の画面のジョブ 1 件の「予定を変える」も、ほかの画面と同じく今と同じか前の時刻は断る（オーナーの判断、2026-10-31。No.80）
        var time = new FakeTimeProvider(Minute.AddSeconds(nowSeconds));
        var h = await new Harness(time: time).ReadyAsync();
        var job = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "demo"), Minute.AddHours(1), () => new TaskCompletionSource<Job>().Task);
        job.EditDate = Minute;
        job.EditTime = Minute.TimeOfDay + TimeSpan.FromMinutes(minutes);

        await job.RescheduleCommand.ExecuteAsync();

        if (accepted)
        {
            job.ScheduledAt.Should().Be(Minute.AddMinutes(minutes));
            job.RescheduleMessage.Should().BeNull();
        }
        else
        {
            job.ScheduledAt.Should().Be(Minute.AddHours(1), "予定は変えない");
            job.State.Should().Be(DesktopJobState.Scheduled, "すぐ実行にしない");
            job.ChangedAt.Should().BeNull("変えていない");
            job.RescheduleMessage.Should().Be($"予定の時刻（{Minute.AddMinutes(minutes).ToLocalTime():MM/dd HH:mm}）は、今より後にしてください。");
        }
    }

    [Fact]
    public async Task Rescheduling_One_Job_Should_Clear_The_Message_After_A_Valid_Time()
    {
        var time = new FakeTimeProvider(Minute);
        var h = await new Harness(time: time).ReadyAsync();
        var job = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "demo"), Minute.AddHours(1), () => new TaskCompletionSource<Job>().Task);
        await job.PauseCommand.ExecuteAsync();
        job.EditDate = Minute;
        job.EditTime = Minute.TimeOfDay;
        await job.RescheduleCommand.ExecuteAsync();
        job.RescheduleMessage.Should().NotBeNull();

        job.EditTime = Minute.TimeOfDay + TimeSpan.FromMinutes(1);
        await job.RescheduleCommand.ExecuteAsync();
        job.RescheduleMessage.Should().BeNull();
        (job.State, job.ScheduledAt).Should().Be((DesktopJobState.Paused, Minute.AddMinutes(1)), "一時停止のまま予定だけ変える");
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task Scheduling_A_Deletion_Should_Accept_Only_Times_After_Now(int nowSeconds, int minutes, bool accepted)
    {
        var time = new FakeTimeProvider(Minute.AddSeconds(nowSeconds));
        var h = await new Harness(time: time).ReadyAsync();
        h.Azure.Deployed();
        var vm = h.Main.Deletion;
        await vm.PlanCommand.ExecuteAsync();
        vm.ConfirmAppName = "demo";
        vm.Schedule.Date = Minute;
        vm.Schedule.TimeOfDay = Minute.TimeOfDay + TimeSpan.FromMinutes(minutes);

        if (accepted)
            vm.BlockedText.Should().BeNull();
        else
            vm.BlockedText.Should().Be($"「予定に追加」: 予定の時刻（{Minute.AddMinutes(minutes).ToLocalTime():MM/dd HH:mm}）は、今より後にしてください。");
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task Scheduling_A_Batch_Deletion_Should_Accept_Only_Times_After_Now(int nowSeconds, int minutes, bool accepted)
    {
        var time = new FakeTimeProvider(Minute.AddSeconds(nowSeconds));
        var h = new Harness(time: time);
        h.Azure.RecordsByTenant["t1"] = [.. DeploymentListViewModelTests.Deployment("sub", "rg", "demo")];
        h.Azure.Deployed();
        await h.ReadyAsync();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        var vm = h.Main.BatchDeletion;
        await vm.OpenAsync(h.Main.Deployments.Rows.Select(r => r.Entry).ToList());
        vm.ConfirmCount = "1";
        vm.Schedule.Date = Minute;
        vm.Schedule.TimeOfDay = Minute.TimeOfDay + TimeSpan.FromMinutes(minutes);

        if (accepted)
            vm.BlockedText.Should().BeNull();
        else
            vm.BlockedText.Should().Be($"「予定に追加」: 予定の時刻（{Minute.AddMinutes(minutes).ToLocalTime():MM/dd HH:mm}）は、今より後にしてください。");
    }
}
