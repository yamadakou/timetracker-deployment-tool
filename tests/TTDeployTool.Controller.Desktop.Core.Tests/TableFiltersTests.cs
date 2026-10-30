using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>表の絞り込み（見出しの ▽・表の上の文字と日付のフィルタ）と、列の幅と順序の保存（docs/design/desktop/desktop-tables.md の 10）。</summary>
public class TableFiltersTests
{
    private sealed record Item(string Name, string Group, string? Note, DateTimeOffset? At)
    {
        public DateTimeOffset? Later => At?.AddDays(10);
    }

    private static readonly DateTimeOffset Day = new(2026, 10, 10, 3, 0, 0, TimeSpan.Zero);

    private static readonly List<Item> Items =
    [
        new("alpha", "Sales", "first", Day),
        new("beta", "sales", null, Day.AddDays(2)),
        new("gamma", "Dev", "third", null),
        new("delta", "Dev", "fourth", Day.AddDays(5)),
    ];

    private static TableFilters<Item> Filters(IEqualityComparer<string>? comparer = null) => new(
    [
        new TableColumn<Item>("Name", "名前", i => i.Name),
        new TableColumn<Item>("Group", "グループ", i => i.Group),
        new TableColumn<Item>("Note", "メモ", i => i.Note ?? "（なし）", i => i.Note),
        new TableColumn<Item>("At", "日付", i => i.At is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd") : "記録なし", date: i => i.At),
        new TableColumn<Item>("Later", "後の日付", i => "-", date: i => i.Later),
    ], () => Items, comparer);

    private static string Names(TableFilters<Item> f) => string.Join(",", Items.Where(f.Matches).Select(i => i.Name));

    [Fact]
    public void The_Column_Values_Should_Count_Rows_And_Follow_The_Other_Filters()
    {
        var f = Filters(StringComparer.OrdinalIgnoreCase);
        var changes = 0;
        f.Changed += () => changes++;

        f.ValuesOf("Group").Should().Equal(new FilterValue("Dev", 2, true), new FilterValue("Sales", 2, true));
        f.ValuesOf("Note", "TH").Select(v => v.Value).Should().Equal("fourth", "third");
        (f.IsFiltered("Group"), f.IsActive, f.ClearCommand.CanExecute(null)).Should().Be((false, false, false));

        f.SetValues("Group", ["SALES"]);
        (Names(f), f.IsFiltered("Group"), f.IsActive, changes).Should().Be(("alpha,beta", true, true, 1));
        f.ValuesOf("Group").Should().Equal(new FilterValue("Dev", 2, false), new FilterValue("Sales", 2, true));
        f.ValuesOf("Name").Select(v => v.Value).Should().Equal(["alpha", "beta"], "ほかの列の絞り込みに合う行の値だけ");

        f.SetValues("Name", []);
        Names(f).Should().BeEmpty("値をひとつも選ばなければ、どの行も出さない");
        f.ClearValues("Name");
        f.ClearValues("Name");
        changes.Should().Be(3, "絞り込んでいない列を外しても変わらない");
        f.ClearValues(null);
        (Names(f), f.IsActive, changes).Should().Be(("alpha,beta,gamma,delta", false, 4));
        f.ClearValues(null);
        changes.Should().Be(4);
    }

    [Fact]
    public void The_Text_Filter_Should_Look_In_One_Column_Or_All()
    {
        var f = Filters();
        f.TextTargets.Select(t => t.ToString()).Should().Equal("すべての列", "名前", "グループ", "メモ");
        f.TextTarget.Column.Should().BeNull();

        f.Text = " TH ";
        (f.Text, Names(f)).Should().Be(("TH", "gamma,delta"));
        f.Text = "a";
        Names(f).Should().Be("alpha,beta,gamma,delta");
        var raised = 0;
        f.Changed += () => raised++;
        f.TextTarget = f.TextTargets[3];
        (Names(f), raised).Should().Be(("", 1), "空のメモは、見えている文字（空）で探す");
        f.TextTarget = f.TextTargets[1];
        Names(f).Should().Be("alpha,beta,gamma,delta");

        var changes = 0;
        f.Changed += () => changes++;
        f.TextTarget = f.TextTargets[1];
        f.Text = "";
        f.TextTarget = f.TextTargets[2];
        changes.Should().Be(1, "文字が無ければ、対象の列を変えても絞り込みは変わらない");
        f.Column("Group").ToString().Should().Be("グループ");
    }

    [Fact]
    public async Task The_Date_Filter_Should_Use_The_Chosen_Column_And_Clear_Everything()
    {
        var f = Filters();
        f.DateColumns.Select(c => c.Key).Should().Equal("At", "Later");
        var changes = 0;
        f.Changed += () => changes++;
        f.DateColumn = f.DateColumns[0];
        changes.Should().Be(0);

        f.Dates.From = Day.ToLocalTime().Date.AddDays(1);
        (Names(f), changes).Should().Be(("beta,delta", 1));
        f.Dates.To = Day.ToLocalTime().Date.AddDays(3);
        Names(f).Should().Be("beta");
        f.DateColumn = f.DateColumns[1];
        (Names(f), changes).Should().Be(("", 3), "日付を入れていれば、対象の列を変えると絞り込み直す");
        f.DateColumn = f.DateColumns[0];

        f.Text = "e";
        f.SetValues("Group", ["sales"]);
        f.ClearCommand.CanExecute(null).Should().BeTrue();
        await f.ClearCommand.ExecuteAsync();
        (Names(f), f.Text, f.Dates.IsActive, f.IsActive, f.ClearCommand.CanExecute(null)).Should().Be(("alpha,beta,gamma,delta", "", false, false, false));
        f.Dates.From = Day.ToLocalTime().Date;
        Names(f).Should().Be("alpha,beta,delta", "外した後も、日付のフィルタは使える");
    }

    [Fact]
    public void The_Copy_Should_Be_Tab_Separated_In_The_Given_Column_Order()
    {
        var f = Filters();
        f.CopyText(Items.Take(2), ["Group", "Name", "Note"], headers: false).Should().Be("Sales\talpha\tfirst\r\nsales\tbeta\t", "空のメモは見えている文字（空）");
        f.CopyText([new Item("a\tb", "x\"y", "1\n2", null)], ["Name", "Group", "Note"], headers: true)
            .Should().Be("名前\tグループ\tメモ\r\n\"a\tb\"\t\"x\"\"y\"\t\"1\n2\"", "タブ・改行・引用符を含む値は引用符で囲む");
        f.CopyText([], ["Name"], headers: false).Should().BeEmpty();
    }

    [Fact]
    public void Every_Column_Should_Either_Sort_Or_Be_The_Last_Column()
    {
        // 見出しで並べ替えられる列は、どれも並べ替えで例外にならない（評価版 0.1.71 では、ジョブ・結果の列を押すと落ちた）
        var h = new Harness();
        var deployments = h.Main.Deployments;
        foreach (var key in deployments.Filters.Columns.Select(c => c.Key))
        {
            DeploymentListViewModel.CanSortBy(key).Should().Be(key != DeploymentListViewModel.JobColumn, key);
            if (DeploymentListViewModel.CanSortBy(key))
            {
                deployments.SortBy(key, descending: true);
                deployments.SortColumn.Should().Be(key);
            }
        }
        var jobs = h.Main.JobList;
        foreach (var key in jobs.Filters.Columns.Select(c => c.Key))
        {
            JobListViewModel.CanSortBy(key).Should().Be(key != JobListViewModel.ResultColumn, key);
            if (JobListViewModel.CanSortBy(key))
            {
                jobs.SortBy(key, descending: true);
                jobs.SortDescending.Should().BeTrue(key);
            }
        }
    }

    [Fact]
    public void The_Layout_Should_Be_Saved_And_Merged_With_New_Columns()
    {
        var preferences = new InMemoryAppPreferences();
        var defaults = new TableColumnLayout[] { new("a", 100), new("b", 120), new("c", 140) };
        var layout = new TableLayout("table", defaults, preferences);
        layout.Current.Should().Equal(defaults);
        layout.Defaults.Should().Equal(defaults);

        layout.Save([new("c", 200), new("a", 100), new("b", 120)]);
        new TableLayout("table", defaults, preferences).Current.Should().Equal(new TableColumnLayout("c", 200), new("a", 100), new("b", 120));

        // 新しい版で足した列は初期値の位置に入り、無くなった列・重なった列は捨てる
        preferences.TableLayouts = new Dictionary<string, IReadOnlyList<TableColumnLayout>>
        {
            ["table"] = [new("c", 150), new("old", 90), new("c", 10)],
            ["other"] = [new("x", 1)],
        };
        layout.Current.Should().Equal(new TableColumnLayout("a", 100), new("b", 120), new("c", 150));
        preferences.TableLayouts = new Dictionary<string, IReadOnlyList<TableColumnLayout>> { ["table"] = [new("b", 80)], ["other"] = [new("x", 1)] };
        layout.Current.Should().Equal(new TableColumnLayout("a", 100), new("b", 80), new("c", 140));

        layout.Reset().Should().Equal(defaults);
        preferences.TableLayouts.Keys.Should().Equal(["other"], "初期値と同じなら保存を消す。ほかの表の配置は残す");
    }

    [Fact]
    public void The_Layouts_Should_Name_Every_Column_Of_The_Two_Lists()
    {
        var h = new Harness();
        h.Main.DeploymentsLayout.Defaults.Select(c => c.Key).Should().Equal(h.Main.Deployments.Filters.Columns.Select(c => c.Key));
        h.Main.JobsLayout.Defaults.Select(c => c.Key).Should().Equal(h.Main.JobList.Filters.Columns.Select(c => c.Key));
        new[] { h.Main.DeploymentsLayout, h.Main.JobsLayout }.Should().AllSatisfy(l => l.Defaults.Last().Width.Should().Be(0, "最後の列は残りの幅いっぱい"));
    }

    [Fact]
    public void The_Layout_Should_Survive_A_Restart_In_The_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tt-layout-{Guid.NewGuid():N}", "preferences.json");
        try
        {
            new FilePreferences(path).TableLayouts.Should().BeEmpty();
            new FilePreferences(path) { TableLayouts = new Dictionary<string, IReadOnlyList<TableColumnLayout>> { ["deployments"] = [new("AppName", 180)] } };
            new FilePreferences(path).TableLayouts["deployments"].Should().Equal(new TableColumnLayout("AppName", 180));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
