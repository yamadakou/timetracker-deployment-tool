using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// Azure を使わない操作の UI テスト（詳細設計 5。CI の desktop-ui ジョブで実行する）。サインインはしない。
/// </summary>
public class CiUiTests
{
    // 7f: 削除・実行状況はメニューに無く、デプロイの一覧はサインインするまで押せない（docs/design/desktop-wizard.md の 2）
    private static readonly string[] Pages = ["Target", "Wizard", "DryRun", "Jobs"];

    [Fact]
    public void Startup_Should_Open_The_Window_And_Write_The_Diagnostic_Log() => AppSession.Run(app =>
    {
        app.Window.Title.Should().Be("TTDeployTool");

        app.Show("Jobs");
        var directory = app.TextOf("Jobs.LogDirectory");
        Directory.Exists(directory).Should().BeTrue($"ログのフォルダー {directory} があるはず");
        var log = Directory.GetFiles(directory, "ttdeploytool-desktop-*.log").OrderBy(f => f).Last();
        var text = ReadShared(log);
        text.Should().Contain("起動しました");
        text.Should().NotContain("[Critical]");
        app.IsEnabled("Jobs.OpenLogFolder").Should().BeTrue();
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
    public void Jobs_Page_Should_Show_No_Jobs_And_No_Banner() => AppSession.Run(app =>
    {
        app.Show("Jobs");
        app.TextOf("Panel.Jobs").Should().StartWith("ジョブ");
        app.TextOf("Jobs.Summary").Should().BeEmpty("終わっていないジョブが無い");
        app.Exists("Jobs.Banner").Should().BeFalse("ジョブが無ければ上部の帯を出さない");
        app.IsEnabled("Jobs.ClearFinished").Should().BeTrue();
        app.Exists("Job.Title").Should().BeFalse();
    });

    [Fact]
    public void Menu_Should_Have_No_Delete_Or_Status_And_The_List_Should_Need_Sign_In() => AppSession.Run(app =>
    {
        foreach (var removed in new[] { "Nav.Settings", "Nav.Deploy", "Nav.Status", "Nav.Delete" })
            app.Exists(removed).Should().BeFalse($"{removed} はメニューに無い");
        app.IsEnabled("Nav.Deployments").Should().BeFalse("サインインするまでデプロイの一覧は押せない");
    });

    [Fact]
    public void The_Wizard_Should_Not_Go_Next_With_Errors_And_Copy_From_Any_Step() => AppSession.Run(app =>
    {
        app.Show("Wizard");
        app.TextOf("Wizard.Step").Should().Be("① 対象（1 / 5）");
        app.IsEnabled("Wizard.Back").Should().BeFalse();
        // サインインしていないため、サブスクリプションが無い
        app.IsEnabled("Wizard.Next").Should().BeFalse("誤りがあれば次へ進めない");
        app.TextOf("Wizard.Errors").Should().Contain("subscription は必須です。").And.Contain("resource-group は必須です。");
        app.TextOf("Wizard.Blocked").Should().Be("誤りが 2 件あるため、次へ進めません。");
        app.SetText("Target.ResourceGroup", "rg-wizard");
        app.WaitForText("Wizard.Blocked", t => t == "誤りが 1 件あるため、次へ進めません。", "直した誤りは消える");
        app.IsEnabled("Settings.CopyMarkdown").Should().BeTrue("どの画面にも設定のコピーがある");
        app.Exists("Settings.Reset").Should().BeTrue("どの画面にも初期値に戻すがある");
        // ジョブが無いのに、ジョブの表示の型（「: 」と進み具合のバー）が出ていた（実機の確認、2026-10-21）
        app.Exists("Job.Title").Should().BeFalse();
        app.Exists("Job.Progress").Should().BeFalse();
    });

    [Fact]
    public void Concurrency_Should_Be_Saved_Across_Restarts()
    {
        AppSession.Run(app =>
        {
            app.Show("Jobs");
            app.ItemsOf("Settings.Concurrency").Should().Equal("1", "2", "3", "4");
            app.SelectedOf("Settings.Concurrency").Should().Be("2", "既定は 2");
            app.Select("Settings.Concurrency", "3");
        }, "Concurrency_Should_Be_Saved_Across_Restarts_1");
        AppSession.Run(app =>
        {
            app.Show("Jobs");
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
        // 7f: サブスクリプションと対象の入力は、ウィザードの ① にある
        app.Exists("Target.Subscription").Should().BeFalse("サインインの画面には、サブスクリプションの選択を出さない");
        app.Show("Wizard");
        app.Exists("Target.Subscription").Should().BeTrue();
        app.Exists("Target.ResourceGroup").Should().BeTrue();
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
        app.Show("Jobs");
        app.TextOf("Jobs.Resident").Should().Contain("通知領域に残って続けます");

        // 2 つ目の起動は 1 つ目に渡されて終わり、アプリのプロセスは 1 つのまま
        using var second = FlaUI.Core.Application.LaunchStoreApp(AppSession.AppUserModelId());
        AppSession.TryWait(() => System.Diagnostics.Process.GetProcessesByName("ttdeploytool-desktop").Length == 1, TimeSpan.FromSeconds(30))
            .Should().BeTrue("2 つ目は起動を渡して終わる");
        app.App.HasExited.Should().BeFalse();
        app.Exists("Panel.Jobs").Should().BeTrue("1 つ目のウィンドウはそのまま");
    });

    // アプリが書き込み中のログを読む
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
