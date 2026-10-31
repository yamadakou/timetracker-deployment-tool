using Bunit;
using FluentAssertions;
using TTDeployTool.Controller.Desktop.Blazor.Screens;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>
/// 予定の時刻の境界を、画面の欄から入れて確かめる（オーナーの依頼、2026-10-31。Core の境界は ScheduleBoundaryTests）。
/// 欄の値は、ブラウザー（Blazor の JavaScript）と同じく秒の付いた形（"03:01:00"）で入れる。今は TestApp の時刻（2026-10-30 03:00:00 UTC）に、秒を足したもの。
/// </summary>
public sealed class ScheduleBoundaryScreenTests
{
    /// <summary>今の秒・入れる時刻（今の分からの分）・受け付けるか。</summary>
    public static TheoryData<int, int, bool> Boundaries => new()
    {
        { 0, -1, false },
        { 0, 0, false },
        { 0, 1, true },
        { 30, 0, false },
        { 30, 1, true },
    };

    private static string Day(TestApp app) => app.Time.GetUtcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string TimeText(int minutes) => TestApp.Now.AddMinutes(minutes).ToString("HH:mm:00", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<TestApp> SignedInAsync(int nowSeconds)
    {
        var app = new TestApp();
        await app.SignInAsync();
        app.ViewModel.Deployments.PageSize = 50;
        app.Time.Advance(TimeSpan.FromSeconds(nowSeconds));
        return app;
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task Rescheduling_From_The_Job_List_Should_Accept_Only_Times_After_Now(int nowSeconds, int minutes, bool accepted)
    {
        using var app = await SignedInAsync(nowSeconds);
        var list = app.ViewModel.Deployments;
        list.SetSelection([list.Rows.First(r => r.AppName == "app00")]);
        await list.DeleteSelectedCommand.ExecuteAsync();
        await app.ViewModel.Deletion.PlanCommand.ExecuteAsync();
        app.ViewModel.Deletion.ConfirmAppName = "app00";
        await app.ViewModel.Deletion.ScheduleCommand.ExecuteAsync();
        var job = app.ViewModel.Jobs.Single(j => !j.IsRecord);
        var before = job.ScheduledAt;

        var page = app.Context.Render<JobsPage>(p => p.Add(x => x.Model, app.ViewModel));
        page.FindAll("tbody tr")[app.ViewModel.JobList.Items.IndexOf(job)].Click();
        page.Find("[data-id='Jobs.ScheduleDate']").Change(Day(app));
        page.Find("[data-id='Jobs.ScheduleTime']").Change(TimeText(minutes));
        page.Find("[data-id='Jobs.RescheduleSelected']").Click();

        var message = page.Find("[data-id='Jobs.Message']").TextContent;
        if (accepted)
        {
            job.ScheduledAt.Should().Be(TestApp.Now.AddMinutes(minutes));
            message.Should().StartWith("1 件の予定の変更");
        }
        else
        {
            job.ScheduledAt.Should().Be(before, "予定は変えない");
            message.Should().EndWith("は、今より後にしてください。");
        }
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task Scheduling_A_Deletion_Should_Accept_Only_Times_After_Now(int nowSeconds, int minutes, bool accepted)
    {
        using var app = await SignedInAsync(nowSeconds);
        var list = app.ViewModel.Deployments;
        list.SetSelection([list.Rows.First(r => r.AppName == FixtureSamples.AppName(2))]);
        await list.DeleteSelectedCommand.ExecuteAsync();
        await app.ViewModel.Deletion.PlanCommand.ExecuteAsync();
        var page = app.Context.Render<DeletePage>(p => p.Add(x => x.Model, app.ViewModel));
        page.Find("[data-id='Delete.ConfirmAppName']").Input(FixtureSamples.AppName(2));
        page.Find("[data-id='Delete.ScheduleDate']").Change(Day(app));
        page.Find("[data-id='Delete.ScheduleTime']").Change(TimeText(minutes));

        // 押す前に、予定に追加できない理由を出す
        if (accepted)
            page.FindAll("[data-id='Delete.Blocked']").Should().BeEmpty();
        else
            page.Find("[data-id='Delete.Blocked']").TextContent.Should().StartWith("「予定に追加」: 予定の時刻（");
        page.Find("[data-id='Delete.Schedule']").Click();

        if (accepted)
            app.ViewModel.Jobs.Single(j => !j.IsRecord).ScheduledAt.Should().Be(TestApp.Now.AddMinutes(minutes));
        else
        {
            page.Find("[data-id='Delete.Error']").TextContent.Should().EndWith("は、今より後にしてください。");
            app.ViewModel.Jobs.Should().OnlyContain(j => j.IsRecord, "登録しない");
        }
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task Rescheduling_One_Job_Should_Accept_Only_Times_After_Now(int nowSeconds, int minutes, bool accepted)
    {
        using var app = await SignedInAsync(nowSeconds);
        var list = app.ViewModel.Deployments;
        list.SetSelection([list.Rows.First(r => r.AppName == "app00")]);
        await list.DeleteSelectedCommand.ExecuteAsync();
        await app.ViewModel.Deletion.PlanCommand.ExecuteAsync();
        app.ViewModel.Deletion.ConfirmAppName = "app00";
        await app.ViewModel.Deletion.ScheduleCommand.ExecuteAsync();
        var job = app.ViewModel.Jobs.Single(j => !j.IsRecord);
        var before = job.ScheduledAt;

        // 実行状況の画面のジョブの表示（JobView）
        var view = app.Context.Render<Components.JobView>(p => p.Add(x => x.Job, job));
        view.Find("[data-id='Job.EditDate']").Change(Day(app));
        view.Find("[data-id='Job.EditTime']").Change(TimeText(minutes));
        view.Find("[data-id='Job.Reschedule']").Click();

        if (accepted)
        {
            job.ScheduledAt.Should().Be(TestApp.Now.AddMinutes(minutes));
            view.FindAll("[data-id='Job.RescheduleMessage']").Should().BeEmpty();
        }
        else
        {
            job.ScheduledAt.Should().Be(before, "予定は変えない");
            view.Find("[data-id='Job.RescheduleMessage']").TextContent.Should().EndWith("は、今より後にしてください。");
        }
    }
}
