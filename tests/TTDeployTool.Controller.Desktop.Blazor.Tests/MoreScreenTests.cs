using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using TTDeployTool.Controller.Desktop.Blazor.Components;
using TTDeployTool.Controller.Desktop.Blazor.Screens;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>一覧のまとめての操作・ジョブの予定・期限を過ぎた行・実行中のジョブ・計画できない削除とデプロイ・入力欄の端の場合。</summary>
public sealed class MoreScreenTests
{
    private static async Task<TestApp> SignedInAsync(Func<FixtureData, FixtureData>? data = null, bool secrets = false)
    {
        var app = new TestApp(data: data, secrets: secrets);
        await app.SignInAsync();
        app.ViewModel.Deployments.PageSize = 50;
        return app;
    }

    private static async Task ScheduleDeletionAsync(TestApp app, string appName)
    {
        var list = app.ViewModel.Deployments;
        list.SetSelection([list.Rows.First(r => r.AppName == appName)]);
        await list.DeleteSelectedCommand.ExecuteAsync();
        await app.ViewModel.Deletion.PlanCommand.ExecuteAsync();
        app.ViewModel.Deletion.ConfirmAppName = appName;
        app.ViewModel.Deletion.Schedule.Date = TestApp.Now.AddHours(1).ToLocalTime();
        await app.ViewModel.Deletion.ScheduleCommand.ExecuteAsync();
    }

    [Fact]
    public async Task Selected_Deployments_Should_Change_Their_Expiry_And_Group_Together()
    {
        using var app = await SignedInAsync();
        var list = app.ViewModel.Deployments;
        var page = app.Context.Render<DeploymentsPage>(p => p.Add(x => x.Model, app.ViewModel));
        page.FindAll("tbody tr")[0].Click();
        page.FindAll("tbody tr")[1].Click();

        page.Find("[data-id='Deployments.BatchNoExpiry']").Change(true);
        list.BatchNoExpiry.Should().BeTrue();
        page.Find("[data-id='Deployments.BatchExpiryDate']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-id='Deployments.BatchNoExpiry']").Change(false);
        page.Find("[data-id='Deployments.BatchExpiryDate']").Change(TestApp.Now.AddDays(20).ToString("yyyy-MM-dd"));
        list.BatchExpiryDate.Date.Should().Be(TestApp.Now.AddDays(20).Date);
        page.Find("[data-id='Deployments.ChangeExpiry']").Click();
        page.WaitForAssertion(() => page.Find("[data-id='Deployments.BatchMessage']").TextContent.Should().NotBeEmpty());

        page.Find("[data-id='Deployments.BatchGroup']").Input("営");
        page.FindAll("#tt-batch-group-suggestions option").Select(o => o.GetAttribute("value")).Should().Equal("営業");
        page.Find("[data-id='Deployments.BatchGroup']").Input("営業");
        page.Find("[data-id='Deployments.ChangeGroup']").Click();
        page.WaitForAssertion(() => app.Dialogs.Pending.Should().NotBeNull());
        await page.InvokeAsync(() => app.Dialogs.Answer(true));
        for (var i = 0; i < 100 && (list.BatchBusy.IsBusy || list.BatchMessage == null); i++)
            await Task.Delay(50);
        app.Dialogs.Pending.Should().BeNull();
        list.BatchMessage.Should().NotBeNull();
    }

    [Fact]
    public async Task Selected_Jobs_Should_Get_A_New_Schedule()
    {
        using var app = await SignedInAsync();
        await ScheduleDeletionAsync(app, "app00");
        var jobs = app.ViewModel.JobList;
        var page = app.Context.Render<JobsPage>(p => p.Add(x => x.Model, app.ViewModel));
        page.FindAll("[data-id='Jobs.SignInNote']").Should().BeEmpty();
        var job = jobs.Items.First(j => !j.IsRecord);
        page.FindAll("tbody tr")[jobs.Items.IndexOf(job)].Click();
        var at = TestApp.Now.AddHours(6).ToLocalTime();
        page.Find("[data-id='Jobs.ScheduleDate']").Change(at.ToString("yyyy-MM-dd"));
        page.Find("[data-id='Jobs.ScheduleTime']").Change(at.ToString("HH:mm"));
        jobs.Schedule.TimeOfDay.Should().Be(new TimeSpan(at.Hour, at.Minute, 0));
        page.Find("[data-id='Jobs.RescheduleSelected']").Click();
        page.WaitForAssertion(() => job.ScheduledAt!.Value.ToLocalTime().Hour.Should().Be(at.Hour));
        page.Find("[data-id='Jobs.Message']").TextContent.Should().NotBeNull();
    }

