using FluentAssertions;
using Microsoft.Playwright;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Fixtures;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// WinUI 3 版の MockSystemTests と同じ内容: アプリの外の機能（通知・既定のアプリで開く・サインイン時の起動）をモックにして、アプリが書いた記録（uitest-events.txt）と
/// 画面の表示で、期限の通知・期限での自動の削除・設定の表示・予定と記録の再起動の後を確かめる。
/// </summary>
public sealed class MockSystemTests
{
    [Fact]
    public async Task Expiry_Should_Notify_And_Delete_Expired_Deployments()
    {
        await using var host = await WithFixtureAsync(data: _ => FixtureSamples.Data(Deployments, DateTimeOffset.UtcNow, expired: 1) with { WriteDelaySeconds = 120 });
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        // 3 日後に期限のデプロイは通知し、期限を過ぎたデプロイ（最後の 1 件）は、自動で削除のジョブを登録する
        await WaitUntilAsync(() => Task.FromResult(host.Events().Any(e => e.Kind == FixtureEvents.Notice && e.Text.StartsWith("利用可能期限が近づいています", StringComparison.Ordinal))),
            "期限の近いデプロイを通知する", TimeSpan.FromSeconds(60));
        var expired = $"rg-mock-{Deployments - 1:00} / app{Deployments - 1:00}";
        await page.ShowAsync("Jobs");
        await Assertions.Expect(page.RowWith("Jobs", "Target", expired).Locator("td[data-key='Kind']")).ToHaveTextAsync("期限での削除", new() { Timeout = 60_000 });
    }

    [Fact]
    public async Task Settings_Should_Show_Startup_And_Notification_States()
    {
        await using var host = await WithFixtureAsync(data: d => d with { StartupTask = StartupTaskStatus.DisabledByUser, Notifications = NotificationPermission.DisabledForApplication });
        var page = host.Page;
        await page.ShowAsync("AppSettings");
        await page.ExpectTextAsync("Settings.StartupStatus", "タスク マネージャー");
        await page.ExpectTextAsync("Settings.NotificationStatus", "このアプリの通知が、Windows の設定で止められています");
    }

    [Fact]
    public async Task The_Startup_Toggle_And_Open_Folder_Should_Reach_Windows()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.ShowAsync("AppSettings");
        await page.ClickIdAsync("Settings.StartupToggle");
        await WaitUntilAsync(() => Task.FromResult(host.Events().Contains((FixtureEvents.StartupTask, nameof(StartupTaskStatus.Enabled)))), "サインイン時の起動を Windows に登録する");

        // Dry-run で保存し、「フォルダーを開く」で保存したフォルダーを開く
        await page.ShowAsync("DryRun");
        await page.Id("DryRun.DbPassword").FillAsync("UiTestDbPass1!");
        await page.Id("DryRun.TrackerPassword").FillAsync("UiTestTracker1!");
        await page.ClickIdAsync("DryRun.Save");
        await Assertions.Expect(page.Id("DryRun.Compose")).Not.ToBeEmptyAsync();
        await page.ClickIdAsync("DryRun.OpenFolder");
        await WaitUntilAsync(() => Task.FromResult(host.Events().Any(e => e.Kind == FixtureEvents.OpenFolder && e.Text.TrimEnd('/', '\\') == host.DryRunFolder)), "保存したフォルダーを開く");
    }

    [Fact]
    public async Task Schedules_And_Deleted_Records_Should_Survive_A_Restart()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        await page.PageSizeAsync("deployments", "50");
        await page.DeleteAsync("app06", schedule: true);
        await page.ExpectTextAsync("Jobs.Count", $"全 {Records + 1} 件");

        // 記録を 1 件消す（確認のダイアログで「記録から削除」）
        var newest = $"rg-mock-{100 + Records - 1} / app{100 + Records - 1}";
        await page.RowWith("Jobs", "Target", newest).ClickAsync();
        await page.ClickIdAsync("Jobs.DeleteSelectedRecords");
        await page.ClickIdAsync("Dialog.Confirm");
        await page.ExpectTextAsync("Jobs.Count", $"全 {Records} 件");

        // 予定があると閉じても通知領域に残る。起動し直すと、サインインする前は記録だけ（消した 1 件は戻らない）、サインインすると予定が戻る
        (await host.CloseActionAsync()).Should().Be("Hide", "予定があれば通知領域に残る");
        await host.RestartAsync(signIn: false);
        page = host.Page;
        await page.ShowAsync("Jobs");
        await page.ExpectTextAsync("Jobs.Count", $"全 {Records - 1} 件");
        await page.SignInAsync();
        await page.ShowAsync("Jobs");
        await Assertions.Expect(page.RowWith("Jobs", "Target", "rg-mock-06 / app06").Locator("td[data-key='State']")).ToContainTextAsync("予定");
        (await page.Rows("Jobs").AllInnerTextsAsync()).Should().NotContain(t => t.Contains(newest), "消した記録は戻らない");
    }
}
