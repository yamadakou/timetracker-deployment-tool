using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using FluentAssertions;
using Microsoft.Playwright;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// 主な画面を axe-core（WCAG 2.1 A・AA）で確かめる（UX の見直し D、2026-11-16）。影響が serious・critical の違反を残さない
/// （moderate 以下は、画面の作りの好みに近いものが多いため、ここでは止めない）。
/// </summary>
public sealed class AccessibilityUiTests
{
    private static readonly string[] Tags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

    [Fact]
    public async Task Main_Screens_Should_Have_No_Serious_Accessibility_Violations()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        var problems = new List<string>();
        await CheckAsync(page, "サインインと対象", problems);
        await page.LoadAsync();
        await page.SelectDeploymentAsync(TTDeployTool.Controller.Desktop.Fixtures.FixtureSamples.AppName(1));
        await CheckAsync(page, "デプロイの一覧（1 行を選んで詳細）", problems);
        await page.Rows("Deployments").First.Locator("td[data-key='AppName']").ClickAsync(new() { Button = MouseButton.Right });
        await CheckAsync(page, "デプロイの一覧の右クリックのメニュー", problems);
        await page.Keyboard.PressAsync("Escape");
        foreach (var (nav, name) in new[] { ("Wizard", "新しくデプロイ"), ("DryRun", "ドライラン"), ("Jobs", "ジョブの一覧"), ("AppSettings", "アプリの設定") })
        {
            await page.ShowAsync(nav);
            await CheckAsync(page, name, problems);
        }
        problems.Should().BeEmpty();
    }

    private static async Task CheckAsync(IPage page, string screen, List<string> problems)
    {
        var result = await page.RunAxe(new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. Tags] } });
        // axe が動いて規則を当てたこと（読み込めずに何も調べないまま通らない）
        result.Passes.Should().NotBeEmpty($"{screen} を axe で調べられる");
        foreach (var violation in result.Violations.Where(v => v.Impact is "serious" or "critical"))
            problems.Add($"{screen}: {violation.Id}（{violation.Impact}）{violation.Help} — " +
                         string.Join(" / ", violation.Nodes.Take(5).Select(n => string.Join(" ", n.Target))));
    }
}
