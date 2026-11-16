using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Controller.Desktop.Blazor.Components;
using TTDeployTool.Controller.Desktop.Blazor.Screens;
using TTDeployTool.Controller.Desktop.Blazor.Tables;
using TTDeployTool.Controller.Desktop.Core;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>
/// UX の見直し（オーナーの判断、2026-11-16: A〜E）の画面の側。C: 一覧の上の説明を 1 行にして畳む・行を選ぶまで操作の帯を出さない。
/// D: キーボードで行のメニューを開き、最初の項目・閉じた後の表にフォーカスを置く。E: 権限の不足の案内の資料をリンクにする。
/// </summary>
public sealed class UxReviewScreenTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static IElement Id<T>(IRenderedComponent<T> page, string id) where T : Microsoft.AspNetCore.Components.IComponent => page.Find($"[data-id='{id}']");

    private DeploymentListViewModel List => _app.ViewModel.Deployments;

    private async Task<IRenderedComponent<DeploymentsPage>> DeploymentsAsync()
    {
        await _app.SignInAsync();
        return _app.Context.Render<DeploymentsPage>(p => p.Add(x => x.Model, _app.ViewModel));
    }

    [Fact]
    public async Task The_Deployment_List_Should_Show_A_Short_Lead_And_The_Batch_Bars_Only_After_Selecting()
    {
        var page = await DeploymentsAsync();
        Id(page, "Deployments.Lead").TextContent.Should().Be("このツールでデプロイした環境を Azure から読みます。");
        Id(page, "Deployments.More").QuerySelector("summary")!.TextContent.Should().Be("詳しく");
        Id(page, "Deployments.Selection").TextContent.Should().Be(DeploymentListViewModel.NoSelectionText);
        page.FindAll("[data-id='Deployments.ChangeExpiry']").Should().BeEmpty("行を選ぶまで、操作の帯を出さない");
        page.FindAll("[data-id='Deployments.BatchGroup']").Should().BeEmpty();

        page.FindAll("tbody tr")[0].Click();
        Id(page, "Deployments.ChangeExpiry").Should().NotBeNull();
        Id(page, "Deployments.BatchGroup").Should().NotBeNull();
        Id(page, "Deployments.ClearSelection").Click();
        page.FindAll("[data-id='Deployments.ChangeExpiry']").Should().BeEmpty();
    }

    [Fact]
    public async Task The_Cost_Bar_Should_Say_What_It_Counts_And_Show_The_Deleted_Deployments()
    {
        // 削除した日までの料金が無かったことにならないように（オーナーの指摘、2026-11-16。案 1・案 2）
        var page = await DeploymentsAsync();
        Id(page, "Deployments.Cost.Scope").TextContent.Should().StartWith(TTDeployTool.Controller.Desktop.Core.Pricing.DeploymentCostBook.ScopeNote);
        page.FindAll("[data-id='Deployments.Cost.Deleted']").Should().BeEmpty("今月に削除したものが無ければ出さない");
        Id(page, "Deployments.Cost.OpenCostManagement").Click();

        var entry = List.Rows.First(r => r.AppName != "").Entry;
        _app.ViewModel.Deployments.RecordDeleted(entry with { DeployedAt = TestApp.Now.AddDays(-3) }, [entry.Timetracker!.Name], false);
        List.Costs.Currency = TTDeployTool.Controller.Desktop.Core.Pricing.CostEstimateViewModel.CurrencyOptions[1];
        page.WaitForAssertion(() => Id(page, "Deployments.Cost.DeletedTotal").TextContent.Should().StartWith("約 $"));
        Id(page, "Deployments.Cost.DeletedNote").TextContent.Should().Be("今月このアプリで削除した 1 件の、削除した日時までの分（今月分に含みます）");
    }

    [Fact]
    public void The_Job_List_Should_Show_A_Short_Lead()
    {
        var page = _app.Context.Render<JobsPage>(p => p.Add(x => x.Model, _app.ViewModel));
        Id(page, "Jobs.Lead").TextContent.Should().StartWith("このアプリで登録したデプロイ・削除です。");
        Id(page, "Jobs.More").TextContent.Should().Contain("Azure のリソースは消しません");
    }

    [Fact]
    public async Task The_Keyboard_Should_Open_The_Row_Menu_And_Move_The_Focus()
    {
        var page = await DeploymentsAsync();
        var table = page.FindComponent<DataTable<DeploymentRowViewModel, DeploymentEntry>>();
        bool Focused(string selector) => _app.Module.Invocations.Any(i => i.Identifier == "focusIn" && (string)i.Arguments[1]! == selector);

        // 選んだ行が無いと開かず、選び方を伝える（表に無い位置も、選んだ行を使う）
        await table.InvokeAsync(() => table.Instance.KeyboardMenu(-1, 10, 20));
        page.FindAll(".tt-menu").Should().BeEmpty();
        page.Find(".tt-status").TextContent.Should().Contain("Shift+F10");
        await table.InvokeAsync(() => table.Instance.KeyboardMenu(999, 10, 20));
        page.FindAll(".tt-menu").Should().BeEmpty();

        // 行の位置を渡せば、その行のメニュー。最初の項目にフォーカスを置く
        await table.InvokeAsync(() => table.Instance.KeyboardMenu(1, 10, 20));
        page.Find(".tt-menu").GetAttribute("style").Should().StartWith("left: min(10px");
        page.WaitForAssertion(() => Focused(DataTable<DeploymentRowViewModel, DeploymentEntry>.FirstMenuItem).Should().BeTrue());
        Id(page, "Menu.Row.Detail").Click();
        List.Current.Should().BeSameAs(List.Rows[1], "メニューの対象はその行");

        // 選んだ行があれば（-1）、その行のメニュー。Esc で閉じて表に戻る（ほかのキーでは閉じない）
        await table.InvokeAsync(() => table.Instance.KeyboardMenu(-1, 10, 20));
        page.Find(".tt-menu").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        page.FindAll(".tt-menu").Should().HaveCount(1);
        page.Find(".tt-menu").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        page.FindAll(".tt-menu").Should().BeEmpty();
        page.WaitForAssertion(() => Focused(".tt-table-scroll").Should().BeTrue());
    }

    [Fact]
    public async Task The_Detail_Menu_Should_Move_The_Focus_To_Its_Items_And_Back()
    {
        await DeploymentsAsync();
        var row = List.Rows.First(r => r.AppName != "");
        var detail = _app.Context.Render<DeploymentDetail>(p => p.Add(x => x.Row, row));
        bool Focused(string selector) => _app.Module.Invocations.Any(i => i.Identifier == "focusSelector" && (string)i.Arguments[0]! == selector);
        Id(detail, "Deployments.Detail").GetAttribute("tabindex").Should().Be("0", "フォーカスを置いて Shift+F10 で開ける");

        await Id(detail, "Deployments.Detail").ContextMenuAsync(new MouseEventArgs());
        detail.WaitForAssertion(() => Focused("[data-id='Detail.Menu'] [role='menuitem']:not([disabled])").Should().BeTrue());
        Id(detail, "Detail.Menu").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        detail.WaitForAssertion(() => Focused("[data-id='Deployments.Detail']").Should().BeTrue());
    }

    [Fact]
    public void Urls_In_Messages_Should_Be_Links()
    {
        var message = _app.Context.Render<Message>(p => p.Add(x => x.Id, "M").Add(x => x.Text, DesktopTerms.RbacGuide));
        var link = message.Find("a");
        link.GetAttribute("href").Should().Be(DesktopTerms.RbacDocUrl);
        link.GetAttribute("target").Should().Be("_blank");
        message.Find("p").TextContent.Should().Be(DesktopTerms.RbacGuide, "リンクの前後の文字もそのまま");

        LinkedText.Parts(null).Should().BeEmpty();
        LinkedText.Parts("").Should().BeEmpty();
        LinkedText.Parts("文字だけ").Should().Equal(("文字だけ", false));
        LinkedText.Parts("https://a.example/x（説明）と http://b.example").Should().Equal(
            ("https://a.example/x", true), ("（説明）と ", false), ("http://b.example", true));

        var lines = _app.Context.Render<Lines>(p => p.Add(x => x.Id, "L").Add(x => x.Items, ["詳しくは https://c.example 。"]));
        lines.Find("li a").GetAttribute("href").Should().Be("https://c.example");
    }
}
