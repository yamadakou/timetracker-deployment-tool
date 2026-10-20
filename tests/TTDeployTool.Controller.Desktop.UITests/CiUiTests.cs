using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// Azure を使わない操作の UI テスト（詳細設計 5。CI の desktop-ui ジョブで実行する）。サインインはしない。
/// </summary>
public class CiUiTests
{
    private static readonly string[] Pages = ["Target", "Settings", "Deploy", "Status", "Delete", "DryRun", "Jobs"];

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
    public void Signed_Out_State_Should_Allow_Only_Sign_In() => AppSession.Run(app =>
    {
        app.TextOf("SignIn.Account").Should().StartWith("サインインしていません");
        app.IsEnabled("SignIn.SignIn").Should().BeTrue();
        app.IsEnabled("SignIn.SignOut").Should().BeFalse();
        app.TextOf("Target.TargetError").Should().Contain("サブスクリプションを選んでください。");
    });

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
            // コンボボックスの選択（選択の変更イベントで ViewModel に戻す）と、パスワードの入力
            app.Show("Settings");
            app.Select("Settings.DbType", "sqlserver");
            app.Select("Settings.PersistDb", "Azure Files に永続化する（料金がかかる）");
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
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void Closing_Without_Jobs_Should_Not_Ask() => AppSession.Run(app =>
    {
        app.CloseAndWait().Should().BeTrue("実行中のジョブが無ければ、確認なしで閉じる");
    });

    // アプリが書き込み中のログを読む
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
