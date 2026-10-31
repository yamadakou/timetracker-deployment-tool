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
}
