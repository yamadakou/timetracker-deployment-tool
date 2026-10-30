using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// モックのデータでの UI テスト（docs/design/desktop/desktop-ui-tests.md の 6.2。CI の desktop-ui ジョブの /p:UiTestMock=true のビルドで実行する）。
/// 起動の前に、アプリのデータのフォルダーにモックのデータ（デプロイ 25 件）と終わったジョブの記録（45 件）を置き、モックでサインインして、
/// 行のある表（並べ替え・絞り込み・ページング・6 行の表示）と、予定・待機中・一時停止・実行中のジョブを確かめる。終わったら、置いたファイルと設定を消す。
/// </summary>
public class MockUiTests
{
    internal const int Deployments = 25;
    internal const int Records = 45;

    [Fact]
    public void The_Deployment_List_Should_Page_Sort_Filter_And_Show_The_Row() => WithFixture(app =>
    {
        SignIn(app);
        Load(app);
        app.RowCount("Deployments.Rows").Should().Be(20, "20 件ずつ");
        app.TextOf("Deployments.Page").Should().Contain("1〜20 件目を表示（全 25 件）・1 / 2 ページ");
        app.Invoke("Deployments.Next");
        app.WaitForText("Deployments.Page", t => t.Contains("21〜25 件目"), "次のページ");
        app.RowCount("Deployments.Rows").Should().Be(5);
        app.Invoke("Deployments.Previous");
        app.WaitForText("Deployments.Page", t => t.Contains("1〜20 件目"), "前のページ");
        app.Select("Deployments.PageSize", "50");
        AppSession.WaitUntil(() => app.RowCount("Deployments.Rows") == Deployments, "50 件ずつにすると 1 ページ");
        app.TextOf("Deployments.Page").Should().Contain("1 / 1 ページ");

        // 列の中身（サブスクリプション・グループ・期限・状態）
        var first = app.RowWith("Deployments.Rows", "Row.AppName", "app00");
        AppSession.TextIn(first, "Row.Group").Should().Be("研修");
        AppSession.TextIn(app.RowWith("Deployments.Rows", "Row.AppName", "app02"), "Row.Group").Should().BeEmpty("グループの無い行のセルは空（▽ の値は「（グループなし）」）");
        AppSession.TextIn(first, "Row.Expiry").Should().Be("期限なし");
        AppSession.TextIn(app.RowWith("Deployments.Rows", "Row.AppName", "app04"), "Row.State")
            .Should().NotBe(AppSession.TextIn(first, "Row.State"), "止めたデプロイの状態は、動いているものと違う");
        app.RowsText("Deployments.Rows").Should().Contain($"{FixtureSamples.SubscriptionNameA}（{FixtureSamples.SubscriptionA}）");

        // 並べ替えと、含む文字で行が減る・外すと戻る（2 ページ目にいても 1 ページ目に戻る）
        app.SortByHeaderThreeTimes("Deployments.Rows", "アプリ名").Last().Should().Be("アプリ名");
        app.Select("Deployments.TextTarget", "リソースグループ");
        app.SetText("Deployments.Text", "rg-mock-0");
        AppSession.WaitUntil(() => app.RowCount("Deployments.Rows") == 10, "rg-mock-00〜09 の 10 行に絞られる");
        app.TextOf("Deployments.Page").Should().Contain("絞り込んだ 10 件 / 全 25 件");
        app.Invoke("Deployments.ClearFilters");
        AppSession.WaitUntil(() => app.RowCount("Deployments.Rows") == Deployments, "外すと戻る");

        // 1 行選ぶと詳細（パスワードはモックのシークレット）、2 行選ぶとまとめての操作。詳細は表の下に出るため、行を絞って表を短くする
        app.SetText("Deployments.Text", "rg-mock-0");
        AppSession.WaitUntil(() => app.RowCount("Deployments.Rows") == 10, "10 行に絞る");
        app.SelectRowWith("Deployments.Rows", "Row.AppName", "app00");
        // 詳細の入れ物（Border）は UI Automation の木に出ないため、詳細の中のボタンで確かめる
        app.Invoke("Row.ShowPasswords");
        app.WaitForText("Row.DbPassword", t => t == "••••••••", "パスワードは伏せ字で出る");
        app.Invoke("Row.TogglePasswords");
        app.WaitForText("Row.DbPassword", t => t == "Db-Pass-00", "表示すると、モックのパスワードが出る");
        // 表は見えている行の分しか行の要素を作らないため、上の方の行（同じサブスクリプション）を選ぶ
        app.SelectRowWith("Deployments.Rows", "Row.AppName", "app02");
        AppSession.WaitUntil(() => !app.Exists("Row.ShowPasswords"), "2 行選ぶと詳細は消える");
        app.IsEnabled("Deployments.ChangeExpiry").Should().BeTrue("まとめて期限を変えられる");
        app.IsEnabled("Deployments.Delete").Should().BeTrue("まとめて削除できる");
    });

