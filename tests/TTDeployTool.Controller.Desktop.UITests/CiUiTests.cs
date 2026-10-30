using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// Azure を使わない操作の UI テスト（詳細設計 5。CI の desktop-ui ジョブで実行する）。サインインはしない。
/// </summary>
public class CiUiTests
{
    // 7f: 削除・実行状況はメニューに無い。No.63: デプロイの一覧・新しくデプロイ・ジョブの一覧はサインインするまで押せない（docs/design/desktop/desktop-wizard.md の 2・7）
    private static readonly string[] Pages = ["Target", "DryRun", "AppSettings"];

    [Fact]
    public void Startup_Should_Open_The_Window_And_Write_The_Diagnostic_Log() => AppSession.Run(app =>
    {
        app.Window.Title.Should().Be("TTDeployTool");

        app.Show("AppSettings");
        var directory = app.TextOf("Settings.LogDirectory");
        Directory.Exists(directory).Should().BeTrue($"ログのフォルダー {directory} があるはず");
        var log = Directory.GetFiles(directory, "ttdeploytool-desktop-*.log").OrderBy(f => f).Last();
        var text = ReadShared(log);
        text.Should().Contain("起動しました");
        text.Should().NotContain("[Critical]");
        app.IsEnabled("Settings.OpenLogFolder").Should().BeTrue();
    });

    [Fact]
    public void Navigation_Should_Show_Only_The_Selected_Page() => AppSession.Run(app =>
    {
        foreach (var page in Pages)
        {
            app.Show(page);
            foreach (var other in Pages.Where(p => p != page))
                app.Exists($"Panel.{other}").Should().BeFalse($"{page} を選んだとき、{other} は表示しない");
        }
    });

    [Fact]
    public void Pages_That_Need_Azure_Should_Wait_For_Sign_In_And_Show_No_Banner() => AppSession.Run(app =>
    {
        // No.63: サインインしていないときは、デプロイの一覧・新しくデプロイは押せない（ウィザードの誤りの表示は Azure の UI テストで確かめる）。
        // No.78: ジョブの一覧は、過去の記録を見るため、サインインしていなくても開ける（ジョブの操作は押せない）
        foreach (var page in new[] { "Nav.Deployments", "Nav.Wizard" })
            app.IsEnabled(page).Should().BeFalse($"サインインするまで {page} は押せない");
        app.IsEnabled("Nav.Jobs").Should().BeTrue("ジョブの一覧は、サインインしていなくても開ける");
        app.Exists("Jobs.Banner").Should().BeFalse("ジョブが無ければ上部の帯を出さない");
        app.Exists("Job.Title").Should().BeFalse();
        app.Exists("Job.Progress").Should().BeFalse();
    });

    [Fact]
    public void Menu_Should_Have_No_Delete_Or_Status_And_Settings_At_The_Bottom() => AppSession.Run(app =>
    {
        foreach (var removed in new[] { "Nav.Settings", "Nav.Deploy", "Nav.Status", "Nav.Delete" })
            app.Exists(removed).Should().BeFalse($"{removed} はメニューに無い");
        app.TextOf("Nav.Jobs").Should().Contain("ジョブの一覧");
        app.Show("AppSettings");
        app.TextOf("Settings.StartupStatus").Should().NotBeEmpty();
        // Windows との連携（No.67）: 前回のサインイン時の起動・通知の許可・閉じたときの動き・予定
        app.TextOf("Settings.LastStartup").Should().StartWith("Windows のサインイン時に起動したことは、まだありません");
        app.TextOf("Settings.NotificationStatus").Should().NotBeEmpty();
        app.TextOf("Settings.CloseBehavior").Should().StartWith("今ウィンドウを閉じると、アプリを終了します");
        app.TextOf("Settings.ScheduledJobs").Should().Be("まだ始めていないジョブ（予定・待機中・一時停止）はありません。");
        app.Invoke("Settings.RefreshSystem");
        app.TextOf("Settings.CloseBehavior").Should().StartWith("今ウィンドウを閉じると、アプリを終了します");
    });

