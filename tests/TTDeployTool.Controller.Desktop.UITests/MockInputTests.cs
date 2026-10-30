using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;
using static TTDeployTool.Controller.Desktop.UITests.MockUiTests;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// マウスとキーボードの入力を合成するモックの UI テスト（docs/design/desktop/desktop-ui-tests.md の 6.3）。UI Automation のパターンで操作できない、
/// 見出しのドラッグ・右クリックのメニュー・Ctrl+C・▽ のメニュー・行のダブルクリック・左上のチェックボックス・ウィンドウの幅を確かめる（実機の確認の代わり）。
/// サインインせずに、終わったジョブの記録 45 件のジョブの一覧で確かめる。
/// </summary>
public class MockInputTests
{
    private const string Table = "Jobs.List";

    private static readonly string Newest = $"rg-mock-{100 + Records - 1} / app{100 + Records - 1}";

    private static readonly string Second = $"rg-mock-{100 + Records - 2} / app{100 + Records - 2}";

    private static void OpenJobs(AppSession app)
    {
        app.Show("Jobs");
        app.WaitForText("Jobs.Count", t => t.Contains($"全 {Records} 件"), "記録の 45 件");
    }

    private static Rectangle Header(AppSession app, string name) => app.ColumnHeader(Table, name).BoundingRectangle;

    private static Point Middle(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [Fact]
    public void Columns_Should_Resize_And_Reorder_By_Dragging_And_Survive_A_Restart() => WithFixture(app =>
    {
        OpenJobs(app);
        var kind = Header(app, "種類");
        // 見出しの右の境目をドラッグして広げる（境目の当たり判定は見出しの上の方だけ。下の方は「…」のボタン）
        AppSession.Drag(new Point(kind.Right - 1, kind.Y + 3), new Point(kind.Right + 60, kind.Y + 3));
        AppSession.WaitUntil(() => Header(app, "種類").Width >= kind.Width + 40, "境目のドラッグで列が広がる");
        var widened = Header(app, "種類").Width;
        // 見出しをドラッグして、リソースグループ / アプリ名 の位置に動かす
        AppSession.Drag(Middle(Header(app, "種類")), Middle(Header(app, "リソースグループ / アプリ名")));
        AppSession.WaitUntil(() => Header(app, "種類").X > Header(app, "サブスクリプション").X, "見出しのドラッグで順序が変わる");

        // 起動し直しても同じ（列の配置を保存する）
        app.CloseAndWait().Should().BeTrue("ジョブが無ければ閉じると終わる");
        using var again = AppSession.Launch();
        OpenJobs(again);
        Header(again, "種類").X.Should().BeGreaterThan(Header(again, "サブスクリプション").X, "順序は起動し直しても同じ");
        Header(again, "種類").Width.Should().BeCloseTo(widened, 4, "幅は起動し直しても同じ");
        again.Invoke("Jobs.ResetLayout");
        AppSession.WaitUntil(() => Header(again, "種類").X < Header(again, "サブスクリプション").X, "初期値に戻すと元の順序");
        Header(again, "種類").Width.Should().BeCloseTo(kind.Width, 4, "初期値の幅");
    }, layout: false);

    [Fact]
    public void Cells_Should_Be_Copied_From_The_Menu_And_The_Keyboard() => WithFixture(app =>
    {
        OpenJobs(app);
        // セルを右クリック →「この文字をコピー」
        var row = app.RowWith(Table, "Job.Target", Newest);
        AppSession.Click(Middle(Cell(row, "Job.Target").BoundingRectangle), right: true);
        MenuItem(app, "この文字をコピー").Patterns.Invoke.Pattern.Invoke();
        AppSession.WaitUntil(() => AppSession.Clipboard() == Newest, "セルの文字がコピーされる");

        // 行を 2 つ選び、右クリック →「選んだ行を Markdown 形式でコピー」
        app.SelectRowWith(Table, "Job.Target", Newest);
        app.SelectRowWith(Table, "Job.Target", Second);
        AppSession.Click(Middle(Cell(app.RowWith(Table, "Job.Target", Second), "Job.Kind").BoundingRectangle), right: true);
        MenuItem(app, "選んだ行を Markdown 形式でコピー").Patterns.Invoke.Pattern.Invoke();
        AppSession.WaitUntil(() => AppSession.Clipboard().StartsWith("| 種類 | ", StringComparison.Ordinal), "Markdown の表がコピーされる");
        var markdown = AppSession.Clipboard().Split('\n');
        markdown[1].Should().StartWith("|---|", "見出しの下の区切り");
        markdown.Should().HaveCount(4, "見出し・区切り・選んだ 2 行");

        // Ctrl+C（選んだ行をタブ区切り）、Ctrl+Shift+C（見出しつき）
        app.RowWith(Table, "Job.Target", Second).Focus();
        AppSession.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        AppSession.WaitUntil(() => AppSession.Clipboard().Contains(Newest) && AppSession.Clipboard().Contains('\t'), "Ctrl+C で選んだ行がコピーされる");
        AppSession.Clipboard().Split('\n').Should().HaveCount(2);
        AppSession.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_C);
        AppSession.WaitUntil(() => AppSession.Clipboard().StartsWith("種類\t", StringComparison.Ordinal), "Ctrl+Shift+C で見出しつき");
    }, jobsLayout:
    [
        // リソースグループ / アプリ名 の列を左に置く（CI の画面は狭く、右の列のセルは画面の外になって押せない）
        new("Kind", 110), new("Target", 200), new("Result", 320), new("Subscription", 200), new("State", 170), new("Time", 120), new("RegisteredAt", 120),
        new("ChangedAt", 120), new("StartedAt", 120), new("Finished", 120), new("Duration", 0),
    ]);

