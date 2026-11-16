using FluentAssertions;
using Microsoft.Playwright;
using TTDeployTool.Controller.Desktop.Fixtures;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// 料金の目安の、削除したデプロイの分（オーナーの指摘、2026-11-16。案 1・案 2）を、本物のブラウザーで確かめる。
/// 削除すると「削除したデプロイ（今月）」が出て、起動し直しても残る。「Cost Management を開く」で、サインインしたテナントの Cost Management を開く。
/// </summary>
public sealed class DeletedCostUiTests
{
    [Fact]
    public async Task Deleting_Should_Keep_Its_Cost_Until_The_Deletion()
    {
        // モックの Azure の書き込みの待ち（既定は 1 回 2 秒）を短くし、削除をすぐ終える
        await using var host = await WithFixtureAsync(d => d with { WriteDelaySeconds = 0.1 });
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        await page.ExpectTextAsync("Deployments.Cost.Scope", "「全体」は今あるデプロイだけを数えます。");
        (await page.Id("Deployments.Cost.Deleted").CountAsync()).Should().Be(0);
        await page.ClickIdAsync("Deployments.Cost.OpenCostManagement");
        await WaitUntilAsync(() => Task.FromResult(host.Events().Contains(
            (FixtureEvents.OpenUri, $"https://portal.azure.com/#@{FixtureSamples.TenantId}/view/Microsoft_Azure_CostManagement/Menu/~/overview"))), "Cost Management を開く");

        await page.DeleteAsync(FixtureSamples.AppName(1), schedule: false);
        await page.ShowAsync("Deployments");
        await Assertions.Expect(page.Id("Deployments.Cost.DeletedTotal")).ToContainTextAsync("円", new() { Timeout = 15000 });
        await page.ExpectTextAsync("Deployments.Cost.DeletedNote", "今月このアプリで削除した 1 件の、削除した日時までの分（今月分に含みます）");
        if (Environment.GetEnvironmentVariable("TT_SCREENSHOT_DIR") is { Length: > 0 } dir)
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "deleted-cost.png") });

        // 起動し直しても残る（アプリの設定に記録する）
        await host.RestartAsync(signIn: true);
        page = host.Page;
        await page.LoadAsync();
        await Assertions.Expect(page.Id("Deployments.Cost.DeletedTotal")).ToContainTextAsync("円");
    }
}
