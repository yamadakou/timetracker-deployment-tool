using FluentAssertions;
using Microsoft.Playwright;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// WinUI 3 版の CiUiTests と同じ内容（Azure を使わない操作。サインインはしない）。ウィンドウの題・2 つ目の起動は WPF のホストの確認
/// （ci.yml の blazor-prototype-windows ジョブの IntegrationCheck・WebView2Tests）で確かめる。
/// </summary>
public sealed class CiUiTests
{
    private static readonly string[] Pages = ["Target", "DryRun", "AppSettings"];

    [Fact]
    public async Task Startup_Should_Open_The_Window_And_Write_The_Diagnostic_Log()
    {
        await using var host = await Ui.EmptyAsync();
        var page = host.Page;
        (await page.TitleAsync()).Should().Be("TTDeployTool");

        await page.ShowAsync("AppSettings");
        var directory = (await page.Id("Settings.LogDirectory").TextContentAsync())!;
        Directory.Exists(directory).Should().BeTrue($"ログのフォルダー {directory} があるはず");
        var log = Directory.GetFiles(directory, "ttdeploytool-desktop-*.log").OrderBy(f => f).Last();
        string text;
        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        text.Should().Contain("起動しました");
        text.Should().NotContain("[Critical]");
        await Assertions.Expect(page.Id("Settings.OpenLogFolder")).ToBeEnabledAsync();
    }

    [Fact]
    public async Task Navigation_Should_Show_Only_The_Selected_Page()
    {
        await using var host = await Ui.EmptyAsync();
        var page = host.Page;
        foreach (var shown in Pages)
        {
            await page.ShowAsync(shown);
            await Assertions.Expect(page.Id($"Panel.{shown}")).ToBeVisibleAsync();
            foreach (var other in Pages.Where(p => p != shown))
                (await page.Id($"Panel.{other}").CountAsync()).Should().Be(0, $"{shown} を選んだとき、{other} は表示しない");
        }
    }

