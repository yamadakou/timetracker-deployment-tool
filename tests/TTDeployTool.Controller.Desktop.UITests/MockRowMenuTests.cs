using System.Drawing;
using FluentAssertions;
using Xunit;
using static TTDeployTool.Controller.Desktop.UITests.MockUiTests;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// 行の右クリックのメニュー・デプロイの一覧の行のダブルクリック・詳細の範囲選択とコピー・スライダーの最低スペック（No.89。Blazor 版の
/// desktop-blazor-hybrid.md の 16.14・16.16 と同じ仕様）のモックの UI テスト。メニューの項目と押せるかの判断は Core の RowMenuTests・ModernDesignTests で確かめている。
/// ここでは、Core の項目が画面のメニューに出ること・押すと Core の操作につながることを見る。
/// </summary>
public class MockRowMenuTests
{
    private const string Rows = "Deployments.Rows";

    private static Point Middle(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static void OpenDeployments(AppSession app)
    {
        SignIn(app);
        Load(app);
        app.Select("Deployments.PageSize", "50");
        // 件数・料金のカードと操作の帯の下にある表を、画面の上の方に出す（セルをマウスで押すため）
        app.ScrollToTop("Deployments.Text");
    }

    private static void RightClick(AppSession app, string table, string cellId, string text) =>
        AppSession.Click(Middle(Cell(app.RowWith(table, cellId, text), cellId).BoundingRectangle), right: true);

    [Fact]
    public void The_Deployment_Row_Menu_Should_Show_The_Core_Items_And_Open_The_Detail() => WithFixture(app =>
    {
        OpenDeployments(app);

        // 1 行の右クリック: コピーの後に、Core の項目（元に戻せない削除まで）を出す。「詳細を開く」でその行だけを選んで詳細を出す
        RightClick(app, Rows, "Row.AppName", "app05");
        app.FindAnywhere("Menu.Row.Delete").IsEnabled.Should().BeTrue();
        app.FindAnywhere("Menu.Row.EditBatch");
        app.FindAnywhere("Menu.Row.Detail").Patterns.Invoke.Pattern.Invoke();
        app.WaitForText("Deployments.Selection", t => t == "1 件を選んでいます。", "右クリックした行を選ぶ");
        // 詳細の枠（Border）は UI Automation に出ないため、詳細の中の操作で見る
        app.Find("Row.Extend7");

        // 2 行を選んで右クリック: まとめての項目になる
        app.SelectRowWith(Rows, "Row.AppName", "app06");
        app.WaitForText("Deployments.Selection", t => t == "2 件を選んでいます。", "2 行を選ぶ");
        RightClick(app, Rows, "Row.AppName", "app05");
        app.FindAnywhere("Menu.Rows.Extend7").Name.Should().Be("選んだ 2 件の期限を 7 日延ばす");
        // メニューの項目は画面のコードが作る（AutomationId は XAML に無い）ため、ウィンドウから直接探す
        app.Window.FindFirstDescendant(cf => cf.ByAutomationId("Menu.Row.Detail")).Should().BeNull("1 行の項目は出さない");
        AppSession.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);

        // 行のダブルクリック: その行だけを選んで、右に詳細を出す
        AppSession.Click(Middle(Cell(app.RowWith(Rows, "Row.AppName", "app01"), "Row.AppName").BoundingRectangle), twice: true);
        app.WaitForText("Deployments.Selection", t => t == "1 件を選んでいます。", "ダブルクリックした行だけを選ぶ");
        app.WaitForText("Row.DaysLeft", t => t.StartsWith("あと ", StringComparison.Ordinal), "app01 の詳細");

        // 詳細は表の下（小さい画面では画面の外）にあるため、上の方に出してから押す（画面の外の要素は位置が空で、押すとウィンドウの左上になる）
        app.ScrollToTop("Row.DaysLeft");
        // 詳細の右クリック →「詳細をコピー」（パスワードは含めない）
        AppSession.Click(Middle(app.Find("Row.DaysLeft").BoundingRectangle), right: true);
        app.FindAnywhere("Detail.CopySelection").IsEnabled.Should().BeFalse("文字を選んでいなければ押せない");
        app.FindAnywhere("Detail.CopyAll").Patterns.Invoke.Pattern.Invoke();
        AppSession.WaitUntil(() => AppSession.Clipboard().StartsWith("アプリ名: app01", StringComparison.Ordinal), "詳細がコピーされる");
        AppSession.Clipboard().Should().Contain("リソースグループ: rg-mock-01").And.NotContain("パスワード");

        // 詳細の文字をダブルクリックで選び（範囲選択できる）、「選んだ文字をコピー」
        // 文字の欄は横いっぱいに伸びるが、文字は左にしか無いため、左の端の「あと」を押す（真ん中は文字の無い所で、何も選べない）
        var days = app.Find("Row.DaysLeft").BoundingRectangle;
        var word = new Point(days.X + 12, days.Y + days.Height / 2);
        AppSession.Click(word, twice: true);
        AppSession.Click(word, right: true);
        var selected = app.FindAnywhere("Detail.CopySelection");
        AppSession.WaitUntil(() => selected.IsEnabled, "選んだ文字があれば押せる");
        selected.Patterns.Invoke.Pattern.Invoke();
        AppSession.WaitUntil(() => AppSession.Clipboard() is { Length: > 0 } t && !t.StartsWith("アプリ名", StringComparison.Ordinal), "選んだ文字がコピーされる");
        app.TextOf("Row.DaysLeft").Should().Contain(AppSession.Clipboard().Trim());
    });

