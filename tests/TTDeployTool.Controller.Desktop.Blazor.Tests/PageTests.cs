using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Controller.Desktop.Blazor.Components;
using TTDeployTool.Controller.Desktop.Blazor.Screens;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>メニューに無い画面（削除・まとめての削除・実行状況）と、Dry-run・設定・一覧の 1 行の詳細（WinUI の画面と同じ操作が Core に届くこと）。</summary>
public sealed class PageTests : IDisposable
{
    // app01 は Timetracker の URL が無い（止めた・作りかけのデプロイ）
    private readonly TestApp _app = new(secrets: true, data: d => d with
    {
        Resources = [.. d.Resources.Select(r => r.Name == ResourceNaming.TimetrackerApp(FixtureSamples.AppName(1)) ? r with { App = r.App! with { Fqdn = null } } : r)],
    }, history: records => [.. records.Select((r, i) => i == 0 ? r with { Endpoint = "https://recorded.example.com/" } : r)]);

    private readonly List<string> _back = [];

    public void Dispose() => _app.Dispose();

    private DeploymentListViewModel List => _app.ViewModel.Deployments;

    private static IElement Id<T>(IRenderedComponent<T> page, string id) where T : Microsoft.AspNetCore.Components.IComponent => page.Find($"[data-id='{id}']");

    private async Task SelectAsync(params int[] rows)
    {
        await _app.SignInAsync();
        List.PageSize = 50;
        List.SetSelection(rows.Select(i => List.Rows.First(r => r.AppName == FixtureSamples.AppName(i))));
    }

    private Microsoft.AspNetCore.Components.EventCallback<string> Back => Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(this, _back.Add);

    // 戻る（Back）のある画面は、戻った先を残す
    private IRenderedComponent<T> Render<T>() where T : Microsoft.AspNetCore.Components.IComponent => (IRenderedComponent<T>)(object)(typeof(T).Name switch
    {
        nameof(DeletePage) => _app.Context.Render<DeletePage>(p => p.Add(x => x.Model, _app.ViewModel).Add(x => x.Back, Back)),
        nameof(BatchDeletePage) => _app.Context.Render<BatchDeletePage>(p => p.Add(x => x.Model, _app.ViewModel).Add(x => x.Back, Back)),
        nameof(StatusPage) => _app.Context.Render<StatusPage>(p => p.Add(x => x.Model, _app.ViewModel).Add(x => x.Back, Back)),
        nameof(DryRunPage) => _app.Context.Render<DryRunPage>(p => p.Add(x => x.Model, _app.ViewModel)),
        _ => (object)_app.Context.Render<SettingsPage>(p => p.Add(x => x.Model, _app.ViewModel)),
    });

    [Fact]
    public async Task Delete_Should_Plan_Ask_For_The_App_Name_And_Run_Or_Schedule()
    {
        await SelectAsync(0);
        await List.DeleteSelectedCommand.ExecuteAsync();
        var page = Render<DeletePage>();
        page.WaitForAssertion(() => Id(page, "Delete.Targets").TextContent.Should().Contain("app00"));
        Id(page, "Delete.Target").TextContent.Should().Be(_app.ViewModel.Target.SubscriptionDisplay);
        var deletion = _app.ViewModel.Deletion;
        foreach (var (id, read) in new (string, Func<bool>)[] { ("Delete.IncludeUntagged", () => deletion.IncludeUntagged), ("Delete.KeepDbData", () => deletion.KeepDbData), ("Delete.DeleteResourceGroup", () => deletion.DeleteResourceGroup) })
        {
            Id(page, id).Change(true);
            read().Should().BeTrue(id);
        }
        Id(page, "Delete.Plan").Click();
        page.WaitForAssertion(() => deletion.NeedsAppNameConfirmation.Should().BeTrue());
        Id(page, "Delete.CopyPlan").Click();
        Id(page, "Delete.CopyAppName").Click();
        _app.Clipboard.Texts.Should().HaveCount(2).And.HaveElementAt(1, "app00");
        Id(page, "Delete.Start").HasAttribute("disabled").Should().BeTrue("アプリ名を入れるまで押せない");
        Id(page, "Delete.Blocked").TextContent.Should().NotBeEmpty();
        Id(page, "Delete.ConfirmAppName").Input("app00");
        var at = TestApp.Now.AddHours(3).ToLocalTime();
        Id(page, "Delete.ScheduleDate").Change(at.ToString("yyyy-MM-dd"));
        Id(page, "Delete.ScheduleTime").Change(at.ToString("HH:mm:00"));
        Id(page, "Delete.ScheduleNote").TextContent.Should().Be(SystemStatusViewModel.DeletionScheduleNote);
        Id(page, "Delete.Schedule").Click();
        page.WaitForAssertion(() => _app.ViewModel.Queue.HasActive.Should().BeTrue());

        Id(page, "Delete.BackToList").Click();
        _back.Should().Equal(Pages.Deployments);
    }