    [Fact]
    public void The_Job_List_Should_Have_Every_State_And_Page_Sort_Filter_While_Jobs_Run() => WithFixture(app =>
    {
        SignIn(app);
        Load(app);
        app.Select("Deployments.PageSize", "50");
        AppSession.WaitUntil(() => app.RowCount("Deployments.Rows") == Deployments, "全部の行を出す");

        // 実行中 2 件（同時に実行できる数は 2）・待機中 1 件・予定 2 件（うち 1 件を一時停止）。書き込みは 1 回 120 秒かかるため、テストの間は実行中のまま
        Delete(app, "app00", schedule: false);
        Delete(app, "app02", schedule: false);
        Delete(app, "app04", schedule: false);
        Delete(app, "app06", schedule: true);
        Delete(app, "app08", schedule: true);
        app.Show("Deployments");
        app.WaitForRowText("Deployments.Rows", "Row.AppName", "app00", "Row.Job", t => t.Contains("削除"), "デプロイの一覧の行に、削除のジョブが出る");

        app.Show("Jobs");
        app.WaitForText("Jobs.Count", t => t.Contains($"全 {Records + 5} 件"), "記録 45 件と、作ったジョブ 5 件");
        app.IsEnabled("Jobs.CancelSelected").Should().BeFalse("行を選ぶまで押せない");
        app.Exists("Jobs.SignInNote").Should().BeFalse("サインインした後は、操作できない旨を出さない");

        // ページング（20 件ずつで 3 ページ）
        app.RowCount("Jobs.List").Should().Be(20);
        app.Invoke("Jobs.Next");
        app.Invoke("Jobs.Next");
        app.WaitForText("Jobs.Count", t => t.Contains("41〜50 件目"), "3 ページ目");
        app.RowCount("Jobs.List").Should().Be(10);
        app.Invoke("Jobs.Previous");
        app.WaitForText("Jobs.Count", t => t.Contains("21〜40 件目"), "2 ページ目");
        app.Select("Jobs.PageSize", "50");
        AppSession.WaitUntil(() => app.RowCount("Jobs.List") == Records + 5, "50 件ずつにすると 1 ページ");

        // 予定の 1 件を一時停止し、どの状態の行もそろう
        app.SelectRowWith("Jobs.List", "Job.Target", "rg-mock-08 / app08");
        app.Invoke("Jobs.PauseSelected");
        app.WaitForRowText("Jobs.List", "Job.Target", "rg-mock-08 / app08", "Job.Status", t => t == "一時停止", "予定を一時停止できる");
        app.Invoke("Jobs.ClearSelection");
        AppSession.WaitUntil(() =>
        {
            var states = app.RowsText("Jobs.List");
            return new[] { "実行中", "待機中", "予定（", "一時停止", "完了", "失敗", "取り消し", "停止" }.All(states.Contains);
        }, "予定・待機中・一時停止・実行中・終わった状態の行がそろう");

        // ジョブが動いている間に、並べ替え・絞り込みをしても落ちない
        app.SortByHeaderThreeTimes("Jobs.List", "状態").Last().Should().Be("状態");
        app.Select("Jobs.TextTarget", "リソースグループ / アプリ名");
        app.SetText("Jobs.Text", "rg-mock-0");
        AppSession.WaitUntil(() => app.RowCount("Jobs.List") == 5, "作ったジョブの 5 行に絞られる");
        app.SelectRowWith("Jobs.List", "Job.Target", "rg-mock-04 / app04");
        app.IsEnabled("Jobs.PauseSelected").Should().BeTrue("待機中は一時停止できる");
        app.IsEnabled("Jobs.CancelSelected").Should().BeTrue("待機中は取り消せる");
        app.Invoke("Jobs.ClearSelection");
        app.SelectRowWith("Jobs.List", "Job.Target", "rg-mock-08 / app08");
        app.IsEnabled("Jobs.ResumeSelected").Should().BeTrue("一時停止は再開できる");
        app.Invoke("Jobs.ClearSelection");
        app.Invoke("Jobs.ClearFilters");
        AppSession.WaitUntil(() => app.RowCount("Jobs.List") == Records + 5, "外すと戻る");
        app.IsWindowShown.Should().BeTrue("表の部品の中の例外で落ちていない");
    });