    [Fact]
    public async Task Pages_That_Need_Azure_Should_Wait_For_Sign_In_And_Show_No_Banner()
    {
        await using var host = await Ui.EmptyAsync();
        var page = host.Page;
        foreach (var nav in new[] { "Nav.Deployments", "Nav.Wizard" })
            await Assertions.Expect(page.Id(nav)).ToBeDisabledAsync();
        await Assertions.Expect(page.Id("Nav.Jobs")).ToBeEnabledAsync();
        (await page.Id("Jobs.Banner").CountAsync()).Should().Be(0, "ジョブが無ければ上部の帯を出さない");
        (await page.Id("Job.Title").CountAsync()).Should().Be(0);
        (await page.Id("Job.Progress").CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Menu_Should_Have_No_Delete_Or_Status_And_Settings_At_The_Bottom()
    {
        await using var host = await Ui.EmptyAsync();
        var page = host.Page;
        foreach (var removed in new[] { "Nav.Settings", "Nav.Deploy", "Nav.Status", "Nav.Delete" })
            (await page.Id(removed).CountAsync()).Should().Be(0, $"{removed} はメニューに無い");
        await page.ExpectTextAsync("Nav.Jobs", "ジョブの一覧");
        // 設定はメニューのいちばん下（離して置く）
        var settings = (await page.Id("Nav.AppSettings").BoundingBoxAsync())!;
        var dryRun = (await page.Id("Nav.DryRun").BoundingBoxAsync())!;
        settings.Y.Should().BeGreaterThan(dryRun.Y + dryRun.Height * 2);

        await page.ShowAsync("AppSettings");
        (await page.Id("Settings.StartupStatus").TextContentAsync()).Should().NotBeEmpty();
        (await page.Id("Settings.LastStartup").TextContentAsync()).Should().StartWith("Windows のサインイン時に起動したことは、まだありません");
        (await page.Id("Settings.NotificationStatus").TextContentAsync()).Should().NotBeEmpty();
        (await page.Id("Settings.CloseBehavior").TextContentAsync()).Should().StartWith("今ウィンドウを閉じると、アプリを終了します");
        (await page.Id("Settings.ScheduledJobs").TextContentAsync()).Should().Be("まだ始めていないジョブ（予定・待機中・一時停止）はありません。");
        await page.ClickIdAsync("Settings.RefreshSystem");
        (await page.Id("Settings.CloseBehavior").TextContentAsync()).Should().StartWith("今ウィンドウを閉じると、アプリを終了します");
    }

    [Fact]
    public async Task Concurrency_Should_Be_Saved_Across_Restarts()
    {
        await using var host = await Ui.EmptyAsync();
        var page = host.Page;
        await page.ShowAsync("AppSettings");
        var select = page.Id("Settings.Concurrency");
        // 画面を開いた直後は、まだ選択肢を描いていないことがあるため、描くまで待つ
        await Assertions.Expect(select.Locator("option")).ToHaveCountAsync(4);
        (await select.Locator("option").AllTextContentsAsync()).Should().Equal("1", "2", "3", "4");
        (await select.EvaluateAsync<string>("s => s.options[s.selectedIndex].text")).Should().Be("2", "既定は 2");
        await select.SelectOptionAsync(new SelectOptionValue { Label = "3" });

        await host.RestartAsync();
        page = host.Page;
        await page.ShowAsync("AppSettings");
        (await page.Id("Settings.Concurrency").EvaluateAsync<string>("s => s.options[s.selectedIndex].text")).Should().Be("3", "アプリのデータに保存する");
    }

    [Fact]
    public async Task Signed_Out_State_Should_Allow_Only_Sign_In()
    {
        await using var host = await Ui.EmptyAsync();
        var page = host.Page;
        (await page.Id("SignIn.Account").TextContentAsync()).Should().StartWith("サインインしていません");
        await Assertions.Expect(page.Id("SignIn.SignIn")).ToBeEnabledAsync();
        await Assertions.Expect(page.Id("SignIn.SignOut")).ToBeDisabledAsync();
        (await page.Id("Target.Subscription").CountAsync()).Should().Be(0, "サインインの画面には、サブスクリプションの選択を出さない");
        await Assertions.Expect(page.Id("Nav.Wizard")).ToBeDisabledAsync();
    }

    [Fact]
    public async Task Dry_Run_Should_Not_Save_With_Invalid_Settings()
    {
        await using var host = await Ui.EmptyAsync();
        var page = host.Page;
        await page.ShowAsync("DryRun");
        await page.ClickIdAsync("DryRun.Save");
        await page.ExpectTextAsync("DryRun.Error", "設定に誤りがあるため、ファイルを保存しませんでした");
        (await page.Id("DryRun.Compose").CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Dry_Run_Should_Save_The_Files_In_The_Chosen_Folder()
    {
        await using var host = await Ui.EmptyAsync();
        var page = host.Page;
        await page.ShowAsync("DryRun");
        await page.ClickIdAsync("DryRun.Save");
        await Assertions.Expect(page.Id("DryRun.Error")).ToBeVisibleAsync();

        await page.Id("DryRun.DbType").SelectOptionAsync(new SelectOptionValue { Label = "sqlserver" });
        await page.Id("DryRun.DbPassword").FillAsync("UiTestDbPass1!");
        await page.Id("DryRun.TrackerPassword").FillAsync("UiTestTracker1!");
        // フォルダーの選択は、テスト用のホストが --folder のフォルダーを返す
        await page.ClickIdAsync("DryRun.Save");

        await Assertions.Expect(page.Id("DryRun.Compose")).ToContainTextAsync("11.0.1-linux-mssql");
        File.Exists(Path.Combine(host.DryRunFolder, "docker-compose.yml")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(host.DryRunFolder, ".env"), TestContext.Current.CancellationToken)).Should().Contain("UiTestDbPass1!");
        (await page.Locator("body").InnerTextAsync()).Should().Contain("docker compose up -d");
        await Assertions.Expect(page.Id("DryRun.OpenFolder")).ToBeEnabledAsync();
        (await page.Id("DryRun.Error").CountAsync()).Should().Be(0, "保存できたら、前のエラーの表示が消える");
    }

    [Fact]
    public async Task Closing_Without_Jobs_Should_Not_Ask()
    {
        await using var host = await Ui.EmptyAsync();
        (await host.CloseActionAsync()).Should().Be("Exit", "終わっていないジョブが無ければ、確認なしで終了する（通知領域に残らない）");
        await host.Page.ShowAsync("AppSettings");
        (await host.Page.Id("Settings.Resident").TextContentAsync()).Should().Contain("通知領域に残って続けます");
    }

    /// <summary>行のある表（ジョブの一覧）。終わったジョブの記録を置いて起動し、サインインせずに、並べ替え・絞り込み・列の配置を初期値に戻す。</summary>
    [Fact]
    public async Task Job_Records_Should_Fill_The_Table_And_Survive_Sorting_And_Filtering()
    {
        await using var host = await Ui.EmptyAsync(directory => File.WriteAllText(Path.Combine(directory, "job-history.json"), JobRecords(DateTimeOffset.UtcNow)));
        var page = host.Page;
        await page.ShowAsync("Jobs");
        await page.ExpectTextAsync("Jobs.Count", "全 3 件");
        await Assertions.Expect(page.Id("Jobs.SignInNote")).ToBeVisibleAsync();
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(3);
        (await page.RowWith("Jobs", "Target", "rg-ci-b / app-b").CellTextAsync("State")).Should().StartWith("失敗");

        (await page.SortByHeaderThreeTimesAsync("Jobs", "State")).Should().Be("状態");
        (await page.Locator("[data-id='Panel.Jobs'] th[data-key='Result'] button.tt-sort").CountAsync()).Should().Be(0, "結果の列は並べ替えない");

        await page.TextTargetAsync("jobs", "リソースグループ / アプリ名");
        await page.TextFilterAsync("jobs", "rg-ci-b");
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(1);
        await page.ExpectTextAsync("Jobs.Count", "絞り込んだ 1 件 / 全 3 件");
        await page.ClearFiltersAsync("Jobs");
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(3);
        (await page.Locator("input[data-filter='jobs']").InputValueAsync()).Should().BeEmpty("外すと入力欄も空になる");

        await page.ResetLayoutAsync("Jobs");
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(3);
    }

    // 終わったジョブの記録（Core の JobRecord と同じ形。WinUI の CiUiTests と同じ）
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
}
