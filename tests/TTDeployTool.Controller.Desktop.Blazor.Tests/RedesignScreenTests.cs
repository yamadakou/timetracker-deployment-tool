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
    public async Task The_Job_Cards_Should_Filter()
    {
        await _app.SignInAsync();
        var page = _app.Context.Render<JobsPage>(p => p.Add(x => x.Model, _app.ViewModel).Add(x => x.Clipboard, _app.Clipboard));
        var jobs = _app.ViewModel.JobList;
        foreach (var (card, filter) in new[] { ("Running", JobQuickFilter.Running), ("Pending", JobQuickFilter.Pending), ("Failed", JobQuickFilter.RecentFailed), ("Succeeded", JobQuickFilter.RecentSucceeded) })
        {
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
}
