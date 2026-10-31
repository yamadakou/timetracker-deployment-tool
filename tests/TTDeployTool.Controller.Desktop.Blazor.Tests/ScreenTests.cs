using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using TTDeployTool.Controller.Desktop.Blazor.Screens;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>表の上のフィルタ・含む文字（変換の間は待つ）・ジョブの一覧・画面の根（desktop-blazor-hybrid.md の 9）。</summary>
public sealed class ScreenTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private IRenderedComponent<DeploymentsPage> Deployments() =>
        _app.Context.Render<DeploymentsPage>(p => p.Add(x => x.Model, _app.ViewModel));

    private IRenderedComponent<JobsPage> Jobs() =>
        _app.Context.Render<JobsPage>(p => p.Add(x => x.Model, _app.ViewModel).Add(x => x.Clipboard, _app.Clipboard));

    [Fact]
    public async Task Text_Filter_Should_Wait_For_Composition_And_A_Pause()
    {
        await _app.SignInAsync();
        var page = Deployments();
        var box = page.FindComponent<Tables.TextFilterBox<TTDeployTool.Application.Inventory.DeploymentEntry>>();
        var filters = _app.ViewModel.Deployments.Filters;

        // 変換の途中は、止まっても絞り込まない
        await box.InvokeAsync(box.Instance.CompositionStarted);
        page.Find("input[data-filter='deployments']").Input("あｐｐ");
        _app.Time.Advance(TimeSpan.FromSeconds(1));
        filters.Text.Should().BeEmpty();

        // 変換が終わって 0.4 秒止まったら、絞り込む（入力欄は書き戻さない）
        page.Find("input[data-filter='deployments']").Input(" app01 ");
        await box.InvokeAsync(box.Instance.CompositionEnded);
        _app.Time.Advance(TimeSpan.FromMilliseconds(399));
        filters.Text.Should().BeEmpty();
        _app.Time.Advance(TimeSpan.FromMilliseconds(1));
        page.WaitForAssertion(() => filters.Text.Should().Be("app01"));
        page.WaitForAssertion(() => page.FindAll("tbody tr").Should().HaveCount(1));
        _app.Module.Invocations.Should().NotContain(i => i.Identifier == "setValue");

        // 「絞り込みをすべて外す」で、入力欄を空にする（JavaScript で入れる）
        page.Find("button.tt-clear").Click();
        page.WaitForAssertion(() => _app.Module.Invocations.Should().Contain(i => i.Identifier == "setValue" && (string)i.Arguments[1]! == ""));
        page.Find("button.tt-clear").HasAttribute("disabled").Should().BeTrue();

        // 入力欄を空（null）にしても、0.4 秒後に絞り込みを外したまま
        page.Find("input[data-filter='deployments']").Input(new ChangeEventArgs { Value = null });
        _app.Time.Advance(TimeSpan.FromSeconds(1));
        page.WaitForAssertion(() => filters.Text.Should().BeEmpty());
    }

    [Fact]
    public async Task Text_And_Date_Filters_Should_Change_Their_Columns()
    {
        await _app.SignInAsync();
        var page = Deployments();
        var filters = _app.ViewModel.Deployments.Filters;

        page.Find("select[data-filter-target='deployments']").Change("3");
        filters.TextTarget.Label.Should().Be("アプリ名");
        page.Find("select[data-date-column='deployments']").Change("1");
        filters.DateColumn.Key.Should().Be("DeployedAt");

        page.Find("input[data-date-from='deployments']").Change("2026-10-20");
        page.Find("input[data-date-to='deployments']").Change("2026-10-25");
        filters.Dates.IsActive.Should().BeTrue();
        page.Find("input[data-date-from='deployments']").GetAttribute("value").Should().Be("2026-10-20");
        _app.ViewModel.Deployments.FilteredCount.Should().Be(6);
        page.Find("input[data-date-to='deployments']").Change("");
        filters.Dates.To.Should().BeNull();
    }

    [Fact]
    public async Task Deployments_Should_Show_Busy_Error_And_Notices()
    {
        // サインインしていないと、読めない（サブスクリプションがない）
        var page = Deployments();
        page.Find("[data-id='Deployments.Load']").Click();
        page.Find(".tt-error").TextContent.Should().Contain("サブスクリプションがありません");

        var done = new TaskCompletionSource();
        var busy = _app.ViewModel.Deployments.Busy.RunAsync("読んでいます", () => done.Task);
        page.WaitForAssertion(() => page.Find(".tt-busy").TextContent.Should().Be("読んでいます…"));
        done.SetResult();
        await busy;
        page.WaitForAssertion(() => page.FindAll(".tt-busy").Should().BeEmpty());
    }

    [Fact]
    public void Jobs_Should_Sort_Filter_And_Open_The_Details_On_A_Double_Click()
    {
        var page = Jobs();
        page.FindAll("tbody tr").Should().HaveCount(20);
        page.Find(".tt-page-text").TextContent.Should().Contain("全 30 件");
        // 既定は登録の新しい順（見出しには並べ替えの印がない）
        page.FindAll("thead th[aria-sort='none']").Should().HaveCount(11);
        page.Find("th[data-key='Result']").GetAttribute("style").Should().Be("min-width: 320px");

        page.Find("th[data-key='Kind'] button.tt-sort").Click();
        _app.ViewModel.JobList.SortColumn.Should().Be("Kind");
        page.Find("th[data-key='Kind'] button.tt-sort").Click();
        page.Find("th[data-key='Kind'] button.tt-sort").Click();
        _app.ViewModel.JobList.SortColumn.Should().BeNull();

        // いちばん新しい記録の結果は 6 行で切れる長い文字。ダブルクリックで詳細を出し、閉じる
        var first = page.FindAll("tbody tr")[0];
        first.QuerySelector("td[data-key='Result'] .tt-cell")!.TextContent.Should().Be(FixtureSamples.LongText);
        first.DoubleClick();
        _app.ViewModel.StatusJob.Should().BeSameAs(_app.ViewModel.JobList.Items[0]);

        // 結果の無い記録（成功・取り消し）
        page.Find("th[data-key='State'] button.tt-filter").Click();
        page.FindAll(".tt-popup-value").Select(v => v.TextContent).Should().Contain("取り消し");
        page.FindAll(".tt-popup-buttons button")[1].Click();
        _app.ViewModel.JobList.Items.Should().Contain(j => j.ResultText == null);

        page.FindAll("tbody tr")[0].Click();
        page.Find("[data-id='Jobs.Selection']").TextContent.Should().Be("1 件を選んでいます。");
        page.Find("[data-id='Jobs.Detail']").Click();
        _app.ViewModel.StatusJob.Should().BeSameAs(_app.ViewModel.JobList.Items[0]);
        page.Find("[data-id='Jobs.ClearSelection']").Click();
        _app.ViewModel.JobList.Selected.Should().BeEmpty();
        page.Find(".tt-pager select").Change("50");
        page.FindAll("tbody tr").Should().HaveCount(30);
    }
}
