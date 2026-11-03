using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Blazor.Screens;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>
/// デプロイのジョブの「この設定で新しくデプロイ」（オーナーの依頼、2026-11-03）を、ジョブの一覧・実行状況の画面・デプロイの一覧の詳細から押す。
/// 記録の 1 件目（app00 のデプロイ・失敗）だけに設定を持たせ、ほかは設定の無い（この機能より前の）記録。
/// </summary>
public sealed class RedeployScreenTests : IDisposable
{
    private static readonly DeploymentTemplate Settings = new("", FixtureSamples.SubscriptionA, FixtureSamples.ResourceGroup(0), FixtureSamples.AppName(0), "japaneast",
        "postgresql", "timetracker", "", "1.25", "2.5", "0.5", "1.0", "0.25", "0.5", true, ExpiryMode.Date, "2026-12-24", "チーム A");

    private readonly TestApp _app = new(history: records =>
    [
        new JobRecord(JobKind.Deploy, FixtureSamples.SubscriptionA, FixtureSamples.ResourceGroup(0), FixtureSamples.AppName(0), false, null, TestApp.Now.AddMinutes(-1),
            DesktopJobState.Failed, "実行しませんでした", null, null, null, null, null, [], Settings: Settings),
        .. records,
    ]);

    public void Dispose() => _app.Dispose();

    private static IElement Id<T>(IRenderedComponent<T> page, string id) where T : Microsoft.AspNetCore.Components.IComponent => page.Find($"[data-id='{id}']");

    private JobViewModel WithSettings => _app.ViewModel.Jobs.Single(j => j.Settings != null);

    private JobViewModel OldDeployment => _app.ViewModel.Jobs.First(j => j.IsDeployment && j.Settings == null && !j.IsActive);

    private JobViewModel Deletion => _app.ViewModel.Jobs.First(j => !j.IsDeployment && !j.IsActive);

