using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Blazor.Components;
using TTDeployTool.Controller.Desktop.Blazor.Screens;
using TTDeployTool.Controller.Desktop.Blazor.Tables;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using Fixtures = TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>
/// 推奨のデザイン（オーナーの判断、2026-11-13）の画面の部品: 件数のカード、期限の残りのバー、状態の印、右の詳細（リソースグループだけの行を含む）、
/// リソースグループだけの削除、削除のジョブのリソースグループの切り替え、スペックのスライダー。
/// </summary>
public sealed class RedesignScreenTests : IDisposable
{
    // 管理タグの残ったリソースグループ（本ツールのアプリのリソースが無い）を 1 つ足す
    private readonly TestApp _app = new(data: d => d with
    {
        ResourceGroups = [.. d.ResourceGroups!, new Fixtures.FixtureResourceGroup(Fixtures.FixtureSamples.SubscriptionA, "rg-old", "japaneast", ManagedTags.ForResourceGroup())],
    });

    public void Dispose() => _app.Dispose();

    private static IElement Id<T>(IRenderedComponent<T> page, string id) where T : Microsoft.AspNetCore.Components.IComponent => page.Find($"[data-id='{id}']");

    private DeploymentListViewModel List => _app.ViewModel.Deployments;

    private async Task<IRenderedComponent<DeploymentsPage>> DeploymentsAsync()
    {
        await _app.SignInAsync();
        List.PageSize = 50;
        return _app.Context.Render<DeploymentsPage>(p => p.Add(x => x.Model, _app.ViewModel));
    }

    [Fact]
    public async Task The_Deployment_Cards_Should_Filter_And_Open_The_Jobs()
    {
        var page = await DeploymentsAsync();
        Id(page, "Deployments.Card.All.Value").TextContent.Should().Be($"{List.TotalCount}");
        foreach (var card in new[] { "Expiring", "Attention" })
            Id(page, $"Deployments.Card.{card}").QuerySelector(".tt-stat-go")!.TextContent.Should().Be("絞り込む ›");
        Id(page, "Deployments.Card.All").GetAttribute("aria-pressed").Should().Be("true");

        Id(page, "Deployments.Card.Expiring").Click();
        page.WaitForAssertion(() => Id(page, "Deployments.Card.Expiring").GetAttribute("aria-pressed").Should().Be("true"));
        List.QuickFilter.Should().Be(DeploymentQuickFilter.ExpiringSoon);
        Id(page, "Deployments.Card.Expiring").Click();
        List.QuickFilter.Should().Be(DeploymentQuickFilter.All, "同じカードをもう一度押すと外す");
        Id(page, "Deployments.Card.Attention").Click();
        List.QuickFilter.Should().Be(DeploymentQuickFilter.Attention);
        page.WaitForAssertion(() => List.Rows.Should().ContainSingle(r => r.AppName == ""));
        Id(page, "Deployments.Card.All").Click();
        List.QuickFilter.Should().Be(DeploymentQuickFilter.All);

        var navigations = new List<string>();
        _app.ViewModel.NavigationRequested += navigations.Add;
        Id(page, "Deployments.Card.Jobs").Click();
        page.WaitForAssertion(() => navigations.Should().Equal(Pages.Jobs));
    }

