using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>表の画面の側が使う決まり（余白の幅・セルの文字・コピーの対象・並べ替え・列の配置・入力の待ち合わせ。docs/design/desktop/desktop-tables.md の 10.7）。</summary>
public class TableRowsTests
{
    private sealed record Item(string Name, string Note);

    // 画面の行（ItemsSource の要素）。Core の行を包む
    private sealed record Row(Item Item);

    private static readonly Item A = new("alpha", "x\ty");
    private static readonly Item B = new("beta", "b|c");

    private static TableRows<Item> Rows(Func<Item, string>? note = null) => new(
        new TableFilters<Item>(
        [
            new TableColumn<Item>("Name", "名前", i => i.Name),
            new TableColumn<Item>("Note", "メモ", note ?? (i => i.Note)),
            new TableColumn<Item>("At", "日付", _ => "-", date: _ => null),
        ], () => [A, B]),
        row => (row as Row)?.Item);

    [Theory]
    [InlineData(44, null, 28.0)]
    [InlineData(20, null, 16.0)]
    [InlineData(44, 28.0, null)]
    [InlineData(double.NaN, null, null)]
    [InlineData(double.PositiveInfinity, 16.0, null)]
    public void The_Spacer_Should_Leave_Room_For_The_Scroll_Bar_And_Skip_NaN(double offset, double? current, double? expected) =>
        TableRules.SpacerWidth(offset, current).Should().Be(expected, "NaN の幅は作らない（例外になりアプリが落ちた）。同じ幅なら変えない");

    [Fact]
    public void The_Cell_Text_Should_Never_Throw()
    {
        var rows = Rows();
        rows.CellText("Name", new Row(A)).Should().Be("alpha");
        rows.CellText("Name", "{DisconnectedItem}").Should().BeEmpty("使い回しの途中の行の入れ物の目印");
        rows.CellText("Name", null).Should().BeEmpty();
        rows.CellText("Missing", new Row(A)).Should().BeEmpty("無い列");
        Rows(_ => throw new InvalidOperationException()).CellText("Note", new Row(A)).Should().BeEmpty("文字を作れない");
    }

    [Fact]
    public void The_Copy_Should_Use_The_Selected_Rows_Or_The_Clicked_Row()
    {
        TableRules.Target([new Row(A), new Row(B)], new Row(B)).Should().HaveCount(2, "選んだ行");
        TableRules.Target([], new Row(B)).Should().Equal(new Row(B));
        TableRules.Target([], null).Should().BeEmpty();

        var rows = Rows();
        rows.CopyText([new Row(A), "{DisconnectedItem}", new Row(B)], ["Note", "Name"], headers: true)
            .Should().Be("メモ\t名前\r\n\"x\ty\"\talpha\r\nb|c\tbeta", "行でない値は飛ばす");
        rows.CopyMarkdown([new Row(B)], ["Name", "Note"]).Should().Be("| 名前 | メモ |\n|---|---|\n| beta | b\\|c |");
        rows.CopyMarkdown(["{DisconnectedItem}"], ["Name"]).Should().BeEmpty("行が無ければ見出しだけの表にしない");
    }

    [Fact]
    public void The_Sort_Should_Follow_The_First_Sorted_Column()
    {
        TableRules.SortRequest([("Name", null), ("Note", true)]).Should().Be(("Note", true));
        TableRules.SortRequest([("Name", false)]).Should().Be(("Name", false));
        TableRules.SortRequest([("Name", null)]).Should().BeNull("並べ替えている列が無ければ既定の順");
    }

    [Fact]
    public void The_Layout_Should_Be_Applied_With_The_Spacer_Last()
    {
        static IReadOnlyList<string> Apply(IReadOnlyList<string> current, IReadOnlyList<(int From, int To)> moves)
        {
            var list = current.ToList();
            foreach (var (from, to) in moves)
            {
                var key = list[from];
                list.RemoveAt(from);
                list.Insert(to, key);
            }
            return list;
        }

        string[] current = ["a", "b", "c", ""];
        TableRules.Arrange(current, ["a", "b", "c"], "").Should().BeEmpty("同じ順なら動かさない");
        var moves = TableRules.Arrange(current, ["c", "a", "b"], "");
        Apply(current, moves).Should().Equal("c", "a", "b", "");
        // 見出しのドラッグで、ほかの列が余白の列の右に来たとき
        string[] dragged = ["a", "", "b", "c"];
        Apply(dragged, TableRules.Arrange(dragged, ["a", "b", "c"], "")).Should().Equal("a", "b", "c", "");
        TableRules.Arrange(["b", "a"], ["old", "a", "b"], "").Should().Equal([(1, 0)], "表に無い列は飛ばし、余白の列が無ければ最後に動かさない");

        TableRules.Saved("a", 120).Should().Be(new TableColumnLayout("a", 120));
        TableRules.Saved("a", null).Should().Be(new TableColumnLayout("a", 0), "残りの幅いっぱい");
        TableRules.ToolTip(true, "長い文字").Should().Be("長い文字");
        TableRules.ToolTip(false, "短い").Should().BeNull();
        TableRules.WriteBack("abc ", "abc").Should().BeFalse("前後の空白だけの違いは、入力中のため書き換えない");
        TableRules.WriteBack("abc", "").Should().BeTrue("絞り込みを外したら入力欄を空にする");
    }

    [Fact]
    public void The_Text_Should_Wait_For_The_Composition_And_A_Pause()
    {
        var restarts = 0;
        var applied = new List<string>();
        var input = new TextFilterInput(() => restarts++, applied.Add);
        TextFilterInput.Delay.Should().Be(TimeSpan.FromMilliseconds(400));

        input.Changed();
        input.CompositionStarted();
        input.Changed();
        restarts.Should().Be(2, "打つたびにタイマーを掛け直す");
        input.Elapsed("にほ").Should().BeFalse("変換の間は絞り込まない");
        input.CompositionEnded();
        restarts.Should().Be(3, "変換が終わったら掛け直す");
        input.Elapsed("日本").Should().BeTrue();
        applied.Should().Equal("日本");
    }
}