    // ジョブの設定が入力に入り、「新しくデプロイ」の ① を開いた
    private void ShouldBeLoaded(List<string> navigations)
    {
        (_app.ViewModel.Target.ResourceGroup, _app.ViewModel.Target.AppName, _app.ViewModel.Settings.TtCpu, _app.ViewModel.Settings.Group)
            .Should().Be((FixtureSamples.ResourceGroup(0), FixtureSamples.AppName(0), "1.25", "チーム A"));
        _app.ViewModel.Settings.ExpiryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).Should().Be("2026-12-24");
        navigations.Should().Equal(Pages.Wizard);
        _app.ViewModel.Wizard.Step.Should().Be(WizardStep.Target);
    }

    [Fact]
    public async Task The_Job_List_Should_Load_The_Selected_Deployment()
    {
        var page = _app.Context.Render<JobsPage>(p => p.Add(x => x.Model, _app.ViewModel).Add(x => x.Clipboard, _app.Clipboard));
        var jobs = _app.ViewModel.JobList;
        Id(page, "Jobs.Redeploy").HasAttribute("disabled").Should().BeTrue("選んでいない");
        jobs.SetSelection([WithSettings]);
        page.WaitForAssertion(() => Id(page, "Jobs.Redeploy").HasAttribute("disabled").Should().BeTrue("サインインするまで押せない"));

        await _app.SignInAsync();
        var navigations = new List<string>();
        _app.ViewModel.NavigationRequested += navigations.Add;
        page.WaitForAssertion(() => Id(page, "Jobs.Redeploy").HasAttribute("disabled").Should().BeFalse());
        page.FindAll("[data-id='Jobs.RedeployNote']").Should().BeEmpty();
        Id(page, "Jobs.Redeploy").Click();
        page.WaitForAssertion(() => ShouldBeLoaded(navigations));

        // 設定の無い記録は押せず、理由を出す。削除のジョブには出さない
        jobs.SetSelection([OldDeployment]);
        page.WaitForAssertion(() => Id(page, "Jobs.RedeployNote").TextContent.Should().StartWith("このジョブの記録には設定が残っていないため"));
        Id(page, "Jobs.Redeploy").HasAttribute("disabled").Should().BeTrue();
        jobs.SetSelection([Deletion]);
        page.WaitForAssertion(() => page.FindAll("[data-id='Jobs.RedeployNote']").Should().BeEmpty());

        // 2 行を選ぶと（Current が無い）、前の行の理由を残さず、押せない（WinUI 3 版では前の文が残っていた。WinUI 3 版のセッションから、2026-11-03）
        jobs.SetSelection([OldDeployment]);
        page.WaitForAssertion(() => page.FindAll("[data-id='Jobs.RedeployNote']").Should().ContainSingle());
        jobs.SetSelection([OldDeployment, WithSettings]);
        page.WaitForAssertion(() => page.FindAll("[data-id='Jobs.RedeployNote']").Should().BeEmpty());
        Id(page, "Jobs.Redeploy").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task The_Status_Page_Should_Load_The_Finished_Deployment()
    {
        await _app.SignInAsync();
        var page = _app.Context.Render<StatusPage>(p => p.Add(x => x.Model, _app.ViewModel));
        await _app.ViewModel.OpenJobAsync(Deletion);
        page.WaitForAssertion(() => page.FindAll("[data-id='Job.Redeploy']").Should().BeEmpty("削除のジョブには出さない"));

        await _app.ViewModel.OpenJobAsync(OldDeployment);
        page.WaitForAssertion(() => Id(page, "Job.RedeployNote").TextContent.Should().StartWith("このジョブの記録には設定が残っていないため"));
        Id(page, "Job.Redeploy").HasAttribute("disabled").Should().BeTrue();

        var navigations = new List<string>();
        _app.ViewModel.NavigationRequested += navigations.Add;
        await _app.ViewModel.OpenJobAsync(WithSettings);
        navigations.Clear();
        page.WaitForAssertion(() => Id(page, "Job.Redeploy").HasAttribute("disabled").Should().BeFalse());
        page.FindAll("[data-id='Job.RedeployNote']").Should().BeEmpty();
        Id(page, "Job.Redeploy").Click();
        page.WaitForAssertion(() => ShouldBeLoaded(navigations));
    }

    [Fact]
    public async Task The_Status_Page_Should_Not_Offer_It_For_A_Job_That_Has_Not_Finished()
    {
        await _app.SignInAsync();
        var job = _app.ViewModel.Queue.Register(JobKind.Deploy, new JobTarget(FixtureSamples.SubscriptionA, "rg-new", "new"), TestApp.Now.AddHours(1),
            () => new TaskCompletionSource<TTDeployTool.Application.Jobs.Job>().Task);
        var page = _app.Context.Render<StatusPage>(p => p.Add(x => x.Model, _app.ViewModel));
        await _app.ViewModel.OpenJobAsync(job);
        page.WaitForAssertion(() => page.FindAll("[data-id='Job.Cancel']").Should().NotBeEmpty());
        page.FindAll("[data-id='Job.Redeploy']").Should().BeEmpty();
    }

    [Fact]
    public async Task The_Deployment_Detail_Should_Load_The_Last_Deployment_Of_The_Row()
    {
        await _app.SignInAsync();
        var list = _app.ViewModel.Deployments;
        list.PageSize = 50;
        list.SetSelection([list.Rows.First(r => r.AppName == FixtureSamples.AppName(2))]);
        var detail = _app.Context.Render<DeploymentDetail>(p => p.Add(x => x.Row, list.Current!));
        Id(detail, "Row.Redeploy").HasAttribute("disabled").Should().BeTrue("設定のあるデプロイのジョブが無い");

        list.SetSelection([list.Rows.First(r => r.AppName == FixtureSamples.AppName(0))]);
        detail.Render(p => p.Add(x => x.Row, list.Current!));
        var navigations = new List<string>();
        _app.ViewModel.NavigationRequested += navigations.Add;
        Id(detail, "Row.Redeploy").HasAttribute("disabled").Should().BeFalse();
        Id(detail, "Row.Redeploy").Click();
        detail.WaitForAssertion(() => ShouldBeLoaded(navigations));
    }
}
