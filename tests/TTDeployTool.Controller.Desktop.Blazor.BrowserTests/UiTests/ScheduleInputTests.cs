using FluentAssertions;
using Microsoft.Playwright;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// 予定の日付と時刻の欄に、キーボードで打った時刻が届く（オーナーの実機の確認、2026-10-31: ジョブの一覧で予定を変えると、指定と違う時刻になった。
/// Blazor の JavaScript が時刻の欄の値に秒を付けて渡し〔"23:35:00"〕、"HH:mm" だけを読んでいたため、変更が無視されていた）。
/// </summary>
public sealed class ScheduleInputTests
{
    // 時刻の欄の分を押して、打つ（人の操作と同じく、input と change が起きる）
    private static async Task TypeMinutesAsync(IPage page, string id, string minutes)
    {
        await page.Id(id).ClickAsync(new() { Position = new() { X = 30, Y = 10 } });
        await page.Keyboard.TypeAsync(minutes);
        await page.Keyboard.PressAsync("Tab");
    }

    [Fact]
    public async Task Rescheduling_Jobs_Should_Use_The_Typed_Time()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        await page.DeleteAsync("app06", schedule: true);
        var row = page.RowWith("Jobs", "Target", "rg-mock-06 / app06");
        await row.ClickAsync();
        var before = await page.Id("Jobs.ScheduleTime").InputValueAsync();
        var minutes = before.EndsWith("34", StringComparison.Ordinal) ? "36" : "34";
        await TypeMinutesAsync(page, "Jobs.ScheduleTime", minutes);
        var typed = await page.Id("Jobs.ScheduleTime").InputValueAsync();
        typed.Should().Be($"{before[..3]}{minutes}");

        await page.ClickIdAsync("Jobs.RescheduleSelected");
        await page.ExpectTextAsync("Jobs.Message", $" {typed}）");
        await Assertions.Expect(row.Locator("td[data-key='State']")).ToContainTextAsync(typed);
    }

    [Fact]
    public async Task Scheduling_A_Deletion_Should_Use_The_Typed_Time()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        await page.RowWith("Deployments", "AppName", "app08").ClickAsync();
        await page.ClickIdAsync("Deployments.Delete");
        await page.Id("Delete.ConfirmAppName").FillAsync("app08");
        var before = await page.Id("Delete.ScheduleTime").InputValueAsync();
        var minutes = before.EndsWith("34", StringComparison.Ordinal) ? "36" : "34";
        await TypeMinutesAsync(page, "Delete.ScheduleTime", minutes);
        var typed = await page.Id("Delete.ScheduleTime").InputValueAsync();
        typed.Should().Be($"{before[..3]}{minutes}");
        await page.ClickIdAsync("Delete.Schedule");
        await Assertions.Expect(page.RowWith("Jobs", "Target", "rg-mock-08 / app08").Locator("td[data-key='State']")).ToContainTextAsync(typed);
    }

    // 今の時刻と、今より前の時刻（今日の 00:00。日をまたぐ直前でも今より前）は、エラーで予定を変えない（オーナーの確認、2026-10-31）
    public static TheoryData<string> PastTimes => new() { "now", "00:00" };

    private static async Task SetDateTimeAsync(IPage page, string id, string time)
    {
        var now = DateTime.Now;
        await page.Id($"{id}Date").FillAsync(now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await page.Id($"{id}Time").FillAsync(time == "now" ? now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : time);
    }

    [Theory]
    [MemberData(nameof(PastTimes))]
    public async Task Rescheduling_To_Now_Or_Earlier_Should_Be_Refused(string time)
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        await page.DeleteAsync("app06", schedule: true);
        var row = page.RowWith("Jobs", "Target", "rg-mock-06 / app06");
        var state = await row.CellTextAsync("State");
        await row.ClickAsync();
        await SetDateTimeAsync(page, "Jobs.Schedule", time);
        await page.ClickIdAsync("Jobs.RescheduleSelected");
        await page.ExpectTextAsync("Jobs.Message", "は、今より後にしてください。");
        (await row.CellTextAsync("State")).Should().Be(state, "予定は変わらない");
    }

    [Theory]
    [MemberData(nameof(PastTimes))]
    public async Task Scheduling_A_Deletion_At_Now_Or_Earlier_Should_Be_Refused(string time)
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        await page.RowWith("Deployments", "AppName", "app08").ClickAsync();
        await page.ClickIdAsync("Deployments.Delete");
        await page.Id("Delete.ConfirmAppName").FillAsync("app08");
        await SetDateTimeAsync(page, "Delete.Schedule", time);
        await page.ExpectTextAsync("Delete.Blocked", "「予定に追加」: 予定の時刻");
        await page.ClickIdAsync("Delete.Schedule");
        await page.ExpectTextAsync("Delete.Error", "は、今より後にしてください。");
        await Assertions.Expect(page.Id("Panel.Delete")).ToBeVisibleAsync();
    }
}
