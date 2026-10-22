using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// Azure を使わない操作の UI テスト（詳細設計 5。CI の desktop-ui ジョブで実行する）。サインインはしない。
/// </summary>
public class CiUiTests
{
    private static readonly string[] Pages = ["Target", "Deployments", "Settings", "Deploy", "Status", "Delete", "DryRun", "Jobs"];

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
    public void Deployments_Page_Should_Ask_To_Sign_In_Before_Loading() => AppSession.Run(app =>
    {
        app.Show("Deployments");
        app.TextOf("Deployments.Page").Should().StartWith("「読み込む」を押すと");
        app.IsEnabled("Deployments.Previous").Should().BeFalse();
        app.IsEnabled("Deployments.Next").Should().BeFalse();
        app.Exists("Deployments.ExpiryFrom").Should().BeFalse("日付の範囲を選ぶまで出さない");
        app.Select("Deployments.ExpiryFilter", "日付の範囲");
        AppSession.WaitUntil(() => app.Exists("Deployments.ExpiryFrom"), "期限日の範囲の入力が表示される");
        app.Select("Deployments.ExpiryFilter", "すべて");

        // サインインしていないため、サブスクリプションが無い
        app.Invoke("Deployments.Load");
        app.WaitForText("Deployments.Error", t => t.Contains("サブスクリプションがありません"), "サインインを求めるエラーが出る");
        app.Exists("Row.AppName").Should().BeFalse();
    });

    [Fact]
    public void Deploy_And_Delete_Pages_Should_Not_Show_A_Job_Before_Any_Job() => AppSession.Run(app =>
    {
        // ジョブが無いのに、ジョブの表示の型（「: 」と進み具合のバー）が出ていた（実機の確認、2026-10-21）
        foreach (var page in new[] { "Deploy", "Delete" })
        {
            app.Show(page);
            app.Exists("Job.Title").Should().BeFalse($"{page} の画面にジョブの題名を出さない");
            app.Exists("Job.Progress").Should().BeFalse($"{page} の画面に進み具合のバーを出さない");
        }
    });

    [Fact]
    public void Concurrency_Should_Be_Saved_Across_Restarts()
    {
        AppSession.Run(app =>
        {
            app.Show("Settings");
            app.ItemsOf("Settings.Concurrency").Should().Equal("1", "2", "3", "4");
            app.SelectedOf("Settings.Concurrency").Should().Be("2", "既定は 2");
            app.Select("Settings.Concurrency", "3");
        }, "Concurrency_Should_Be_Saved_Across_Restarts_1");
        AppSession.Run(app =>
        {
            app.Show("Settings");
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
        // 7e: 対象の入力は「設定」にある
        app.Exists("Target.ResourceGroup").Should().BeFalse("「サインインと対象」には対象の入力を出さない");
        app.Show("Settings");
        app.TextOf("Settings.Subscription").Should().Contain("「サインインと対象」で選んでください");
        app.TextOf("Target.TargetError").Should().Contain("サブスクリプションを選んでください。");
    });

    [Fact]
    public void A_Template_Should_Be_Saved_And_Applied_After_A_Restart()
    {
        var name = "UI テスト " + Guid.NewGuid().ToString("N")[..6];
        AppSession.Run(app =>
        {
            app.Show("Settings");
            app.SetText("Target.ResourceGroup", "rg-template");
            app.SetText("Settings.DbName", "uitemplate");
            app.SetText("Settings.TemplateName", name);
            app.Invoke("Settings.SaveTemplate");
            app.WaitForText("Settings.Message", t => t.Contains($"テンプレート「{name}」を保存しました"), "テンプレートを保存できる");
            app.IsEnabled("Settings.CopyMarkdown").Should().BeTrue();
        }, "A_Template_Should_Be_Saved_And_Applied_After_A_Restart_1");
        AppSession.Run(app =>
        {
            app.Show("Settings");
            app.ValueOf("Settings.DbName").Should().Be("timetracker", "起動したときは既定の値");
            app.Select("Settings.Template", name);
            app.Invoke("Settings.ApplyTemplate");
            AppSession.WaitUntil(() => app.ValueOf("Settings.DbName") == "uitemplate", "テンプレートの値が入る");
            app.ValueOf("Target.ResourceGroup").Should().Be("rg-template");
        }, "A_Template_Should_Be_Saved_And_Applied_After_A_Restart_2");
    }

    [Fact]
    public void Settings_Should_Be_Validated_While_Typing() => AppSession.Run(app =>
    {
        app.Show("Settings");

        app.SetText("Settings.TtCpu", "abc");
        app.WaitForScreenText(t => t.Contains("tt-cpu には数値を指定してください"), "数値でない CPU の誤りが表示される");

        app.SetText("Settings.TtCpu", "1.5");
        app.WaitForScreenText(t => !t.Contains("tt-cpu"), "直すと誤りが消える");
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

            // コンボボックスの選択（選択の変更イベントで ViewModel に戻す）と、パスワードの入力
            app.Show("Settings");
            app.Select("Settings.DbType", "sqlserver");
            app.Select("Settings.PersistDb", "Azure Files に永続化する（料金がかかる）");
            // 利用可能期限日: 「日付を指定する」で日付の入力が現れ、既定に戻すと消える
            app.TextOf("Settings.ExpiryNotice").Should().Contain("今日から 32 日後").And.Contain("自動で削除します");
            app.Exists("Settings.ExpiryDate").Should().BeFalse();
            app.Select("Settings.Expiry", "日付を指定する");
            AppSession.WaitUntil(() => app.Exists("Settings.ExpiryDate"), "期限日の入力が表示される");
            app.Select("Settings.Expiry", "期限なし");
            AppSession.WaitUntil(() => !app.Exists("Settings.ExpiryDate"), "期限日の入力が消える");
            app.SetText("Settings.DbPassword", "UiTestDbPass1!");
            app.SetText("Settings.TrackerPassword", "UiTestTracker1!");

            app.Show("DryRun");
            app.Invoke("DryRun.Save");
            app.PickFolder(folder);

            app.WaitForText("DryRun.Compose", t => t.Contains("11.0.1-linux-mssql"), "SQL Server のイメージの docker-compose.yml が表示される");
            File.Exists(Path.Combine(folder, "docker-compose.yml")).Should().BeTrue();
            File.ReadAllText(Path.Combine(folder, ".env")).Should().Contain("UiTestDbPass1!");
            app.ScreenText().Should().Contain("persist-db は Azure 向けの設定のため").And.Contain("docker compose up -d");
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