    [Fact]
    public void Filter_Menu_Corner_Check_Box_And_Double_Click_Should_Work() => WithFixture(app =>
    {
        OpenJobs(app);
        // 見出しの右の「…」→ 値の一覧の「デプロイ」を外して OK → 削除の 22 件
        var kind = Header(app, "種類");
        AppSession.Click(new Point(kind.Right - 14, kind.Y + kind.Height / 2));
        var ok = app.FindAnywhere("OkButton");
        // 値のチェックボックスには名前が無く、横の文字（値）に名前があるため、同じ高さのチェックボックスを選ぶ（表のセルの「デプロイ」は除く）
        var label = app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("デプロイ")))
            .FirstOrDefault(t => t.BoundingRectangle.Height > 0 && t.Properties.AutomationId.ValueOrDefault != "Job.Kind")
            ?? throw new InvalidOperationException("▽ の値の一覧に「デプロイ」がありません。");
        var deploy = app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
            .OrderBy(c => Math.Abs(Middle(c.BoundingRectangle).Y - Middle(label.BoundingRectangle).Y))
            .First();
        deploy.Patterns.Toggle.Pattern.Toggle();
        ok.Patterns.Invoke.Pattern.Invoke();
        app.WaitForText("Jobs.Count", t => t.Contains($"絞り込んだ {Records / 2} 件 / 全 {Records} 件"), "▽ で削除だけに絞られる");
        app.Invoke("Jobs.ClearFilters");
        app.WaitForText("Jobs.Count", t => t.Contains($"（全 {Records} 件）"), "外すと戻る");

        // 左上のチェックボックスで、今のページ（20 件）の行をすべて選ぶ・外す
        var table = app.Find(Table).BoundingRectangle;
        var corner = new Point(table.X + 20, kind.Y + kind.Height / 2);
        AppSession.Click(corner);
        app.WaitForText("Jobs.Selection", t => t == "20 件を選んでいます。", "左上で今のページの行をすべて選ぶ");
        AppSession.Click(corner);
        app.WaitForText("Jobs.Selection", t => t.StartsWith("行を選ぶと", StringComparison.Ordinal), "もう一度押すと外れる");

        // 行のダブルクリックで実行状況の画面を開き、ジョブの一覧に戻る
        AppSession.Click(Middle(Cell(app.RowWith(Table, "Job.Target", Newest), "Job.Kind").BoundingRectangle), twice: true);
        app.Find("Panel.Status");
        app.Invoke("Status.BackToList");
        app.Find("Panel.Jobs");
    });

    [Fact]
    public void The_Last_Column_Should_Fill_The_Width_And_Stay_Left_Of_The_Scroll_Bar() => WithFixture(app =>
    {
        OpenJobs(app);
        // 左のメニューを畳み、表に幅を空ける
        app.FindAnywhere("TogglePaneButton").Patterns.Invoke.Pattern.Invoke();
        var table = app.Find(Table).BoundingRectangle;
        var last = Header(app, "結果");
        // 最後の列（結果）は残りの幅いっぱいで、その右に余白の列（縦のスクロールバーの分）がある
        last.Width.Should().BeGreaterThanOrEqualTo(320, "最後の列は初期の幅（320）より狭くならない");
        (table.Right - last.Right).Should().BeInRange(8, 60, "右端の列の右に余白があり、スクロールバーにかからない");
    }, jobsLayout:
    [
        // 列を狭くし、最後の列（結果。0 = 残りの幅いっぱい）が残りの幅で広がるようにする（CI の画面は 1024 × 768）
        new("Kind", 50), new("Subscription", 50), new("Target", 50), new("State", 50), new("Time", 50), new("RegisteredAt", 50),
        new("ChangedAt", 50), new("StartedAt", 50), new("Finished", 50), new("Duration", 50), new("Result", 0),
    ]);

    private static AutomationElement MenuItem(AppSession app, string name) =>
        FlaUI.Core.Tools.Retry.WhileNull(
            () => app.Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(name))),
            AppSession.Timeout, TimeSpan.FromMilliseconds(200), throwOnTimeout: true, ignoreException: true,
            timeoutMessage: $"右クリックのメニューに「{name}」がありません。").Result!;
}