    [Fact]
    public async Task The_List_Should_Show_The_Cost_Of_Each_Row_And_The_Totals_And_Switch_The_Unit()
    {
        var page = await DeploymentsAsync();
        page.WaitForAssertion(() => Id(page, "Deployments.Cost.Total").TextContent.Should().StartWith("約 ").And.EndWith(" 円"));
        Id(page, "Deployments.Cost.Month").TextContent.Should().EndWith(" 円");
        Id(page, "Deployments.Cost.MonthLabel").TextContent.Should().EndWith("月の見込み");
        Id(page, "Deployments.Cost.TotalNote").TextContent.Should().Contain("件のデプロイから期限まで");
        Id(page, "Deployments.Cost.Note").TextContent.Should().StartWith("料金は目安です");
        page.FindAll("td[data-key='Cost']").Select(c => c.TextContent).Should().Contain(t => t.EndsWith(" 円")).And.Contain("見積もれません");
        page.FindAll("td[data-key='Cost'] [data-sub]").Select(c => c.GetAttribute("data-sub")).Should().Contain(t => t!.StartsWith("1 日 約 "));

        Id(page, "Deployments.Cost.Unit.Usd").Click();
        page.WaitForAssertion(() => Id(page, "Deployments.Cost.Total").TextContent.Should().StartWith("約 $"));
        Id(page, "Deployments.Cost.Unit.Usd").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public async Task The_Batch_Deletion_Should_Explain_Resource_Group_Only_Rows_And_Hide_The_Options_When_All_Are()
    {
        await DeploymentsAsync();
        var group = List.Rows.Single(r => r.AppName == "").Entry;
        var other = List.Rows.First(r => r.AppName != "").Entry;
        await _app.ViewModel.BatchDeletion.OpenAsync([group, other]);
        var page = _app.Context.Render<BatchDeletePage>(p => p.Add(x => x.Model, _app.ViewModel));
        Id(page, "BatchDelete.ResourceGroupOnly").TextContent.Should().StartWith("リソースグループだけの行（1 件）は");
        page.FindAll("[data-id='BatchDelete.DeleteResourceGroup']").Should().NotBeEmpty();
        page.Find("[data-id='BatchDelete.Items']").TextContent.Should().Contain("rg-old（リソースグループだけ）");

        await page.InvokeAsync(() => _app.ViewModel.BatchDeletion.OpenAsync([group]));
        page.WaitForAssertion(() => page.FindAll("[data-id='BatchDelete.DeleteResourceGroup']").Should().BeEmpty());
        page.FindAll("[data-id='BatchDelete.KeepDbData']").Should().BeEmpty();
        Id(page, "BatchDelete.ResourceGroupOnly").TextContent.Should().StartWith("選んだのは、本ツールのアプリのリソースが無い");
    }

    [Fact]
    public async Task The_Job_Cards_Should_Filter()
    {
        await _app.SignInAsync();
        var page = _app.Context.Render<JobsPage>(p => p.Add(x => x.Model, _app.ViewModel).Add(x => x.Clipboard, _app.Clipboard));
        var jobs = _app.ViewModel.JobList;
        foreach (var (card, filter) in new[] { ("Running", JobQuickFilter.Running), ("Pending", JobQuickFilter.Pending), ("Failed", JobQuickFilter.RecentFailed), ("Succeeded", JobQuickFilter.RecentSucceeded) })
        {
            Id(page, $"Jobs.Card.{card}").QuerySelector(".tt-stat-go")!.TextContent.Should().Be("絞り込む ›", "押すと絞り込むカードには、デプロイの一覧と同じく「絞り込む」と出す");
            Id(page, $"Jobs.Card.{card}").Click();
            jobs.QuickFilter.Should().Be(filter);
            page.WaitForAssertion(() => Id(page, $"Jobs.Card.{card}").GetAttribute("aria-pressed").Should().Be("true"));
            Id(page, $"Jobs.Card.{card}").Click();
            jobs.QuickFilter.Should().Be(JobQuickFilter.All);
        }
    }

    [Fact]
    public void Every_State_Should_Have_A_Tone()
    {
        Enum.GetValues<DeploymentState>().Select(TableSources.StateTone).Should().Equal(
            "tt-ok", "tt-muted", "tt-info", "tt-bad", "tt-warn", "tt-info", "tt-muted", "tt-muted");
        Enum.GetValues<DesktopJobState>().ToDictionary(s => s, TableSources.JobTone).Should().Contain(
        [
            new(DesktopJobState.Succeeded, "tt-ok"), new(DesktopJobState.Failed, "tt-bad"), new(DesktopJobState.Canceled, "tt-muted"),
            new(DesktopJobState.Stopped, "tt-muted"), new(DesktopJobState.Running, "tt-info"), new(DesktopJobState.Scheduled, "tt-info"),
        ]);
    }

    [Fact]
    public async Task Expiry_Cells_Should_Show_The_Days_Left_With_A_Colored_Bar()
    {
        // 期限が 3 日後・40 日後・90 日後・なし。25 日進めると 40 日後は残り 15 日（30 日以内の黄）、3 日後は期限切れ
        _app.Time.Advance(TimeSpan.FromDays(25));
        var page = await DeploymentsAsync();
        var cells = page.FindAll("td[data-key='ExpiresAt'] .tt-cell");
        cells.Should().Contain(c => c.QuerySelector(".tt-exp-line") != null && c.QuerySelector(".tt-exp-line")!.GetAttribute("data-sub") == "期限切れ"
                                    && c.QuerySelector(".tt-bar-track.tt-soon") != null);
        cells.Should().Contain(c => c.QuerySelector(".tt-bar-track.tt-mid") != null);
        cells.Should().Contain(c => c.QuerySelector(".tt-bar-track") != null && c.QuerySelector(".tt-bar-track")!.ClassList.Length == 1, "30 日より先は緑（既定）");
        cells.Should().Contain(c => c.QuerySelector(".tt-bar-track") == null, "期限なしはバーを出さない");
        page.FindAll("td[data-key='State'] .tt-cell.tt-dot").Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_Detail_Should_Show_The_Days_Left_And_Extend_Buttons()
    {
        _app.Time.Advance(TimeSpan.FromDays(25));
        await DeploymentsAsync();
        IRenderedComponent<DeploymentDetail> Detail(Func<DeploymentRowViewModel, bool> pick)
        {
            List.SetSelection([List.Rows.First(pick)]);
            return _app.Context.Render<DeploymentDetail>(p => p.Add(x => x.Row, List.Current!));
        }

        var soon = Detail(r => r.DaysLeft == 0);
        Id(soon, "Row.DaysLeft").TextContent.Should().Be("期限切れ");
        Id(soon, "Row.DaysLeft").ClassList.Should().Contain("tt-critical");
        soon.Find("[data-id='Row.Expiry'] .tt-bar-track").ClassList.Should().Contain("tt-soon");
        var mid = Detail(r => r.DaysLeft is > 7 and <= 30);
        mid.Find("[data-id='Row.Expiry'] .tt-bar-track").ClassList.Should().Contain("tt-mid");
        var far = Detail(r => r.DaysLeft > 30);
        Id(far, "Row.DaysLeft").ClassList.Should().NotContain("tt-critical");
        far.Find("[data-id='Row.Expiry'] .tt-bar-track").ClassList.Should().NotContain("tt-mid").And.NotContain("tt-soon");
        var none = Detail(r => r.DaysLeft == null && r.AppName.Length > 0);
        Id(none, "Row.Expiry").TextContent.Should().Be("利用可能期限: 期限なし");
        Id(none, "Row.Extend7").Click();
        none.WaitForAssertion(() => List.Current!.Message.Should().StartWith("利用可能期限を "));
        Id(none, "Row.Extend30").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task A_Resource_Group_Only_Row_Should_Be_Shown_And_Deleted_Alone()
    {
        await DeploymentsAsync();
        List.SetSelection([List.Rows.Single(r => r.AppName == "")]);
        var detail = _app.Context.Render<DeploymentDetail>(p => p.Add(x => x.Row, List.Current!));
        detail.Find("h2").TextContent.Should().StartWith("（リソースグループだけ）");
        detail.FindAll("[data-id='Row.Expiry']").Should().BeEmpty();
        detail.FindAll("[data-id='Row.Extend7']").Should().BeEmpty();
        Id(detail, "Row.StateText").TextContent.Should().StartWith("リソースグループだけ");
        Id(detail, "Row.CheckStatus").HasAttribute("disabled").Should().BeTrue();

        Id(detail, "Row.Delete").Click();
        var page = _app.Context.Render<DeletePage>(p => p.Add(x => x.Model, _app.ViewModel));
        page.WaitForAssertion(() => Id(page, "Delete.ConfirmAppName").Should().NotBeNull());
        Id(page, "Delete.ResourceGroupOnly").TextContent.Should().StartWith("本ツールで作ったリソースグループを削除します");
        page.FindAll("[data-id='Delete.DeleteResourceGroup']").Should().BeEmpty("リソースグループは必ず削除の対象");
        Id(page, "Delete.Target").ParentElement!.TextContent.Should().EndWith("rg-old（リソースグループだけ）");
        Id(page, "Delete.CopyAppName").TextContent.Should().Be("リソースグループ名をコピー");
        page.Find("label.tt-field span").TextContent.Should().Be("リソースグループ名");
    }

    [Fact]
    public async Task A_Pending_Deletion_Should_Change_Whether_It_Deletes_The_Resource_Group()
    {
        await DeploymentsAsync();
        List.SetSelection([List.Rows.First(r => r.AppName == Fixtures.FixtureSamples.AppName(0))]);
        await List.DeleteSelectedCommand.ExecuteAsync();
        var deletion = _app.ViewModel.Deletion;
        await deletion.PlanCommand.ExecuteAsync();
        deletion.ConfirmAppName = Fixtures.FixtureSamples.AppName(0);
        deletion.Schedule.Date = TestApp.Now.AddDays(1);
        await deletion.ScheduleCommand.ExecuteAsync();
        var job = _app.ViewModel.Jobs.Single(j => !j.IsRecord);

        var view = _app.Context.Render<JobView>(p => p.Add(x => x.Job, job));
        Id(view, "Job.DeletionSettings").TextContent.Should().Be("リソースグループは残す");
        Id(view, "Job.DeleteResourceGroup").Change(true);
        Id(view, "Job.ChangeDeletion").Click();
        // 広げるときは確かめる（確かめるダイアログに答える）
        view.WaitForAssertion(() => _app.Dialogs.Pending.Should().NotBeNull());
        await view.InvokeAsync(() => _app.Dialogs.Answer(true));
        view.WaitForAssertion(() => Id(view, "Job.DeletionMessage").TextContent.Should().StartWith("削除の設定を変えました"));
        Id(view, "Job.DeletionSettings").TextContent.Should().StartWith("リソースグループも削除する");

        // 記録のジョブ（削除の設定が無い）には出さない
        var record = _app.Context.Render<JobView>(p => p.Add(x => x.Job, _app.ViewModel.Jobs.First(j => j.IsRecord)));
        record.FindAll("[data-id='Job.Deletion']").Should().BeEmpty();
    }

    [Fact]
    public async Task The_Spec_Sliders_Should_Set_The_Spec()
    {
        await _app.SignInAsync();
        var view = _app.Context.Render<SpecSliderView>(p => p.Add(x => x.Spec, _app.ViewModel.Settings.RedisSpec).Add(x => x.Cost, _app.ViewModel.Cost));
        Id(view, "Spec.Redis.Slider").Input("3");
        view.WaitForAssertion(() => Id(view, "Spec.Redis.Value").TextContent.Should().Be("1 vCPU / 2 GiB"));
        (_app.ViewModel.Settings.RedisCpu, _app.ViewModel.Settings.RedisMemoryGi).Should().Be(("1.0", "2.0"));
        Id(view, "Spec.Redis.Cost").TextContent.Should().BeEmpty("単価を読むまでは出さない");
        await _app.ViewModel.Cost.RefreshAsync();
        view.WaitForAssertion(() => Id(view, "Spec.Redis.Cost").TextContent.Should().StartWith("期限まで 約 "));

        // 期限なしは、1 か月分（内訳とスライダー）と 1 日分
        _app.ViewModel.Settings.Expiry = SettingsViewModel.ExpiryOptions[2];
        view.WaitForAssertion(() => Id(view, "Spec.Redis.Cost").TextContent.Should().StartWith("1 か月 約 "));
        var panel = _app.Context.Render<CostPanel>(p => p.Add(x => x.Cost, _app.ViewModel.Cost));
        (Id(panel, "Cost.Total").TextContent, Id(panel, "Cost.PerDay").TextContent).Should().Match<(string, string)>(x => x.Item1.StartsWith("1 か月 約 ") && x.Item2.StartsWith("1 日 約 "));
        Id(panel, "Cost.Period").TextContent.Should().Be("期限なしのため、目安として 1 か月（30 日）分と 1 日分");
    }

    [Fact]
    public async Task The_Spec_Sliders_Should_Show_The_Range_Below_The_Sample_Db_Minimum_In_Red()
    {
        await _app.SignInAsync();
        var spec = _app.ViewModel.Settings.TtSpec;
        var view = _app.Context.Render<SpecSliderView>(p => p.Add(x => x.Spec, spec).Add(x => x.Cost, _app.ViewModel.Cost));
        Id(view, "Spec.TimeTracker.Below").GetAttribute("style").Should().Be("width: 57.143%");
        view.FindAll(".tt-ticks .tt-tick-low").Select(t => t.TextContent).Should().Equal("0.25", "0.5", "0.75", "1.0");
        Id(view, "Spec.TimeTracker.Minimum").ClassList.Should().Contain("tt-subtle");
        Id(view, "Spec.TimeTracker").ClassList.Should().NotContain("tt-spec-low");

        Id(view, "Spec.TimeTracker.Slider").Input("3");
        view.WaitForAssertion(() => Id(view, "Spec.TimeTracker").ClassList.Should().Contain("tt-spec-low"));
        Id(view, "Spec.TimeTracker.Minimum").ClassList.Should().Contain("tt-critical");
        Id(view, "Spec.TimeTracker.Minimum").TextContent.Should().StartWith("サンプル DB を作れる実用上の最低スペック（1.25 vCPU / 2.5Gi）を下回っています");

        // Redis は最低が 0.25 vCPU（いちばん左）のため、赤の帯は無い
        var redis = _app.Context.Render<SpecSliderView>(p => p.Add(x => x.Spec, _app.ViewModel.Settings.RedisSpec).Add(x => x.Cost, _app.ViewModel.Cost));
        redis.FindAll("[data-id='Spec.Redis.Below']").Should().BeEmpty();
    }

    // ---------------- 行の右クリックのメニュー・ダブルクリック・詳細のコピー（オーナーの依頼、2026-11-14）

    [Fact]
    public async Task The_Deployment_Row_Menu_Should_Show_The_Operations_And_Double_Click_Should_Open_The_Detail()
    {
        var page = await DeploymentsAsync();
        var row = List.Rows.First(r => r.AppName != "");
        var cell = page.FindAll("tbody tr")[List.Rows.IndexOf(row)].QuerySelector("td[data-key='AppName']")!;
        cell.ContextMenu(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { ClientX = 10, ClientY = 10 });
        var menu = page.Find(".tt-menu");
        menu.QuerySelectorAll("button").Select(b => b.TextContent).Should().StartWith(["この文字をコピー", "選んだ行をコピー", "選んだ行を Markdown 形式でコピー", "詳細を開く"]);
        menu.QuerySelectorAll(".tt-menu-sep").Should().HaveCount(4, "コピーとの間と、3 つの区切り");
        Id(page, "Menu.Row.Delete").ClassList.Should().Contain("tt-danger-text");
        Id(page, "Menu.Row.Redeploy").HasAttribute("disabled").Should().BeTrue();

        // 「期限・グループを変える…」: その行を選び、上の操作の帯の期限日に移る
        Id(page, "Menu.Row.EditBatch").Click();
        page.FindAll(".tt-menu").Should().BeEmpty();
        List.Current.Should().BeSameAs(row);
        _app.Module.Invocations.Should().Contain(i => i.Identifier == "focusSelector" && (string)i.Arguments[0]! == "[data-id='Deployments.BatchExpiryDate']");

        // ダブルクリックで、その行だけを選んで右に詳細を出す
        List.SetSelection([]);
        var other = List.Rows.Last(r => r.AppName != "");
        page.FindAll("tbody tr")[List.Rows.IndexOf(other)].DoubleClick();
        page.WaitForAssertion(() => List.Current.Should().BeSameAs(other));
        page.Find("[data-id='Deployments.Detail'] h2").TextContent.Should().StartWith(other.AppName);

        // 画面を閉じたら、メニューからの移動の知らせを受けない
        await _app.Context.DisposeComponentsAsync();
        await List.MenuFor([row]).Single(i => i.Id == "Row.EditBatch").Run();
        _app.Module.Invocations.Count(i => i.Identifier == "focusSelector").Should().Be(1);
    }

    [Fact]
    public async Task The_Job_Row_Menu_Should_Open_The_Detail_And_Move_To_The_Schedule()
    {
        await _app.SignInAsync();
        var page = _app.Context.Render<JobsPage>(p => p.Add(x => x.Model, _app.ViewModel).Add(x => x.Clipboard, _app.Clipboard));
        var job = _app.ViewModel.Queue.Register(JobKind.Delete, new JobTarget(Fixtures.FixtureSamples.SubscriptionA, "rg", "menu"), TestApp.Now.AddDays(1),
            () => new TaskCompletionSource<Job>().Task);
        page.WaitForAssertion(() => page.FindAll("tbody tr").Should().NotBeEmpty());
        var index = _app.ViewModel.JobList.Items.IndexOf(job);
        page.FindAll("tbody tr")[index].QuerySelector("td[data-key]")!.ContextMenu(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Id(page, "Menu.Jobs.Reschedule").Click();
        _app.Module.Invocations.Should().Contain(i => i.Identifier == "focusSelector" && (string)i.Arguments[0]! == "[data-id='Jobs.ScheduleDate']");

        var navigations = new List<string>();
        _app.ViewModel.NavigationRequested += navigations.Add;
        page.FindAll("tbody tr")[index].QuerySelector("td[data-key]")!.ContextMenu(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Id(page, "Menu.Job.Detail").Click();
        page.WaitForAssertion(() => navigations.Should().Contain(Pages.Status));
        await _app.Context.DisposeComponentsAsync();
        await _app.ViewModel.JobList.MenuFor([job], _ => Task.CompletedTask, true).Single(i => i.Id == "Jobs.Reschedule").Run();
        _app.Module.Invocations.Count(i => i.Identifier == "focusSelector").Should().Be(1, "画面を閉じたら受けない");
    }

    [Fact]
    public async Task The_Detail_Should_Copy_The_Selected_Text_Or_The_Whole_Detail_From_Its_Menu()
    {
        await DeploymentsAsync();
        var row = List.Rows.First(r => r.AppName != "");
        List.SetSelection([row]);
        var detail = _app.Context.Render<DeploymentDetail>(p => p.Add(x => x.Row, row));
        var aside = Id(detail, "Deployments.Detail");
        aside.ClassList.Should().Contain("tt-select", "文字を範囲選択できる");

        // 範囲選択していなければ「選んだ文字をコピー」は押せない
        await aside.ContextMenuAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { ClientX = 20, ClientY = 30 });
        Id(detail, "Detail.Menu").GetAttribute("style").Should().Be("left: min(20px, calc(100vw - 300px)); top: min(30px, calc(100vh - 92px))");
        Id(detail, "Detail.CopySelection").HasAttribute("disabled").Should().BeTrue();
        Id(detail, "Detail.CopyAll").Click();
        _app.Clipboard.Texts[^1].Should().Be(row.DetailText);
        detail.FindAll("[data-id='Detail.Menu']").Should().BeEmpty();

        _app.Module.Setup<string?>("selectionIn", _ => true).SetResult("選んだところ");
        await aside.ContextMenuAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Id(detail, "Detail.CopySelection").Click();
        _app.Clipboard.Texts[^1].Should().Be("選んだところ");
        Id(detail, "Row.Message").TextContent.Should().Be("選んだ文字をコピーしました。");

        // メニューの外を押す・右クリック・Esc で閉じる（ほかのキーでは閉じない）
        await aside.ContextMenuAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        detail.Find(".tt-overlay").Click();
        detail.FindAll(".tt-menu").Should().BeEmpty();
        await aside.ContextMenuAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        detail.Find(".tt-overlay").ContextMenu(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        detail.FindAll(".tt-menu").Should().BeEmpty();
        await aside.ContextMenuAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        detail.Find(".tt-menu").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Tab" });
        detail.FindAll(".tt-menu").Should().HaveCount(1);
        detail.Find(".tt-menu").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        detail.FindAll(".tt-menu").Should().BeEmpty();
    }

    [Fact]
    public async Task The_Detail_Menu_Should_Still_Open_When_The_Page_Is_Gone()
    {
        await DeploymentsAsync();
        var row = List.Rows.First(r => r.AppName != "");
        var detail = _app.Context.Render<DeploymentDetail>(p => p.Add(x => x.Row, row));
        _app.Module.Setup<string?>("selectionIn", _ => true).SetException(new Microsoft.JSInterop.JSDisconnectedException("閉じた"));
        await Id(detail, "Deployments.Detail").ContextMenuAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Id(detail, "Detail.CopySelection").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task A_Table_Without_Row_Operations_Should_Show_Only_The_Copies()
    {
        await DeploymentsAsync();
        var table = _app.Context.Render<DataTable<DeploymentRowViewModel, TTDeployTool.Application.Inventory.DeploymentEntry>>(p => p
            .Add(x => x.Source, TableSources.Deployments(List, _app.ViewModel.DeploymentsLayout)).Add(x => x.Clipboard, _app.Clipboard));
        table.Find("tbody td[data-key='AppName']").ContextMenu(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        table.FindAll(".tt-menu button").Should().HaveCount(3);
        table.FindAll(".tt-menu-sep").Should().BeEmpty();
    }
}
