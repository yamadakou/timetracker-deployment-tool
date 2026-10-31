using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using TTDeployTool.Controller.Desktop.Blazor.Screens;
using TTDeployTool.Controller.Desktop.Blazor.Tables;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>デプロイの一覧の表（desktop-blazor-hybrid.md の 9）。Core の一覧に、見出し・▽・選択・コピー・列の配置の操作が届くことを確かめる。</summary>
public sealed class DeploymentTableTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private IRenderedComponent<DeploymentsPage> _page = default!;

    private DeploymentListViewModel List => _app.ViewModel.Deployments;

    public async ValueTask InitializeAsync()
    {
        await _app.SignInAsync();
        _page = Render();
    }

    public ValueTask DisposeAsync()
    {
        _app.Dispose();
        return ValueTask.CompletedTask;
    }

    private IRenderedComponent<DeploymentsPage> Render(RecordingClipboard? clipboard = null) =>
        _app.Context.Render<DeploymentsPage>(p => p
            .Add(x => x.Model, _app.ViewModel)
            .Add(x => x.Clipboard, clipboard ?? _app.Clipboard));

    private IReadOnlyList<IElement> Rows => _page.FindAll("tbody tr");

    private IElement Header(string key) => _page.Find($"th[data-key='{key}']");

    private string[] HeaderKeys => [.. _page.FindAll("thead th[data-key]").Select(h => h.GetAttribute("data-key")!)];

    private string[] Column(string key) => [.. _page.FindAll($"tbody td[data-key='{key}']").Select(c => c.TextContent)];

    [Fact]
    public void Should_Show_The_First_Page_With_The_Default_Sort_And_Fill_The_Last_Column()
    {
        Rows.Should().HaveCount(20);
        HeaderKeys.Should().Equal("Subscription", "ResourceGroup", "AppName", "Group", "State", "ExpiresAt", "DeployedAt", "Job");
        Header("Subscription").GetAttribute("aria-sort").Should().Be("ascending");
        Header("ResourceGroup").GetAttribute("aria-sort").Should().Be("none");
        // 右端の列（ジョブ）は幅を指定せず、最小の幅だけ持つ。並べ替えない列は、ボタンではない
        _page.Find("col[data-key='Job']").HasAttribute("style").Should().BeFalse();
        _page.Find("col[data-key='Subscription']").GetAttribute("style").Should().Be("width: 200px");
        Header("Job").GetAttribute("style").Should().Be("min-width: 160px");
        Header("Job").QuerySelector("button.tt-sort").Should().BeNull();
        Header("Job").QuerySelector(".tt-nosort").Should().NotBeNull();
        _page.Find("table").GetAttribute("style").Should().Be("min-width: 1220px");
        _page.Find(".tt-page-text").TextContent.Should().Contain("1〜20 件目");
        _page.Find("[data-id='Deployments.Notices']").TextContent.Should().Contain("Azure に反映されるまで");
    }

    [Fact]
    public void Header_Clicks_Should_Sort_Ascending_Descending_Then_Go_Back_To_The_Default()
    {
        IElement Button() => Header("ResourceGroup").QuerySelector("button.tt-sort")!;

        Button().Click();
        List.Sort.Should().Be(DeploymentListViewModel.SortOptions.Single(o => o.Key == DeploymentSortKey.ResourceGroup && !o.Descending));
        Header("ResourceGroup").GetAttribute("aria-sort").Should().Be("ascending");
        Header("ResourceGroup").QuerySelector(".tt-arrow")!.TextContent.Should().Be("▲");
        Column("ResourceGroup")[0].Should().Be(FixtureSamples.ResourceGroup(0));

        Button().Click();
        Header("ResourceGroup").GetAttribute("aria-sort").Should().Be("descending");
        Header("ResourceGroup").QuerySelector(".tt-arrow")!.TextContent.Should().Be("▼");
        Column("ResourceGroup")[0].Should().Be(FixtureSamples.ResourceGroup(44));

        Button().Click();
        List.Sort.Should().Be(DeploymentListViewModel.SortOptions[0]);
        Header("ResourceGroup").GetAttribute("aria-sort").Should().Be("none");
        Header("ResourceGroup").QuerySelector(".tt-arrow")!.TextContent.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Values_Should_Narrow_The_Rows_And_Mark_The_Header()
    {
        Header("Group").QuerySelector("button.tt-filter")!.Click();
        _page.FindAll(".tt-popup-value").Select(v => v.TextContent).Should().Equal("営業", "研修", "（グループなし）");
        _page.FindAll(".tt-popup-count").Select(v => v.TextContent).Should().Equal("（15）", "（15）", "（15）");
        Header("Group").QuerySelector("button.tt-filter")!.GetAttribute("aria-expanded").Should().Be("true");

        // 「研修」だけを外して OK
        _page.FindAll(".tt-popup-values input")[1].Change(false);
        _page.Find(".tt-popup-buttons .tt-primary").Click();
        _page.FindAll(".tt-popup").Should().BeEmpty();
        List.FilteredCount.Should().Be(30);
        Header("Group").ClassList.Should().Contain("tt-filtered");
        Header("Group").QuerySelector("button.tt-filter")!.GetAttribute("aria-pressed").Should().Be("true");

        // 探して 1 つにする
        Header("Group").QuerySelector("button.tt-filter")!.Click();
        _page.Find(".tt-popup-search").Input("営");
        _page.FindAll(".tt-popup-value").Select(v => v.TextContent).Should().Equal("営業");
        _page.Find(".tt-popup-buttons .tt-primary").Click();
        List.FilteredCount.Should().Be(15);

        // フィルターをクリア
        Header("Group").QuerySelector("button.tt-filter")!.Click();
        _page.FindAll(".tt-popup-buttons button")[2].Click();
        List.FilteredCount.Should().Be(45);
        Header("Group").ClassList.Should().NotContain("tt-filtered");
    }

    [Fact]
    public void Filter_Popup_Should_Select_All_Clear_All_Sort_And_Close()
    {
        IElement Filter() => Header("State").QuerySelector("button.tt-filter")!;
        Filter().Click();
        var all = _page.Find(".tt-popup-all input");
        all.HasAttribute("checked").Should().BeTrue();

        // すべて外して OK すると、どの行も出さない。もう一度すべて選んで OK すると、絞り込みを外す
        all.Change(false);
        _page.FindAll(".tt-popup-values input").Should().OnlyContain(i => !i.HasAttribute("checked"));
        _page.Find(".tt-popup-buttons .tt-primary").Click();
        List.FilteredCount.Should().Be(0);
        Filter().Click();
        _page.Find(".tt-popup-all input").Change(true);
        _page.FindAll(".tt-popup-values input").Should().OnlyContain(i => i.HasAttribute("checked"));
        _page.Find(".tt-popup-buttons .tt-primary").Click();
        List.FilteredCount.Should().Be(45);
        List.Filters.IsFiltered("State").Should().BeFalse();

        // 1 つ外して戻す（変えずに OK）・探す欄を空にする
        Filter().Click();
        var first = _page.FindAll(".tt-popup-values input")[0];
        first.Change(false);
        _page.FindAll(".tt-popup-values input")[0].Change(true);
        _page.Find(".tt-popup-search").Input(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = null });
        _page.Find(".tt-popup-buttons .tt-primary").Click();
        List.Filters.IsFiltered("State").Should().BeFalse();

        // ▽ の中の並べ替え
        Filter().Click();
        _page.FindAll(".tt-popup-sort button")[1].Click();
        List.Sort.Key.Should().Be(DeploymentSortKey.State);
        List.Sort.Descending.Should().BeTrue();
        Filter().Click();
        _page.FindAll(".tt-popup-sort button")[0].Click();
        List.Sort.Descending.Should().BeFalse();
        Filter().Click();
        _page.FindAll(".tt-popup-sort button")[2].Click();
        List.Sort.Should().Be(DeploymentListViewModel.SortOptions[0]);

        // キャンセル・Esc・もう一度 ▽ で閉じる（ほかのキーでは閉じない）
        Filter().Click();
        _page.FindAll(".tt-popup-buttons button")[1].Click();
        _page.FindAll(".tt-popup").Should().BeEmpty();
        Filter().Click();
        _page.Find(".tt-popup").KeyDown(new KeyboardEventArgs { Key = "a" });
        _page.FindAll(".tt-popup").Should().HaveCount(1);
        _page.Find(".tt-popup").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        _page.FindAll(".tt-popup").Should().BeEmpty();
        Filter().Click();
        Filter().Click();
        _page.FindAll(".tt-popup").Should().BeEmpty();
        Filter().Click();
        _page.Find(".tt-table-scroll").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        _page.FindAll(".tt-popup").Should().BeEmpty();
    }

    [Fact]
    public void Filter_Popup_Of_A_Column_That_Does_Not_Sort_Should_Have_No_Sort_Buttons()
    {
        Header("Job").QuerySelector("button.tt-filter")!.Click();
        _page.FindAll(".tt-popup-sort").Should().BeEmpty();
        _page.FindAll(".tt-popup-value").Select(v => v.TextContent).Should().Equal("なし");
    }

    [Fact]
    public void Checkboxes_And_Row_Clicks_Should_Select_Rows()
    {
        var all = _page.Find("thead input[type=checkbox]");
        all.GetAttribute("data-indeterminate").Should().Be("false");

        Rows[0].Click();
        Rows[2].QuerySelector("input")!.Change(true);
        List.Selected.Select(r => r.AppName).Should().Equal(List.Rows[0].AppName, List.Rows[2].AppName);
        Rows[0].GetAttribute("aria-selected").Should().Be("true");
        Rows[0].ClassList.Should().Contain("tt-selected");
        _page.Find("thead input[type=checkbox]").GetAttribute("data-indeterminate").Should().Be("true");
        _page.Find("[data-id='Deployments.Selection']").TextContent.Should().Be("2 件を選んでいます。");

        Rows[0].Click();
        List.Selected.Should().ContainSingle();

        _page.Find("thead input[type=checkbox]").Change(true);
        List.Selected.Should().HaveCount(20);
        _page.Find("thead input[type=checkbox]").HasAttribute("checked").Should().BeTrue();
        _page.Find("thead input[type=checkbox]").Change(false);
        List.Selected.Should().BeEmpty();

        Rows[1].Click();
        _page.Find("[data-id='Deployments.ClearSelection']").Click();
        List.Selected.Should().BeEmpty();
    }

    [Fact]
    public void Ctrl_C_Should_Copy_The_Selected_Rows_In_The_Column_Order()
    {
        var scroll = _page.Find(".tt-table-scroll");
        scroll.KeyDown(new KeyboardEventArgs { Key = "c", CtrlKey = true });
        scroll.KeyDown(new KeyboardEventArgs { Key = "C", CtrlKey = true });
        _app.Clipboard.Texts.Should().BeEmpty();

        Rows[0].Click();
        Rows[1].Click();
        scroll.KeyDown(new KeyboardEventArgs { Key = "c", CtrlKey = true });
        scroll.KeyDown(new KeyboardEventArgs { Key = "C", CtrlKey = true, ShiftKey = true });
        scroll.KeyDown(new KeyboardEventArgs { Key = "c" });
        scroll.KeyDown(new KeyboardEventArgs { Key = "x", CtrlKey = true });
        _app.Clipboard.Texts.Should().HaveCount(2);
        _app.Clipboard.Texts[0].Split("\r\n").Should().HaveCount(2);
        _app.Clipboard.Texts[0].Should().StartWith(List.Filters.Column("Subscription").ValueOf(List.Rows[0].Entry) + "\t" + List.Rows[0].Entry.ResourceGroup + "\t");
        _app.Clipboard.Texts[1].Should().StartWith("サブスクリプション\tリソースグループ\tアプリ名");
        _page.Find(".tt-status").TextContent.Should().Be("2 行をコピーしました。");
    }

    [Fact]
    public void Context_Menu_Should_Copy_A_Cell_The_Rows_And_Markdown()
    {
        IElement Cell(int row, string key) => Rows[row].QuerySelector($"td[data-key='{key}']")!;

        Cell(0, "AppName").ContextMenu(new MouseEventArgs { ClientX = 120, ClientY = 80 });
        _page.Find(".tt-menu").GetAttribute("style").Should().Be("left: 120px; top: 80px");
        _page.FindAll(".tt-menu button")[0].Click();
        _app.Clipboard.Texts.Should().Equal(List.Rows[0].AppName);
        _page.FindAll(".tt-menu").Should().BeEmpty();

        // 選んでいなければ、右クリックした行
        Cell(1, "AppName").ContextMenu(new MouseEventArgs());
        _page.FindAll(".tt-menu button")[1].Click();
        _app.Clipboard.Texts[^1].Should().Contain(List.Rows[1].AppName).And.NotContain("\r\n");
        _page.Find(".tt-status").TextContent.Should().Be("1 行をコピーしました。");

        Rows[2].Click();
        Rows[3].Click();
        Cell(0, "AppName").ContextMenu(new MouseEventArgs());
        _page.FindAll(".tt-menu button")[2].Click();
        _app.Clipboard.Texts[^1].Should().StartWith("| サブスクリプション |").And.Contain(List.Rows[3].AppName).And.NotContain(List.Rows[0].AppName + " |");
        _page.Find(".tt-status").TextContent.Should().Be("2 行を Markdown 形式でコピーしました。");

        // グループの無い行のグループのセルは空のため、コピーしない
        Cell(2, "Group").ContextMenu(new MouseEventArgs());
        _page.FindAll(".tt-menu button")[0].Click();
        _app.Clipboard.Texts.Should().HaveCount(3);

        // メニューの外を押す・右クリック・Esc で閉じる（ほかのキーでは閉じない）
        Cell(0, "AppName").ContextMenu(new MouseEventArgs());
        _page.Find(".tt-overlay").Click();
        _page.FindAll(".tt-menu").Should().BeEmpty();
        Cell(0, "AppName").ContextMenu(new MouseEventArgs());
        _page.Find(".tt-overlay").ContextMenu(new MouseEventArgs());
        _page.FindAll(".tt-menu").Should().BeEmpty();
        Cell(0, "AppName").ContextMenu(new MouseEventArgs());
        _page.Find(".tt-menu").KeyDown(new KeyboardEventArgs { Key = "Tab" });
        _page.FindAll(".tt-menu").Should().HaveCount(1);
        _page.Find(".tt-menu").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        _page.FindAll(".tt-menu").Should().BeEmpty();
    }

    [Fact]
    public void Copy_Should_Report_A_Busy_Clipboard_And_Do_Nothing_Without_One()
    {
        _app.Clipboard.Fail = true;
        Rows[0].QuerySelector("td[data-key='AppName']")!.ContextMenu(new MouseEventArgs());
        _page.FindAll(".tt-menu button")[0].Click();
        _page.Find(".tt-status").TextContent.Should().Be("コピーできませんでした。もう一度試してください。");

        var page = _app.Context.Render<DeploymentsPage>(p => p.Add(x => x.Model, _app.ViewModel));
        page.FindAll("tbody tr")[0].QuerySelector("td[data-key='AppName']")!.ContextMenu(new MouseEventArgs());
        page.FindAll(".tt-menu button")[0].Click();
        page.Find(".tt-status").TextContent.Should().BeEmpty();
    }

    [Fact]
    public async Task Resizing_And_Reordering_Should_Save_The_Layout_And_Reset_Should_Restore_It()
    {
        var table = _page.FindComponent<DataTable<DeploymentRowViewModel, TTDeployTool.Application.Inventory.DeploymentEntry>>();

        // つまみのドラッグ（JavaScript から）・Alt+←/→・Alt+Shift+←/→
        await table.InvokeAsync(() => table.Instance.ColumnResized("ResourceGroup", 20));
        _page.Find("col[data-key='ResourceGroup']").GetAttribute("style").Should().Be("width: 60px");
        await table.InvokeAsync(() => table.Instance.ColumnNudged("ResourceGroup", 1));
        _page.Find("col[data-key='ResourceGroup']").GetAttribute("style").Should().Be("width: 76px");
        await table.InvokeAsync(() => table.Instance.ColumnNudged("Job", 1));
        Header("Job").GetAttribute("style").Should().Be("min-width: 176px");
        await table.InvokeAsync(() => table.Instance.ColumnNudged("Unknown", 1));
        await table.InvokeAsync(() => table.Instance.ColumnShifted("AppName", -1));
        HeaderKeys.Take(3).Should().Equal("Subscription", "AppName", "ResourceGroup");

        // 見出しのドラッグ（ドラッグしないで落としても変えない）
        Header("Job").Drop(new DragEventArgs());
        Header("Job").QuerySelector(".tt-head")!.DragStart(new DragEventArgs());
        Header("Subscription").Drop(new DragEventArgs());
        HeaderKeys.Should().Equal("Job", "Subscription", "AppName", "ResourceGroup", "Group", "State", "ExpiresAt", "DeployedAt");
        // 動かした右端の列（幅 0）は最小の幅になり、新しい右端の列が残りの幅いっぱいに伸びる
        _page.Find("col[data-key='Job']").GetAttribute("style").Should().Be("width: 176px");
        _page.Find("col[data-key='DeployedAt']").HasAttribute("style").Should().BeFalse();
        Rows[0].QuerySelectorAll("td[data-key]").Select(c => c.GetAttribute("data-key")).First().Should().Be("Job");

        // 保存した配置は、次の起動（作り直した画面）でも同じ
        _app.ViewModel.DeploymentsLayout.Current.Select(c => c.Key).Should().Equal(table.Instance.Layout.Select(c => c.Key));
        var again = Render();
        again.FindAll("thead th[data-key]").Select(h => h.GetAttribute("data-key")).First().Should().Be("Job");

        _page.Find(".tt-reset").Click();
        HeaderKeys.Should().Equal("Subscription", "ResourceGroup", "AppName", "Group", "State", "ExpiresAt", "DeployedAt", "Job");
        _app.ViewModel.DeploymentsLayout.Current.Should().Equal(_app.ViewModel.DeploymentsLayout.Defaults);
    }

    [Fact]
    public void JavaScript_Should_Be_Attached_Once_And_Synced_After_Each_Render()
    {
        _app.Module.Invocations.Select(i => i.Identifier).Should().Contain(["attach", "attachComposition"]);
        var syncs = _app.Module.Invocations.Count(i => i.Identifier == "sync");
        Rows[0].Click();
        _app.Module.Invocations.Count(i => i.Identifier == "sync").Should().BeGreaterThan(syncs);
        _app.Module.Invocations.Count(i => i.Identifier == "attach").Should().Be(1);
    }

    [Fact]
    public void Pages_Should_Move_And_Change_The_Page_Size()
    {
        var pager = _page.FindAll(".tt-pager button");
        pager[0].HasAttribute("disabled").Should().BeTrue();
        pager[1].Click();
        List.PageIndex.Should().Be(1);
        Column("ResourceGroup").Should().NotBeEmpty();
        _page.FindAll(".tt-pager button")[0].Click();
        List.PageIndex.Should().Be(0);

        _page.Find(".tt-pager select").Change("50");
        List.PageSize.Should().Be(50);
        Rows.Should().HaveCount(45);
        _page.FindAll(".tt-pager button")[1].HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void A_Change_In_Core_Should_Render_Again()
    {
        var renders = _page.RenderCount;
        List.Filters.Text = "app00";
        _page.WaitForAssertion(() => Rows.Should().HaveCount(1));
        _page.RenderCount.Should().BeGreaterThan(renders);
    }
}
