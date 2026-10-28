using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3.Converters;
using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>試作（No.78）: WinUI.TableView を UI Automation で読めるか、列を隠せるか、選べるか。確かめたことをすべて集め、1 つでも違えば UIA の木と一緒に失敗として出す。</summary>
public class SpikeTableViewTests
{
    [Fact]
    public void The_Table_Should_Be_Readable_By_UI_Automation() => AppSession.Run(app =>
    {
        app.Show("DryRun");
        var table = app.Find("Spike.Table");
        var report = new List<string>();
        var problems = new List<string>();
        void Check(bool ok, string what)
        {
            report.Add($"{(ok ? "OK" : "NG")}: {what}");
            if (!ok)
                problems.Add(what);
        }
        string Describe(AutomationElement e) =>
            $"{e.Properties.ControlType.ValueOrDefault} class={e.Properties.ClassName.ValueOrDefault} name={e.Properties.Name.ValueOrDefault} help={e.Properties.HelpText.ValueOrDefault} focusable={e.Properties.IsKeyboardFocusable.ValueOrDefault}";
        // ColumnHeaders・ColumnHeaderItems のプロパティは XAML の UIA の橋渡しで「対応しない」になるため、パターンのメソッドで直接読む
        AutomationElement[] ColumnHeaders(AutomationElement grid)
        {
            try
            {
                var native = ((Interop.UIAutomationClient.IUIAutomationTablePattern)AutomationElementConverter.ToNative(grid).GetCurrentPattern(10012)).GetCurrentColumnHeaders();
                return AutomationElementConverter.NativeArrayToManaged(grid.Automation, native);
            }
            catch (Exception e)
            {
                report.Add($"ColumnHeaders の読み取りに失敗: {e.GetType().Name}: {e.Message}");
                return [];
            }
        }
        AutomationElement[] HeaderItems(AutomationElement cell)
        {
            try
            {
                var native = ((Interop.UIAutomationClient.IUIAutomationTableItemPattern)AutomationElementConverter.ToNative(cell).GetCurrentPattern(10013)).GetCurrentColumnHeaderItems();
                return AutomationElementConverter.NativeArrayToManaged(cell.Automation, native);
            }
            catch (Exception e)
            {
                report.Add($"ColumnHeaderItems の読み取りに失敗: {e.GetType().Name}: {e.Message}");
                return [];
            }
        }
        List<string> Names() => table.FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault ?? "").ToList();

        Check(table.Properties.ControlType.ValueOrDefault == ControlType.DataGrid, $"表が DataGrid として見える（{table.Properties.ControlType.ValueOrDefault}）");
        var names = Names();
        Check(new[] { "demo", "研修", "完了", "other", "営業", "失敗" }.All(names.Contains), "セルの文字が読める");

        // 列の見出しは、表の子ではなく Table パターンの ColumnHeaders で出す作り（WinUI.TableView の TableViewAutomationPeer.GetColumnHeaders）
        var tablePattern = table.Patterns.Table.IsSupported;
        Check(tablePattern && table.Patterns.Grid.IsSupported, "表が Table・Grid パターンに対応する");
        var headers = tablePattern ? ColumnHeaders(table) : Array.Empty<AutomationElement>();
        Check(headers.Select(h => h.Properties.ControlType.ValueOrDefault).All(t => t == ControlType.HeaderItem)
              && headers.Select(h => h.Properties.Name.ValueOrDefault).SequenceEqual(["アプリ名", "グループ", "結果"]),
            $"見出しが Table パターンから HeaderItem として読める（{string.Join(" / ", headers.Select(Describe))}）");
        if (tablePattern)
        {
            var grid = table.Patterns.Grid.Pattern;
            Check((grid.RowCount.Value, grid.ColumnCount.Value) == (2, 3), $"行と列の数（{grid.RowCount.Value} 行・{grid.ColumnCount.Value} 列）");
            var cell = grid.GetItem(0, 1);
            var cellHeaders = cell?.Patterns.TableItem.IsSupported == true ? HeaderItems(cell) : Array.Empty<AutomationElement>();
            Check(cellHeaders.Select(h => h.Properties.Name.ValueOrDefault).SequenceEqual(["グループ"]),
                $"セルから見出しが分かる（{cell?.Properties.Name.ValueOrDefault} → {string.Join(", ", cellHeaders.Select(h => h.Properties.Name.ValueOrDefault))}）");
        }
        // 表から見出しの一覧が取れないときに備え、各列の 1 行目のセルから見出しをたどる
        var viaCells = tablePattern
            ? Enumerable.Range(0, table.Patterns.Grid.Pattern.ColumnCount.Value)
                .Select(c => table.Patterns.Grid.Pattern.GetItem(0, c))
                .SelectMany(c => c?.Patterns.TableItem.IsSupported == true ? HeaderItems(c) : Array.Empty<AutomationElement>()).ToArray()
            : Array.Empty<AutomationElement>();
        Check(viaCells.Select(h => h.Properties.ControlType.ValueOrDefault).All(t => t == ControlType.HeaderItem)
              && viaCells.Select(h => h.Properties.Name.ValueOrDefault).SequenceEqual(["アプリ名", "グループ", "結果"]),
            $"セルからたどった見出しが HeaderItem として読める（{string.Join(" / ", viaCells.Select(Describe))}）");
        var group = headers.Concat(viaCells).FirstOrDefault(h => h.Properties.Name.ValueOrDefault == "グループ");
        Check(app.Window.FindFirstDescendant(cf => cf.ByName("グループ").And(cf.ByControlType(ControlType.HeaderItem))) != null,
            "見出しを名前でツリーから探せる（FindFirstDescendant）");
        if (group != null && group.Patterns.Invoke.IsSupported)
        {
            group.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(500);
            var log = app.Find("Spike.SortLog").Properties.Name.ValueOrDefault;
            Check(log == "sorted:グループ", $"見出しの Invoke で並べ替えの Sorting が起き、アプリ側で処理できる（{log}）");
        }
        else
        {
            Check(false, "見出しが Invoke パターンに対応する");
        }

        var rows = table.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem).Or(cf.ByControlType(ControlType.DataItem)));
        Check(rows.Length == 2, $"行が 2 つ見える（{rows.Length} 個）");
        if (rows.Length > 0 && rows[0].Patterns.SelectionItem.IsSupported)
        {
            rows[0].Patterns.SelectionItem.Pattern.AddToSelection();
            Thread.Sleep(300);
            Check(rows[0].Patterns.SelectionItem.Pattern.IsSelected.Value, "行を UIA で選べる");
        }
        else
        {
            Check(false, "行が SelectionItem に対応する");
        }

        app.Invoke("Spike.HideGroup");
        Thread.Sleep(1000);
        var after = Names();
        var afterHeaders = tablePattern ? ColumnHeaders(table).Select(h => h.Properties.Name.ValueOrDefault ?? "").ToList() : new List<string>();
        Check(!after.Contains("研修") && afterHeaders.SequenceEqual(["アプリ名", "結果"]), $"列を隠すと、その列の見出しとセルが消える（見出し: {string.Join(", ", afterHeaders)}）");

        Console.WriteLine(string.Join("\n", report));
        problems.Should().BeEmpty($"\n{string.Join("\n", report)}");
    });
}
