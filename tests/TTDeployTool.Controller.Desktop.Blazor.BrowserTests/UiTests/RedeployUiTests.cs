using Microsoft.Playwright;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// 失敗したデプロイのジョブの設定を「新しくデプロイ」に読み込み、変えてから出し直せる（オーナーの依頼、2026-11-03。オーナーの実機で、予定より遅れて期限を過ぎて失敗したデプロイ）。
/// </summary>
public sealed class RedeployUiTests
{
    private static readonly DeploymentTemplate Settings = new("", FixtureSamples.SubscriptionA, "rg-failed", "app-failed", "japaneast",
        "postgresql", "timetracker", "", "1.25", "2.5", "0.5", "1.0", "0.25", "0.5", true, ExpiryMode.Date, "2026-11-02", "チーム A");

    [Fact]
    public async Task A_Failed_Deployment_Should_Be_Loaded_From_The_Job_List_And_The_Status_Page()
    {
        await using var host = await WithFixtureAsync(records: records =>
        [
            .. records,
            new JobRecord(JobKind.Deploy, FixtureSamples.SubscriptionA, "rg-failed", "app-failed", false, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(-1),
                DesktopJobState.Failed, "実行しませんでした: 利用可能期限日（2026-11-02）は、今日以降の日付を指定してください。", null, null, null, null, null, [], Settings: Settings),
        ]);
        var page = host.Page;
        await page.SignInAsync();
        await page.ShowAsync("Jobs");
        await page.PageSizeAsync("jobs", "50");
        var row = page.RowWith("Jobs", "Target", "rg-failed / app-failed");

        // ジョブの一覧で選んで読み込むと、「新しくデプロイ」の ① に、ジョブの対象が入る
        await row.ClickAsync();
        await page.ClickIdAsync("Jobs.Redeploy");
        await Assertions.Expect(page.Id("Panel.Wizard")).ToBeVisibleAsync();
        await page.ExpectTextAsync("Wizard.Step", "①");
        await Assertions.Expect(page.Id("Target.ResourceGroup")).ToHaveValueAsync("rg-failed");
        await Assertions.Expect(page.Id("Target.AppName")).ToHaveValueAsync("app-failed");
        await page.ExpectTextAsync("Settings.Message", "ジョブの設定（rg-failed / app-failed）を入力しました。");

        // ① の値を変えてから、実行状況の画面（詳細。選んだ行は選んだまま）から読み込み直すと、ジョブの値に戻る
        await page.Id("Target.AppName").FillAsync("changed");
        await page.ShowAsync("Jobs");
        await page.ExpectTextAsync("Jobs.Selection", "1 件を選んでいます。");
        await page.ClickIdAsync("Jobs.Detail");
        await Assertions.Expect(page.Id("Panel.Status")).ToBeVisibleAsync();
        await page.ClickIdAsync("Job.Redeploy");
        await Assertions.Expect(page.Id("Panel.Wizard")).ToBeVisibleAsync();
        await Assertions.Expect(page.Id("Target.AppName")).ToHaveValueAsync("app-failed");
    }
}
