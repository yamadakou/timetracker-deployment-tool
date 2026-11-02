using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using TTDeployTool.Controller.Desktop.Blazor.Components;
using TTDeployTool.Controller.Desktop.Blazor.Tables;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>
/// 画面の部品の境界値（オーナーの依頼、2026-11-02: 境界値は単体テストで確かめる）。列の幅の最小（60）・キーボードで縮めたとき・左上のチェックボックス・
/// 幅を保存した列と残りの幅の列の境目・日付の欄（うるう年・月の終わり）・時刻の欄（0:00・23:59・24:00）。
/// </summary>
public sealed class BoundaryTests
{
    private static readonly IReadOnlyList<TableColumnLayout> Layout = [new("A", 100), new("B", 120), new("C", 0)];

    [Theory]
    [InlineData(59, 60)]
    [InlineData(59.9, 60)]
    [InlineData(60, 60)]
    [InlineData(60.4, 60)]
    [InlineData(60.6, 61)]
    [InlineData(61, 61)]
    public void Resize_Should_Not_Go_Below_The_Minimum(double width, double saved) =>
        HtmlTableRules.Resize(Layout, "B", width).Single(c => c.Key == "B").Width.Should().Be(saved);

    [Fact]
    public async Task Keyboard_Should_Shrink_Down_To_The_Minimum_And_Stop()
    {
        // 76 → 60（ちょうど最小）→ 60（それより狭くしない）。75 → 59 は 60 にする
        using var app = new TestApp();
        await app.SignInAsync();
        app.ViewModel.DeploymentsLayout.Save([.. app.ViewModel.DeploymentsLayout.Current.Select(c => c.Key == "ResourceGroup" ? c with { Width = 76 } : c.Key == "AppName" ? c with { Width = 75 } : c)]);
        var page = app.Context.Render<Screens.DeploymentsPage>(p => p.Add(x => x.Model, app.ViewModel));
        var table = page.FindComponent<DataTable<DeploymentRowViewModel, TTDeployTool.Application.Inventory.DeploymentEntry>>();
        string Width(string key) => page.Find($"col[data-key='{key}']").GetAttribute("style")!;

        await table.InvokeAsync(() => table.Instance.ColumnNudged("ResourceGroup", -1));
        Width("ResourceGroup").Should().Be("width: 60px");
        await table.InvokeAsync(() => table.Instance.ColumnNudged("ResourceGroup", -1));
        Width("ResourceGroup").Should().Be("width: 60px");
        await table.InvokeAsync(() => table.Instance.ColumnNudged("AppName", -1));
        Width("AppName").Should().Be("width: 60px");
        await table.InvokeAsync(() => table.Instance.ColumnNudged("ResourceGroup", 1));
        Width("ResourceGroup").Should().Be("width: 76px");
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 0, false)]
    [InlineData(20, 19, null)]
    [InlineData(20, 20, true)]
    [InlineData(20, 1, null)]
    public void Corner_Check_Box_Should_Be_Checked_Only_When_Every_Row_Is_Selected(int rows, int selected, bool? state) =>
        HtmlTableRules.AllState(rows, selected).Should().Be(state);

    [Theory]
    [InlineData(0, 160)]
    [InlineData(1, 1)]
    public void A_Column_Should_Use_Its_Saved_Width_From_One_Pixel(double saved, double shown) =>
        HtmlTableRules.WidthOf([new("A", saved), new("B", 0)], 0, 160).Should().Be(shown);

    [Theory]
    [InlineData("2028-02-29", true)]
    [InlineData("2026-02-29", false)]
    [InlineData("2026-12-31", true)]
    [InlineData("2026-13-01", false)]
    [InlineData("2026-11-00", false)]
    public void Date_Filters_Should_Read_Only_Real_Days(string input, bool read) =>
        (DateInput.Parse(input) != null).Should().Be(read);

    /// <summary>時刻の欄の値（Blazor の JavaScript は秒を付けて渡す）と、Core に渡る時刻（null なら渡さない）。</summary>
    public static TheoryData<string, TimeSpan?> Times => new()
    {
        { "00:00:00", TimeSpan.Zero },
        { "00:00", TimeSpan.Zero },
        { "23:59:00", new TimeSpan(23, 59, 0) },
        { "23:59", new TimeSpan(23, 59, 0) },
        { "24:00:00", null },
        { "12:60:00", null },
        { "9:05", null },
        { "", null },
    };

    [Theory]
    [MemberData(nameof(Times))]
    public void Time_Input_Should_Pass_Only_Real_Times(string input, TimeSpan? expected)
    {
        using var app = new TestApp();
        TimeSpan? changed = null;
        var view = app.Context.Render<DateTimeInput>(p => p
            .Add(x => x.Id, "t").Add(x => x.Time, new TimeSpan(10, 0, 0))
            .Add(x => x.TimeChanged, EventCallback.Factory.Create<TimeSpan>(this, t => changed = t)));
        view.Find("[data-id='tTime']").Change(input);
        changed.Should().Be(expected);
    }

    [Theory]
    [InlineData("2028-02-29", true)]
    [InlineData("2026-02-29", false)]
    [InlineData("2026-12-31", true)]
    public void Date_Input_Should_Pass_Only_Real_Days(string input, bool passed)
    {
        using var app = new TestApp();
        DateTimeOffset? changed = null;
        var view = app.Context.Render<DateTimeInput>(p => p
            .Add(x => x.Id, "d").Add(x => x.Date, new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.FromHours(9)))
            .Add(x => x.DateChanged, EventCallback.Factory.Create<DateTimeOffset>(this, d => changed = d)));
        view.Find("[data-id='dDate']").Change(input);
        (changed != null).Should().Be(passed);
        if (passed)
            changed!.Value.Should().Be(new DateTimeOffset(DateTime.ParseExact(input, "yyyy-MM-dd", null).AddHours(10.5), TimeSpan.FromHours(9)), "時刻と時刻帯はそのまま");
    }
}