    [Fact]
    public async Task Delete_Should_Show_The_Error_Without_A_Plan()
    {
        await _app.SignInAsync();
        _app.ViewModel.Target.AppName = "missing";
        _app.ViewModel.Target.ResourceGroup = "rg-missing";
        var page = Render<DeletePage>();
        Id(page, "Delete.Plan").Click();
        page.WaitForAssertion(() => _app.ViewModel.Deletion.Busy.IsBusy.Should().BeFalse());
        page.FindAll("[data-id='Delete.Targets']").Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_Should_Run_Now_And_Open_The_Status()
    {
        await SelectAsync(2);
        await List.DeleteSelectedCommand.ExecuteAsync();
        var page = Render<DeletePage>();
        page.WaitForAssertion(() => _app.ViewModel.Deletion.NeedsAppNameConfirmation.Should().BeTrue());
        Id(page, "Delete.ConfirmAppName").Input("app02");
        Id(page, "Delete.Start").Click();
        page.WaitForAssertion(() => _app.ViewModel.StatusJob.Should().NotBeNull());
    }

    [Fact]
    public async Task Batch_Delete_Should_Exclude_Busy_Deployments_And_Ask_For_The_Count()
    {
        // app00 に予定の削除を入れておき、まとめての削除からは外す
        await SelectAsync(0);
        await List.DeleteSelectedCommand.ExecuteAsync();
        await _app.ViewModel.Deletion.PlanCommand.ExecuteAsync();
        _app.ViewModel.Deletion.ConfirmAppName = "app00";
        _app.ViewModel.Deletion.Schedule.Date = TestApp.Now.AddHours(1).ToLocalTime();
        await _app.ViewModel.Deletion.ScheduleCommand.ExecuteAsync();
        List.SetSelection([.. List.Rows.Where(r => r.AppName is "app00" or "app02" or "app03")]);
        await List.DeleteSelectedCommand.ExecuteAsync();

        var page = Render<BatchDeletePage>();
        var batch = _app.ViewModel.BatchDeletion;
        page.WaitForAssertion(() => Id(page, "BatchDelete.Items").QuerySelectorAll("li").Should().HaveCount(3));
        Id(page, "BatchDelete.Items").QuerySelectorAll(".tt-caution").Should().ContainSingle("ジョブのある app00 は外す");
        Id(page, "BatchDelete.KeepDbData").Change(true);
        batch.KeepDbData.Should().BeTrue();
        Id(page, "BatchDelete.DeleteResourceGroup").Change(true);
        batch.DeleteResourceGroup.Should().BeTrue();
        Id(page, "BatchDelete.Plan").Click();
        page.WaitForAssertion(() => Id(page, "BatchDelete.ConfirmText").TextContent.Should().Contain("2"));
        Id(page, "BatchDelete.Blocked").TextContent.Should().NotBeEmpty();
        Id(page, "BatchDelete.ConfirmCount").Input("2");
        var at = TestApp.Now.AddHours(4).ToLocalTime();
        Id(page, "BatchDelete.ScheduleDate").Change(at.ToString("yyyy-MM-dd"));
        Id(page, "BatchDelete.ScheduleTime").Change(at.ToString("HH:mm"));
        Id(page, "BatchDelete.ScheduleNote").TextContent.Should().NotBeEmpty();
        Id(page, "BatchDelete.Schedule").Click();
        page.WaitForAssertion(() => _app.ViewModel.JobList.CountText.Should().Contain("全 33 件"));
        Id(page, "BatchDelete.BackToList").Click();
        _back.Should().Equal(Pages.Deployments);

        // 今すぐ（残った app05）
        List.SetSelection([.. List.Rows.Where(r => r.AppName is "app05" or "app06")]);
        await List.DeleteSelectedCommand.ExecuteAsync();
        page.WaitForAssertion(() => batch.Items.Should().HaveCount(2));
        page.WaitForAssertion(() => batch.Busy.IsBusy.Should().BeFalse());
        Id(page, "BatchDelete.ConfirmCount").Input("2");
        Id(page, "BatchDelete.Start").Click();
        page.WaitForAssertion(() => _app.ViewModel.JobList.CountText.Should().Contain("全 35 件"));
    }

    [Fact]
    public async Task Status_Should_Show_The_Job_And_The_Apps_In_Azure()
    {
        var empty = Render<StatusPage>();
        empty.FindAll("[data-id='Status.Job']").Should().BeEmpty("ジョブを開いていなければ出さない");

        await SelectAsync(0);
        await List.Current!.CheckStatusCommand.ExecuteAsync();
        var page = Render<StatusPage>();
        page.WaitForAssertion(() => page.FindAll(".tt-apps li").Should().HaveCount(3));
        page.Find("a[href^='https://']").TextContent.Should().Contain("app00-tt");
        Id(page, "Status.Summary").TextContent.Should().NotBeEmpty();
        Id(page, "Status.Refresh").Click();
        page.WaitForAssertion(() => _app.ViewModel.Status.Busy.IsBusy.Should().BeFalse());
        Id(page, "Status.Copy").Click();
        page.WaitForAssertion(() => Id(page, "Status.Message").TextContent.Should().NotBeEmpty());
        Id(page, "Status.BackToList").Click();
        _back.Should().Equal(_app.ViewModel.ReturnPage);

        // 記録のジョブ（終わったもの: 操作のボタンを出さない）
        await _app.ViewModel.OpenJobAsync(_app.ViewModel.JobList.Items[0]);
        page.WaitForAssertion(() => Id(page, "Job.Title").TextContent.Should().Be(_app.ViewModel.JobList.Items[0].Title));
        page.FindAll("[data-id='Job.Cancel']").Should().BeEmpty();
        page.FindAll("[data-id='Job.Progress']").Should().BeEmpty();
    }

    [Fact]
    public async Task Status_Should_Say_When_Azure_Has_Nothing()
    {
        await _app.SignInAsync();
        _app.ViewModel.Target.AppName = "missing";
        var page = Render<StatusPage>();
        Id(page, "Status.Refresh").Click();
        page.WaitForAssertion(() => _app.ViewModel.Status.Busy.IsBusy.Should().BeFalse());
        page.FindAll("a[href^='https://']").Should().BeEmpty();
    }

    [Fact]
    public async Task An_Active_Job_Should_Show_Its_Operations_And_Reschedule()
    {
        await SelectAsync(0);
        await List.DeleteSelectedCommand.ExecuteAsync();
        await _app.ViewModel.Deletion.PlanCommand.ExecuteAsync();
        _app.ViewModel.Deletion.ConfirmAppName = "app00";
        _app.ViewModel.Deletion.Schedule.Date = TestApp.Now.AddHours(1).ToLocalTime();
        await _app.ViewModel.Deletion.ScheduleCommand.ExecuteAsync();
        var job = _app.ViewModel.Jobs.Single(j => !j.IsRecord);

        var view = _app.Context.Render<JobView>(p => p.Add(x => x.Job, job));
        Id(view, "Job.Status").TextContent.Should().StartWith("予定");
        var at = TestApp.Now.AddHours(5).ToLocalTime();
        Id(view, "Job.EditDate").Change(at.ToString("yyyy-MM-dd"));
        Id(view, "Job.EditTime").Change(at.ToString("HH:mm"));
        Id(view, "Job.Reschedule").Click();
        view.WaitForAssertion(() => job.ScheduledAt!.Value.Hour.Should().Be(at.ToUniversalTime().Hour));
        Id(view, "Job.Copy").Click();
        view.WaitForAssertion(() => _app.Clipboard.Texts.Should().ContainSingle());
        Id(view, "Job.Pause").Click();
        view.WaitForAssertion(() => Id(view, "Job.Status").TextContent.Should().Be("一時停止"));
        Id(view, "Job.Resume").Click();
        view.WaitForAssertion(() => Id(view, "Job.Status").TextContent.Should().StartWith("予定"));
        Id(view, "Job.Stop").HasAttribute("disabled").Should().BeTrue("始まっていないジョブは停止できない");
        Id(view, "Job.Cancel").Click();
        view.WaitForAssertion(() => view.FindAll("[data-id='Job.Cancel']").Should().BeEmpty());
    }

    [Fact]
    public void A_Recorded_Job_Should_Show_Its_Endpoint_In_Both_Forms()
    {
        var record = _app.ViewModel.Jobs.First(j => j.Endpoint != null);
        var row = _app.Context.Render<JobView>(p => p.Add(x => x.Job, record).Add(x => x.Mode, JobViewMode.Row));
        row.Find("a").GetAttribute("href").Should().Be(record.Endpoint!.ToString());
        var summary = _app.Context.Render<JobView>(p => p.Add(x => x.Job, record).Add(x => x.Mode, JobViewMode.Summary));
        Id(summary, "WizardJob.Endpoint").TextContent.Should().Be(record.Endpoint.ToString());
        summary.FindAll(".tt-progress").Should().BeEmpty();
    }

    [Fact]
    public async Task Detail_Should_Show_Passwords_And_Hide_A_Missing_Url()
    {
        await SelectAsync(0);
        var row = List.Current!;
        var detail = _app.Context.Render<DeploymentDetail>(p => p.Add(x => x.Row, row));
        Id(detail, "Row.Url").TextContent.Should().Contain("app00-tt");
        Id(detail, "Row.Url").Click();
        detail.FindAll("[data-id='Row.DbPassword']").Should().BeEmpty();
        Id(detail, "Row.ShowPasswords").Click();
        detail.WaitForAssertion(() => Id(detail, "Row.DbPassword").TextContent.Should().Be("••••••••"));
        Id(detail, "Row.TogglePasswords").Click();
        detail.WaitForAssertion(() => Id(detail, "Row.DbPassword").TextContent.Should().Be("Db-Pass-00"));
        Id(detail, "Row.TrackerPassword").TextContent.Should().Be("App-Pass-00");
        Id(detail, "Row.CopyDbPassword").Click();
        Id(detail, "Row.CopyTrackerPassword").Click();
        _app.Clipboard.Texts.Should().Equal("Db-Pass-00", "App-Pass-00");
        Id(detail, "Row.UseAsTarget").Click();
        detail.WaitForAssertion(() => _app.ViewModel.Target.AppName.Should().Be("app00"));

        List.SetSelection([List.Rows.First(r => r.AppName == "app01")]);
        detail.Render(p => p.Add(x => x.Row, List.Current!));
        detail.FindAll("[data-id='Row.Url']").Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_Run_Should_Refuse_Bad_Settings_And_Save_To_The_Chosen_Folder()
    {
        var page = Render<DryRunPage>();
        Id(page, "DryRun.Save").Click();
        page.WaitForAssertion(() => Id(page, "DryRun.Error").TextContent.Should().Contain("設定に誤りがあるため、ファイルを保存しませんでした"));
        page.FindAll("[data-id='DryRun.Compose']").Should().BeEmpty();

        Id(page, "DryRun.DbType").Change("1");
        Id(page, "DryRun.DbName").Input("timetracker");
        Id(page, "DryRun.TtTag").Input("");
        Id(page, "DryRun.DbPassword").Input("UiTestDbPass1!");
        Id(page, "DryRun.TrackerPassword").Input("UiTestTracker1!");
        _app.Folder = Path.Combine(_app.Directory, "dryrun");
        Directory.CreateDirectory(_app.Folder);
        Id(page, "DryRun.Save").Click();
        page.WaitForAssertion(() => Id(page, "DryRun.Compose").TextContent.Should().Contain("11.0.1-linux-mssql"));
        File.ReadAllText(Path.Combine(_app.Folder, ".env")).Should().Contain("UiTestDbPass1!");
        Id(page, "DryRun.NextSteps").TextContent.Should().Contain("docker compose up -d");
        page.FindAll("[data-id='DryRun.Error']").Should().BeEmpty("保存できたら、前のエラーを消す");
        Id(page, "DryRun.OpenFolder").Click();
        await Task.CompletedTask;
        FixtureEvents.Read(Path.Combine(_app.Directory, FixtureEvents.FileName)).Should().Contain(e => e.Kind == FixtureEvents.OpenFolder);
    }

    [Fact]
    public async Task Settings_Should_Change_The_Concurrency_And_Reach_Windows()
    {
        await _app.ViewModel.StartAsync(atStartup: false);
        var page = Render<SettingsPage>();
        page.FindAll("[data-id='Settings.Concurrency'] option").Select(o => o.TextContent).Should().Equal("1", "2", "3", "4");
        Id(page, "Settings.Concurrency").Change("2");
        _app.ViewModel.Queue.MaxConcurrency.Should().Be(3);
        Id(page, "Settings.LastStartup").TextContent.Should().StartWith("Windows のサインイン時に起動したことは、まだありません");
        Id(page, "Settings.CloseBehavior").TextContent.Should().StartWith("今ウィンドウを閉じると、アプリを終了します");
        Id(page, "Settings.ScheduledJobs").TextContent.Should().Be("まだ始めていないジョブ（予定・待機中・一時停止）はありません。");
        Id(page, "Settings.NotificationStatus").TextContent.Should().NotBeEmpty();
        Id(page, "Settings.RefreshSystem").Click();
        Id(page, "Settings.StartupToggle").Click();
        page.WaitForAssertion(() => FixtureEvents.Read(Path.Combine(_app.Directory, FixtureEvents.FileName))
            .Should().Contain((FixtureEvents.StartupTask, "Enabled")));
        Id(page, "Settings.OpenStartupApps").Click();
        Id(page, "Settings.OpenNotifications").Click();
        Id(page, "Settings.OpenLogFolder").HasAttribute("disabled").Should().BeTrue("ログのフォルダーが無ければ押せない");
    }
}
