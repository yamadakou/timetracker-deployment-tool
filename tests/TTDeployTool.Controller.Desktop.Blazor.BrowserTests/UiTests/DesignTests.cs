using FluentAssertions;
using Microsoft.Playwright;
using TTDeployTool.Controller.Desktop.Fixtures;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// 推奨のデザイン（オーナーの判断、2026-11-13）の画面を、明るい画面と濃い色の画面（Windows の設定に合わせる。prefers-color-scheme）で開く。
/// 件数のカード・期限の残りのバー・スペックのスライダー・料金の目安を確かめ、環境変数 TT_SCREENSHOT_DIR があれば、画面の画像を残す（CI の成果物）。
/// </summary>
public sealed class DesignTests
{
    private static async Task ShotAsync(IPage page, string name)
    {
        if (Environment.GetEnvironmentVariable("TT_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{name}.png") });
        }
    }

    [Theory]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task The_Screens_Should_Show_The_Cards_The_Bars_The_Sliders_And_The_Cost(ColorScheme scheme)
    {
        await using var host = await WithFixtureAsync(width: 1440, height: 900);
        var page = host.Page;
        await page.EmulateMediaAsync(new() { ColorScheme = scheme });
        var mode = scheme.ToString().ToLowerInvariant();
        (await page.EvaluateAsync<bool>("matchMedia('(prefers-color-scheme: dark)').matches")).Should().Be(scheme == ColorScheme.Dark);
        await page.SignInAsync();
        await page.LoadAsync();

        // デプロイの一覧: 件数のカードで絞り込み、期限の残りのバーと、右の詳細
        await page.ExpectTextAsync("Deployments.Card.All.Value", $"{Deployments}");
        await Assertions.Expect(page.Locator("[data-id='Panel.Deployments'] td[data-key='ExpiresAt'] .tt-bar-track").First).ToBeVisibleAsync();
        await Assertions.Expect(page.Id("Deployments.Cost.Total")).ToContainTextAsync("円");
        await page.RowWith("Deployments", "AppName", FixtureSamples.AppName(1)).ClickAsync();
        await Assertions.Expect(page.Id("Row.DaysLeft")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^あと \\d+ 日$"));
        await ShotAsync(page, $"deployments-{mode}");
        await page.ClickIdAsync("Deployments.Card.Expiring");
        await Assertions.Expect(page.Id("Deployments.Card.Expiring")).ToHaveAttributeAsync("aria-pressed", "true");
        (await page.Rows("Deployments").CountAsync()).Should().BeLessThan(Deployments);
        await page.ClickIdAsync("Deployments.Card.Expiring");
        await Assertions.Expect(page.PageText("deployments")).ToContainTextAsync($"全 {Deployments} 件");

        // ジョブの一覧: 件数のカード
        await page.ShowAsync("Jobs");
        await Assertions.Expect(page.Id("Jobs.Card.Failed")).ToBeVisibleAsync();
        await ShotAsync(page, $"jobs-{mode}");

        // 新しくデプロイ: 手順の帯、④ のスライダーと料金の目安（単位の切り替え）
        await page.ShowAsync("Wizard");
        await Assertions.Expect(page.Id("Wizard.Stepper")).ToBeVisibleAsync();
        await page.Id("Target.Subscription").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await page.Id("Target.ResourceGroup").FillAsync("rg-design");
        await page.Id("Target.AppName").FillAsync("design");
        await page.ClickIdAsync("Wizard.Next");
        await page.ClickIdAsync("Wizard.Next");
        await page.Id("Settings.DbPassword").FillAsync("DbPassw0rd!");
        await page.Id("Settings.TrackerPassword").FillAsync("TrackerPass1!");
        await page.ClickIdAsync("Wizard.Next");
        await Assertions.Expect(page.Id("Spec.TimeTracker.Slider")).ToBeVisibleAsync();
        await page.Id("Spec.DB.Slider").FillAsync("7");
        await page.ExpectTextAsync("Spec.DB.Value", "2 vCPU / 4 GiB");
        await Assertions.Expect(page.Id("Cost.Total")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^約 [\\d,]+ 円$"));
        await ShotAsync(page, $"wizard-{mode}");
        await page.ClickIdAsync("Cost.Unit.Usd");
        await Assertions.Expect(page.Id("Cost.Total")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^約 \\$[\\d,]+\\.\\d\\d$"));
    }
}