    [Fact]
    public void Long_Text_Should_Stop_At_Six_Lines_With_A_Tool_Tip() => WithFixture(app =>
    {
        app.Show("Jobs");
        app.WaitForText("Jobs.Count", t => t.Contains($"全 {Records} 件"), "記録の 45 件");
        app.Select("Jobs.PageSize", "50");
        AppSession.WaitUntil(() => app.RowCount("Jobs.List") == Records, "50 件ずつにすると 1 ページ");
        // いちばん新しい 2 件の結果（誤り）は長い文字。1 行の文字（種類）の高さと比べる。行は作り直されるため、大きさが出るまで探し直す
        // （結果の列は、保存した列の配置〔WithFixture〕で 2 番目に置く。画面の外の列のセルは大きさが 0 になる）
        var target = $"rg-mock-{100 + Records - 1} / app{100 + Records - 1}";
        AppSession.WaitUntil(() =>
        {
            var found = app.RowWith("Jobs.List", "Job.Target", target);
            AppSession.ScrollIntoView(found);
            return Cell(found, "Job.Kind").BoundingRectangle.Height > 0;
        }, "長い文字の行が画面に出る");
        var row = app.RowWith("Jobs.List", "Job.Target", target);
        var result = Cell(row, "Job.Result");
        var line = Cell(row, "Job.Kind").BoundingRectangle.Height;
        var height = result.BoundingRectangle.Height;
        (height / (double)line).Should().BeInRange(5, 6.6, $"6 行で切れる（高さ {height}・1 行 {line}）");
        (result.Properties.HelpText.ValueOrDefault ?? "").Should().Be(FixtureSamples.LongText, "切れたセルは、マウスを置くと全体が出る（同じ文字を UI Automation の説明にも入れる）");
        var shortRow = app.RowWith("Jobs.List", "Job.Target", $"rg-mock-{100 + Records - 4} / app{100 + Records - 4}");
        AppSession.ScrollIntoView(shortRow);
        Cell(shortRow, "Job.Result").BoundingRectangle.Height.Should().BeLessThan(line * 2, "短い文字は 1 行");
        (Cell(shortRow, "Job.Result").Properties.HelpText.ValueOrDefault ?? "").Should().BeEmpty("切れていなければツールチップは出さない");
    });

    internal static AutomationElement Cell(AutomationElement row, string id) =>
        row.FindFirstDescendant(cf => cf.ByAutomationId(id)) ?? throw new InvalidOperationException($"{id} が行にありません。");

    internal static void SignIn(AppSession app)
    {
        app.Show("Target");
        app.Invoke("SignIn.SignIn");
        app.WaitForText("SignIn.Account", t => t.Contains("mock-user@example.com"), "モックのアカウントでサインインする");
    }