    [Fact]
    public async Task An_Expired_Deployment_Should_Be_Shown_In_The_Critical_Color()
    {
        using var app = await SignedInAsync(d => FixtureSamples.Data(45, TestApp.Now, expired: 1) with { WriteDelaySeconds = 0 });
        var page = app.Context.Render<DeploymentsPage>(p => p.Add(x => x.Model, app.ViewModel));
        page.FindAll("td[data-key='ExpiresAt'] .tt-critical").Should().ContainSingle();
        page.FindAll("td[data-key='ExpiresAt'] .tt-caution").Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_Running_Job_Should_Show_Its_Progress_Until_It_Is_Canceled()
    {
        using var app = await SignedInAsync(d => d with { WriteDelaySeconds = 3 });
        var list = app.ViewModel.Deployments;
        list.SetSelection([list.Rows.First(r => r.AppName == "app00")]);
        await list.DeleteSelectedCommand.ExecuteAsync();
        await app.ViewModel.Deletion.PlanCommand.ExecuteAsync();
        app.ViewModel.Deletion.ConfirmAppName = "app00";
        await app.ViewModel.Deletion.StartCommand.ExecuteAsync();
        var job = app.ViewModel.Jobs.Single(j => !j.IsRecord);

        var row = app.Context.Render<JobView>(p => p.Add(x => x.Job, job));
        row.WaitForAssertion(() => row.FindAll("[data-id='Job.Progress']").Should().ContainSingle());
        var summary = app.Context.Render<JobView>(p => p.Add(x => x.Job, job).Add(x => x.Mode, JobViewMode.Summary));
        summary.FindAll(".tt-progress").Should().ContainSingle();
        row.Find("[data-id='Job.Cancel']").Click();
        row.WaitForAssertion(() => row.FindAll("[data-id='Job.Progress']").Should().BeEmpty(), TimeSpan.FromSeconds(20));
        await app.ViewModel.Queue.WhenIdleAsync();
        job.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task A_Deletion_Without_Targets_Should_Not_Ask_For_The_App_Name()
    {
        using var app = await SignedInAsync();
        app.ViewModel.Target.SelectedSubscription = app.ViewModel.Target.Subscriptions.First(s => s.SubscriptionId == FixtureSamples.Subscription(0));
        app.ViewModel.Target.AppName = "nothing";
        app.ViewModel.Target.ResourceGroup = FixtureSamples.ResourceGroup(0);
        var page = app.Context.Render<DeletePage>(p => p.Add(x => x.Model, app.ViewModel));
        page.Find("[data-id='Delete.Plan']").Click();
        page.WaitForAssertion(() => app.ViewModel.Deletion.HasPlan.Should().BeTrue());
        page.FindAll("[data-id='Delete.ConfirmAppName']").Should().BeEmpty("削除するものが無ければ、アプリ名を求めない");
    }

    [Fact]
    public async Task A_Plan_With_Blockers_Should_Have_No_Expiry_And_No_Saved_Schedule_Without_Encryption()
    {
        using var app = await SignedInAsync();
        var page = app.Context.Render<WizardPage>(p => p.Add(x => x.Model, app.ViewModel));
        var subscriptions = app.ViewModel.Target.Subscriptions.ToList();
        page.Find("[data-id='Target.Subscription']").Change($"{subscriptions.FindIndex(s => s.SubscriptionId == FixtureSamples.Subscription(0))}");
        // 今の Environment と違うリージョン（計画はできるが、実行できない）
        page.Find("[data-id='Target.ResourceGroup']").Input(FixtureSamples.ResourceGroup(0));
        page.Find("[data-id='Target.AppName']").Input(FixtureSamples.AppName(0));
        page.Find("[data-id='Target.Location']").Input("westus");
        app.ViewModel.Settings.DbPassword = "Wizard-Db-Pass1!";
        app.ViewModel.Settings.TrackerPassword = "Wizard-Tt-Pass1!";
        foreach (var step in new[] { WizardStep.Db, WizardStep.Passwords, WizardStep.Spec, WizardStep.Confirm })
        {
            page.Find("[data-id='Wizard.Next']").Click();
            page.WaitForAssertion(() => app.ViewModel.Wizard.Step.Should().Be(step));
        }
        page.WaitForAssertion(() => app.ViewModel.Deployment.HasPlan.Should().BeTrue());
        page.Find("[data-id='Deploy.Blockers']").TextContent.Should().NotBeEmpty();
        app.ViewModel.Deployment.ExpiresAt.Should().BeNull();
        page.FindAll("[data-id='Deploy.KeepSchedule']").Should().BeEmpty("暗号化できなければ、予定を残す選択を出さない");
    }

    [Fact]
    public void Text_Input_Should_Treat_A_Missing_Value_As_Empty()
    {
        using var app = new TestApp();
        string? value = null;
        var input = app.Context.Render<TextInput>(p => p.Add(x => x.Id, "x").Add(x => x.Value, "a").Add(x => x.ValueChanged, EventCallback.Factory.Create<string>(this, v => value = v)));
        input.Find("input").Input(new ChangeEventArgs { Value = null });
        value.Should().BeEmpty();
    }
}
