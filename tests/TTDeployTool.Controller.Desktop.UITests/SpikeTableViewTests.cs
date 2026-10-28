using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
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
        string Tree() => string.Join("\n", table.FindAllDescendants().Select(e =>
            $"{e.Properties.ControlType.ValueOrDefault} id={e.Properties.AutomationId.ValueOrDefault} name={e.Properties.Name.ValueOrDefault}"));
        List<string> Names() => table.FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault ?? "").ToList();

        var before = Tree();
        var names = Names();
        Check(new[] { "demo", "研修", "完了", "other", "営業", "失敗" }.All(names.Contains), "セルの文字が読める");
        Check(new[] { "アプリ名", "グループ", "結果" }.All(names.Contains), "列見出しの名前が読める（どの要素でも）");
        var headers = table.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem));
        Check(headers.Length >= 3, $"列見出しが HeaderItem として見える（{headers.Length} 個: {string.Join(", ", headers.Select(h => h.Properties.Name.ValueOrDefault))}）");

        var everywhere = app.Window.FindAllDescendants().Where(e => (e.Properties.Name.ValueOrDefault ?? "") is "アプリ名" or "グループ")
            .Select(e => $"{e.Properties.ControlType.ValueOrDefault} name={e.Properties.Name.ValueOrDefault}").ToList();
        Check(everywhere.Count >= 2, $"列見出しの文字がウィンドウのどこかに見える（{string.Join(", ", everywhere)}）");
        var custom = app.Window.FindFirstDescendant(cf => cf.ByAutomationId("Spike.ResultHeader"));
        Check(custom != null, $"見出しに置いた要素の AutomationId で見つかる（{custom?.Properties.ControlType.ValueOrDefault} {custom?.Parent?.Properties.ControlType.ValueOrDefault}）");

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
        Check(!after.Contains("研修") && !after.Contains("グループ"), "列を隠すと、その列の見出しとセルが消える");

        Console.WriteLine(string.Join("\n", report));
        problems.Should().BeEmpty($"\n{string.Join("\n", report)}\n==== 隠す前の UIA の木\n{before}\n==== 隠した後の名前\n{string.Join(" | ", after)}");
    });
}
