using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>試作（No.78）: WinUI.TableView を UI Automation で読めるか、列を隠せるか、選べるか。結果は出力に書く。</summary>
public class SpikeTableViewTests
{
    [Fact]
    public void The_Table_Should_Be_Readable_By_UI_Automation() => AppSession.Run(app =>
    {
        app.Show("DryRun");
        var table = app.Find("Spike.Table");
        void Dump(string title)
        {
            Console.WriteLine($"==== {title}");
            foreach (var e in table.FindAllDescendants())
                Console.WriteLine($"{e.Properties.ControlType.ValueOrDefault} id={e.Properties.AutomationId.ValueOrDefault} name={e.Properties.Name.ValueOrDefault}");
        }
        Dump("table");
        var texts = table.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Properties.Name.ValueOrDefault ?? "").ToList();
        Console.WriteLine("texts: " + string.Join(" | ", texts));
        texts.Should().Contain(["アプリ名", "グループ", "結果", "demo", "研修", "完了", "other", "営業", "失敗"]);

        var rows = table.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem).Or(cf.ByControlType(ControlType.DataItem)));
        Console.WriteLine($"rows: {rows.Length}");
        rows.Length.Should().Be(2);
        rows[0].Patterns.SelectionItem.Pattern.AddToSelection();
        rows[0].Patterns.SelectionItem.Pattern.IsSelected.Value.Should().BeTrue();

        app.Invoke("Spike.HideGroup");
        Thread.Sleep(1000);
        var after = table.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Properties.Name.ValueOrDefault ?? "").ToList();
        Console.WriteLine("after hide: " + string.Join(" | ", after));
        after.Should().NotContain("研修").And.NotContain("グループ");
    });
}
