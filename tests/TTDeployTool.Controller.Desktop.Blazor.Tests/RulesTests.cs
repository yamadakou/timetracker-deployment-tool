using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.JSInterop;
using TTDeployTool.Controller.Desktop.Blazor.Tables;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

public sealed class HtmlTableRulesTests
{
    private static readonly IReadOnlyList<TableColumnLayout> Layout = [new("A", 100), new("B", 120), new("C", 0)];

    [Fact]
    public void Header_Clicks_Should_Go_Ascending_Descending_Then_Default()
    {
        HtmlTableRules.Next(null, false, "A").Should().Be(new SortStep("A", false));
        HtmlTableRules.Next("B", true, "A").Should().Be(new SortStep("A", false));
        HtmlTableRules.Next("A", false, "A").Should().Be(new SortStep("A", true));
        HtmlTableRules.Next("A", true, "A").Should().Be(new SortStep(null, false));
    }

    [Fact]
    public void Move_Should_Put_The_Column_At_The_Target_And_Ignore_Unknown_Or_Same_Columns()
    {
        HtmlTableRules.Move(Layout, "C", "A").Select(c => c.Key).Should().Equal("C", "A", "B");
        HtmlTableRules.Move(Layout, "A", "C").Select(c => c.Key).Should().Equal("B", "C", "A");
        HtmlTableRules.Move(Layout, "X", "A").Should().BeSameAs(Layout);
        HtmlTableRules.Move(Layout, "A", "X").Should().BeSameAs(Layout);
        HtmlTableRules.Move(Layout, "A", "A").Should().BeSameAs(Layout);
    }

    [Fact]
    public void Shift_Should_Move_One_Step_And_Stop_At_The_Ends()
    {
        HtmlTableRules.Shift(Layout, "B", -1).Select(c => c.Key).Should().Equal("B", "A", "C");
        HtmlTableRules.Shift(Layout, "B", 1).Select(c => c.Key).Should().Equal("A", "C", "B");
        HtmlTableRules.Shift(Layout, "A", -1).Should().BeSameAs(Layout);
        HtmlTableRules.Shift(Layout, "C", 1).Should().BeSameAs(Layout);
        HtmlTableRules.Shift(Layout, "X", 1).Should().BeSameAs(Layout);
    }

    [Fact]
    public void Resize_Should_Keep_The_Minimum_And_Round()
    {
        HtmlTableRules.Resize(Layout, "B", 10).Should().Equal([new TableColumnLayout("A", 100), new("B", HtmlTableRules.MinColumnWidth), new("C", 0)]);
        HtmlTableRules.Resize(Layout, "C", 200.4).Should().Equal([new TableColumnLayout("A", 100), new("B", 120), new("C", 200)]);
    }

    [Fact]
    public void Widths_Should_Leave_The_Last_Column_To_Fill_And_Use_The_Minimum_For_Moved_Fill_Columns()
    {
        HtmlTableRules.WidthOf(Layout, 0, 160).Should().Be(100);
        HtmlTableRules.WidthOf(Layout, 2, 160).Should().BeNull();
        HtmlTableRules.WidthOf([new("C", 0), new("A", 100)], 0, 160).Should().Be(160);
        HtmlTableRules.LastMinWidth(Layout, 160).Should().Be(160);
        HtmlTableRules.LastMinWidth([new("A", 100), new("C", 300)], 160).Should().Be(300);
        HtmlTableRules.LastMinWidth([], 160).Should().Be(0);
        HtmlTableRules.MinTableWidth(Layout, 160).Should().Be(HtmlTableRules.CheckColumnWidth + 100 + 120 + 160);
    }

    [Fact]
    public void All_State_Should_Be_Checked_Unchecked_Or_Mixed()
    {
        HtmlTableRules.AllState(0, 0).Should().BeFalse();
        HtmlTableRules.AllState(3, 0).Should().BeFalse();
        HtmlTableRules.AllState(3, 3).Should().BeTrue();
        HtmlTableRules.AllState(3, 1).Should().BeNull();
    }

    [Fact]
    public void Date_Input_Should_Convert_Both_Ways()
    {
        DateInput.Format(null).Should().BeNull();
        DateInput.Format(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)).Should().Be("2026-11-02");
        DateInput.Parse("2026-11-02").Should().Be(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        DateInput.Parse("").Should().BeNull();
        DateInput.Parse("11/02").Should().BeNull();
        DateInput.Parse(null).Should().BeNull();
    }
}

public sealed class WatcherTests
{
    private sealed class Model : ObservableObject
    {
        public void Change() => OnPropertyChanged("Value");
    }

    [Fact]
    public void Should_Report_Property_Collection_And_Command_Changes_Until_Disposed()
    {
        var count = 0;
        var watcher = new Watcher(() => count++);
        var model = new Model();
        var items = new ObservableCollection<int>();
        var command = new AsyncCommand(() => Task.CompletedTask);
        watcher.Watch(model);
        watcher.Watch(items);
        watcher.Watch(command);
        watcher.Watch("何も通知しない");

        void Changes(Action change, bool expected)
        {
            var before = count;
            change();
            (count > before).Should().Be(expected);
        }
        Changes(model.Change, true);
        Changes(() => items.Add(1), true);
        Changes(command.RaiseCanExecuteChanged, true);

        watcher.Dispose();
        Changes(model.Change, false);
        Changes(() => items.Add(2), false);
        Changes(command.RaiseCanExecuteChanged, false);
    }

    [Fact]
    public void Sync_Should_Watch_Only_The_Current_Rows()
    {
        var count = 0;
        var watcher = new Watcher(() => count++);
        var first = new Model();
        var second = new Model();
        var third = new Model();
        watcher.Sync([first, second]);
        watcher.Sync([second]);
        watcher.RowCount.Should().Be(1);
        watcher.Sync([second, third]);
        watcher.Sync([third]);
        watcher.Sync([]);
        watcher.Sync([second]);
        watcher.RowCount.Should().Be(1);

        first.Change();
        count.Should().Be(0);
        second.Change();
        count.Should().Be(1);

        watcher.Dispose();
        second.Change();
        count.Should().Be(1);
        watcher.RowCount.Should().Be(0);
    }
}

public sealed class JsModuleTests
{
    private sealed class GoneModule : IJSObjectReference
    {
        public ValueTask DisposeAsync() => throw new JSDisconnectedException("画面を閉じました。");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new JSDisconnectedException("画面を閉じました。");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new JSDisconnectedException("画面を閉じました。");
    }

    [Fact]
    public async Task Calls_After_The_Page_Closed_Should_Be_Ignored()
    {
        var module = Task.FromResult<IJSObjectReference>(new GoneModule());

        await JsModule.InvokeQuietlyAsync(module, "setValue", "x");
        await JsModule.DisposeQuietlyAsync(module);
    }
}