    [Fact]
    public void The_Job_Row_Menu_Should_Allow_Only_The_Detail_Before_Signing_In() => WithFixture(app =>
    {
        app.Show("Jobs");
        app.WaitForText("Jobs.Count", t => t.Contains($"全 {Records} 件"), "記録の 45 件");
        app.ScrollToTop("Jobs.Text");
        // 画面に出ている、いちばん新しい記録の行を右クリックする
        var newest = $"rg-mock-{100 + Records - 1} / app{100 + Records - 1}";
        // 「対象」の列は画面の右の外にあるため、左の「種類」の列を押す
        AppSession.Click(Middle(Cell(app.RowWith("Jobs.List", "Job.Target", newest), "Job.Kind").BoundingRectangle), right: true);
        app.FindAnywhere("Menu.Job.Detail").IsEnabled.Should().BeTrue("詳細はサインインの前も押せる");
        app.FindAnywhere("Menu.Jobs.DeleteRecords").IsEnabled.Should().BeFalse("サインインするまで押せない");
        app.FindAnywhere("Menu.Job.Detail").Patterns.Invoke.Pattern.Invoke();
        app.Find("Panel.Status");
    });

    [Fact]
    public void The_Spec_Slider_Should_Mark_Specs_Below_The_Sample_Db_Minimum() => WithFixture(app =>
    {
        MockCostTests.OpenSpec(app);
        // 赤の帯（Rectangle）は UI Automation に出ないため、帯の幅は Core の ModernDesignTests（BelowMinimumShare）で確かめ、見た目は実機で見る（desktop-app-check.md の 4.4 の 10）
        app.TextOf("Settings.TtSpecMinimum").Should().Be("サンプル DB を作れる実用上の最低スペック: 1.25 vCPU / 2.5Gi", "既定の 1.5 vCPU は下回らない");

        // 1.0 vCPU（段階 3）は下回る。1.25 vCPU（段階 4。最低ちょうど）は下回らない
        app.Find("Settings.TtSpec").Patterns.RangeValue.Pattern.SetValue(3);
        app.WaitForText("Settings.TtSpecMinimum", t => t.Contains("を下回っています"), "下回ったら注意に変える");
        app.Find("Settings.TtSpec").Patterns.RangeValue.Pattern.SetValue(4);
        app.WaitForText("Settings.TtSpecMinimum", t => t.StartsWith("サンプル DB を作れる実用上の最低スペック: ", StringComparison.Ordinal), "最低ちょうどは注意にしない");
    });
}