    internal static void Load(AppSession app)
    {
        app.Show("Deployments");
        app.Invoke("Deployments.Load");
        app.WaitForText("Deployments.Page", t => t.Contains($"全 {Deployments} 件"), "モックのデプロイが一覧に出る");
    }

    // デプロイの一覧の行から削除の画面を開き、今すぐ削除するか、予定に追加する（予定は今から 1 時間後）
    internal static void Delete(AppSession app, string appName, bool schedule)
    {
        app.Show("Deployments");
        if (app.IsEnabled("Deployments.ClearSelection"))
            app.Invoke("Deployments.ClearSelection");
        app.SelectRowWith("Deployments.Rows", "Row.AppName", appName);
        app.Invoke("Deployments.Delete");
        app.Find("Panel.Delete");
        app.Find("Delete.ConfirmAppName");
        AppSession.WaitUntil(() => app.IsEnabled("Delete.Plan"), "削除の計画ができる");
        app.SetText("Delete.ConfirmAppName", appName);
        if (schedule)
        {
            app.Invoke("Delete.Schedule");
            app.Find("Panel.Jobs");
        }
        else
        {
            app.Invoke("Delete.Start");
            app.Find("Panel.Status");
        }
    }

    /// <summary>CI のウィンドウは狭いため、最大化して表の列と詳細を画面に入れる（画面の外のセルは、UI Automation の位置が 0 になる）。</summary>
    internal static void Maximize(AppSession app)
    {
        if (app.Window.Patterns.Window.TryGetPattern(out var window))
            window.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
    }

    /// <summary>アプリが書いた、外の機能の記録（通知・開いたフォルダーと URL・サインイン時の起動。desktop-ui-tests.md の 6.3）。</summary>
    internal static IReadOnlyList<(string Kind, string Text)> Events() => FixtureEvents.Read(Path.Combine(AppSession.DataDirectory(), FixtureEvents.FileName));

    /// <summary>
    /// モックのデータ（<paramref name="data"/> で変えられる）と終わったジョブの記録を置いて起動し、終わったら（アプリを閉じた後に）置いたファイル・記録・設定を消す。
    /// <paramref name="layout"/> なら、保存した列の配置（ジョブの一覧。既定は結果の列を左に。<paramref name="jobsLayout"/> で変えられる）も置く。
    /// </summary>
    internal static void WithFixture(Action<AppSession> test, Func<FixtureData, FixtureData>? data = null, bool layout = true, IReadOnlyList<TableColumnLayout>? jobsLayout = null,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var directory = AppSession.DataDirectory();
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var files = new[] { FixtureData.FileName, "job-history.json", "preferences.json", FixtureEvents.FileName }.Select(f => Path.Combine(directory, f)).ToList();
        foreach (var file in files)
            File.Delete(file);
        var fixture = FixtureSamples.Data(Deployments, now) with { WriteDelaySeconds = 120 };
        File.WriteAllText(files[0], (data?.Invoke(fixture) ?? fixture).ToJson());
        new FileJobHistory(files[1]).Records = FixtureSamples.JobRecords(Records, now);
        // 保存した列の配置を当てる（ジョブの一覧の結果の列を、種類の次に置く。CI のウィンドウは狭く、右端の列は画面の外になるため）
        if (layout)
            new FilePreferences(files[2]).TableLayouts = new Dictionary<string, IReadOnlyList<TableColumnLayout>>
        {
            ["jobs"] = jobsLayout ??
            [
                new("Kind", 110), new("Result", 320), new("Subscription", 200), new("Target", 200), new("State", 170), new("Time", 120), new("RegisteredAt", 120),
                new("ChangedAt", 120), new("StartedAt", 120), new("Finished", 120), new("Duration", 0),
            ],
        };
        try
        {
            AppSession.Run(app =>
            {
                Maximize(app);
                test(app);
            }, name);
        }
        finally
        {
            // 実行中のジョブがあると、閉じても通知領域に残るため、AppSession が終了させる。予定は preferences.json に残るため、設定ごと消す
            foreach (var file in files)
                File.Delete(file);
        }
    }
}
