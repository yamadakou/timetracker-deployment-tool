using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// Azure を使わない操作の UI テスト（詳細設計 5。CI の desktop-ui ジョブで実行する）。サインインはしない。
/// </summary>
public class CiUiTests
{
    // 7f: 削除・実行状況はメニューに無い。No.63: デプロイの一覧・新しくデプロイ・ジョブの一覧はサインインするまで押せない（docs/design/desktop-wizard.md の 2・7）
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
        // No.63: サインインしていないときは、新しくデプロイ・ジョブの一覧も押せない（ウィザードの誤りの表示は Azure の UI テストで確かめる）
        foreach (var page in new[] { "Nav.Deployments", "Nav.Wizard", "Nav.Jobs" })
            app.IsEnabled(page).Should().BeFalse($"サインインするまで {page} は押せない");
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
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