    [Fact]
    public void Concurrency_Should_Be_Saved_Across_Restarts()
    {
        AppSession.Run(app =>
        {
            app.Show("AppSettings");
            app.ItemsOf("Settings.Concurrency").Should().Equal("1", "2", "3", "4");
            app.SelectedOf("Settings.Concurrency").Should().Be("2", "既定は 2");
            app.Select("Settings.Concurrency", "3");
        }, "Concurrency_Should_Be_Saved_Across_Restarts_1");
        AppSession.Run(app =>
        {
            app.Show("AppSettings");
            app.SelectedOf("Settings.Concurrency").Should().Be("3", "アプリのデータに保存する");
            // ほかのテストのために既定に戻す
            app.Select("Settings.Concurrency", "2");
        }, "Concurrency_Should_Be_Saved_Across_Restarts_2");
    }

    [Fact]
    public void Signed_Out_State_Should_Allow_Only_Sign_In() => AppSession.Run(app =>
    {
        app.TextOf("SignIn.Account").Should().StartWith("サインインしていません");
        app.IsEnabled("SignIn.SignIn").Should().BeTrue();
        app.IsEnabled("SignIn.SignOut").Should().BeFalse();
        // 7f: サブスクリプションと対象の入力は、ウィザードの ① にある（No.63: ウィザードはサインインするまで開けない）
        app.Exists("Target.Subscription").Should().BeFalse("サインインの画面には、サブスクリプションの選択を出さない");
        app.IsEnabled("Nav.Wizard").Should().BeFalse();
    });

    [Fact]
    public void Dry_Run_Should_Not_Save_With_Invalid_Settings() => AppSession.Run(app =>
    {
        // パスワードを入れていない
        app.Show("DryRun");
        app.Invoke("DryRun.Save");

        app.WaitForText("DryRun.Error", t => t.Contains("設定に誤りがあるため、ファイルを保存しませんでした"), "誤りが表示される");
        app.Exists("DryRun.Compose").Should().BeFalse();
    });

    [Fact]
    public void Dry_Run_Should_Save_The_Files_In_The_Chosen_Folder() => AppSession.Run(app =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "ttdeploytool-uitest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // まず誤った設定（パスワードなし）で保存し、エラーを出す
            app.Show("DryRun");
            app.Invoke("DryRun.Save");
            app.Find("DryRun.Error");

            // 7f: DB とパスワードは、この画面でも入れられる（ウィザードと同じ値）。コンボボックスの選択は、選択の変更イベントで ViewModel に戻す
            app.Select("DryRun.DbType", "sqlserver");
            app.SetText("DryRun.DbPassword", "UiTestDbPass1!");
            app.SetText("DryRun.TrackerPassword", "UiTestTracker1!");
            app.Invoke("DryRun.Save");
            app.PickFolder(folder);

            app.WaitForText("DryRun.Compose", t => t.Contains("11.0.1-linux-mssql"), "SQL Server のイメージの docker-compose.yml が表示される");
            File.Exists(Path.Combine(folder, "docker-compose.yml")).Should().BeTrue();
            File.ReadAllText(Path.Combine(folder, ".env")).Should().Contain("UiTestDbPass1!");
            app.ScreenText().Should().Contain("docker compose up -d");
            app.IsEnabled("DryRun.OpenFolder").Should().BeTrue();
            // 保存できたら、前のエラーの表示（領域ごと）が消える（実機の確認の P1）
            app.Exists("DryRun.Error").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void Closing_Without_Jobs_Should_Not_Ask() => AppSession.Run(app =>
    {
        app.CloseAndWait().Should().BeTrue("終わっていないジョブが無ければ、確認なしで終了する（通知領域に残らない）");
    });

    [Fact]
    public void A_Second_Launch_Should_Bring_The_Running_App_Forward() => AppSession.Run(app =>
    {
        app.Show("AppSettings");
        app.TextOf("Settings.Resident").Should().Contain("通知領域に残って続けます");

        // 2 つ目の起動は 1 つ目に渡されて終わり、アプリのプロセスは 1 つのまま
        using var second = FlaUI.Core.Application.LaunchStoreApp(AppSession.AppUserModelId());
        AppSession.TryWait(() => System.Diagnostics.Process.GetProcessesByName("ttdeploytool-desktop").Length == 1, TimeSpan.FromSeconds(30))
            .Should().BeTrue("2 つ目は起動を渡して終わる");
        app.App.HasExited.Should().BeFalse();
        app.Exists("Panel.AppSettings").Should().BeTrue("1 つ目のウィンドウはそのまま");
    });

    // アプリが書き込み中のログを読む
    /// <summary>
    /// No.78: 行のある表（ジョブの一覧）。終わったジョブの記録（job-history.json）を置いて起動し、サインインせずに、並べ替え・絞り込みで行が減る・
    /// 絞り込みを外す・列の配置を初期値に戻す、を確かめる（表の部品の中の例外でアプリが落ちないこと。Desktop System Test の 6〜9 回目に落ちた場面を、
    /// Azure を使わずに通す。docs/design/desktop/desktop-tables.md の 10.7）。
    /// </summary>
    [Fact]
    public void Job_Records_Should_Fill_The_Table_And_Survive_Sorting_And_Filtering()
    {
        var path = Path.Combine(AppSession.DataDirectory(), "job-history.json");
        File.WriteAllText(path, JobRecords(DateTimeOffset.UtcNow));
        try
        {
            AppSession.Run(app =>
            {
                app.Show("Jobs");
                app.WaitForText("Jobs.Count", t => t.Contains("全 3 件"), "記録の 3 件が一覧に出る");
                app.Exists("Jobs.SignInNote").Should().BeTrue("サインインするまで、ジョブの操作はできない旨を出す");
                app.RowCount("Jobs.List").Should().Be(3);
                app.WaitForRowText("Jobs.List", "Job.Target", "rg-ci-b / app-b", "Job.Status", t => t.StartsWith("失敗"), "失敗した記録の行");

                // 並べ替え（Core の並べ替え）と、並べ替えない列
                app.SortByHeaderThreeTimes("Jobs.List", "状態").Last().Should().Be("状態", "3 回目で既定の順に戻る");
                app.ColumnHeader("Jobs.List", "結果").Patterns.Invoke.IsSupported.Should().BeFalse("結果の列は並べ替えない");

                // 含む文字で行が減る（行を減らすと、表はチェックボックスの列の幅を一時的に NaN にする）・外すと戻る
                app.Select("Jobs.TextTarget", "リソースグループ / アプリ名");
                app.SetText("Jobs.Text", "rg-ci-b");
                AppSession.WaitUntil(() => app.RowCount("Jobs.List") == 1, "含む文字で 1 行に絞られる");
                app.TextOf("Jobs.Count").Should().Contain("絞り込んだ 1 件 / 全 3 件");
                app.Invoke("Jobs.ClearFilters");
                AppSession.WaitUntil(() => app.RowCount("Jobs.List") == 3, "絞り込みを外すと 3 行に戻る");
                app.ValueOf("Jobs.Text").Should().BeEmpty("外すと入力欄も空になる");

                // 列の幅と順序を初期値に戻す（余白の列は最後のまま）
                app.Invoke("Jobs.ResetLayout");
                app.RowCount("Jobs.List").Should().Be(3);
                app.IsWindowShown.Should().BeTrue("表の部品の中の例外で落ちていない");
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 終わったジョブの記録（Core の JobRecord と同じ形。列挙は名前）
    private static string JobRecords(DateTimeOffset now)
    {
        static string Record(string kind, string group, string app, string state, string? error, DateTimeOffset finished)
        {
            var errorJson = error == null ? "null" : System.Text.Json.JsonSerializer.Serialize(error);
            return $$"""
                {"Kind":"{{kind}}","Subscription":"00000000-0000-0000-0000-000000000001","ResourceGroup":"{{group}}","AppName":"{{app}}","ExpiryDeletion":false,
                 "ScheduledAt":null,"FinishedAt":"{{finished:O}}","State":"{{state}}","Error":{{errorJson}},"Note":null,"Endpoint":null,
                 "PersistenceSummary":null,"ExpirySummary":null,"DeletionSummary":null,"Logs":[],"RegisteredAt":"{{finished.AddMinutes(-10):O}}",
                 "ChangedAt":"{{finished:O}}","StartedAt":"{{finished.AddMinutes(-5):O}}"}
                """;
        }
        return "[" + string.Join(",",
            Record("Deploy", "rg-ci-a", "app-a", "Succeeded", null, now.AddDays(-2)),
            Record("Delete", "rg-ci-b", "app-b", "Failed", "権限がありません", now.AddDays(-1)),
            Record("Deploy", "rg-ci-c", "app-c", "Canceled", null, now.AddHours(-1))) + "]";
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
