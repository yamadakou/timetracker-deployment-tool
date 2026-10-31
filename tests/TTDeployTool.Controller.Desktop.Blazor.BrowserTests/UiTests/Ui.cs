using FluentAssertions;
using Microsoft.Playwright;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// WinUI 3 版の UI テスト（tests/TTDeployTool.Controller.Desktop.UITests）の操作を、Blazor の画面（テスト用のホスト）で同じように行う部品
/// （AppSession・MockUiTests の SignIn・Load・Delete・WithFixture に当たる。名前〔data-id〕は WinUI の AutomationId と同じ）。
/// </summary>
internal static class Ui
{
    public const int Deployments = 25;

    public const int Records = 45;

    public static ILocator Id(this IPage page, string id) => page.Locator($"[data-id='{id}']");

    public static Task ShowAsync(this IPage page, string name) => page.Id($"Nav.{name}").ClickAsync();

    public static Task ClickIdAsync(this IPage page, string id) => page.Id(id).ClickAsync();

    public static Task ExpectTextAsync(this IPage page, string id, string text) => Assertions.Expect(page.Id(id)).ToContainTextAsync(text);

    /// <summary>表の行（デプロイの一覧は Panel.Deployments、ジョブの一覧は Panel.Jobs）。</summary>
    public static ILocator Rows(this IPage page, string panel) => page.Locator($"[data-id='Panel.{panel}'] tbody tr");

    /// <summary>列の値が <paramref name="text"/> の行。</summary>
    public static ILocator RowWith(this IPage page, string panel, string key, string text) =>
        page.Rows(panel).Filter(new() { Has = page.Locator($"td[data-key='{key}']", new() { HasTextRegex = new System.Text.RegularExpressions.Regex($"^{System.Text.RegularExpressions.Regex.Escape(text)}$") }) });

    public static Task<string?> CellTextAsync(this ILocator row, string key) => row.Locator($"td[data-key='{key}']").TextContentAsync();

    public static ILocator PageText(this IPage page, string table) => page.Locator($"[data-pager='{table}'] .tt-page-text");

    public static ILocator Pager(this IPage page, string table, string text) => page.Locator($"[data-pager='{table}'] button", new() { HasText = text });

    public static Task PageSizeAsync(this IPage page, string table, string size) => page.Locator($"[data-pager='{table}'] select").SelectOptionAsync(size);

    public static Task TextTargetAsync(this IPage page, string table, string label) =>
        page.Locator($"select[data-filter-target='{table}']").SelectOptionAsync(new SelectOptionValue { Label = label });

    public static Task TextFilterAsync(this IPage page, string table, string text) => page.Locator($"input[data-filter='{table}']").FillAsync(text);

    public static Task ClearFiltersAsync(this IPage page, string panel) => page.Locator($"[data-id='Panel.{panel}'] button.tt-clear").ClickAsync();

    public static Task ResetLayoutAsync(this IPage page, string panel) => page.Locator($"[data-id='Panel.{panel}'] .tt-reset").ClickAsync();

    /// <summary>見出しを 3 回押して（昇順・降順・既定の順）、押した見出しの文字を返す（WinUI の SortByHeaderThreeTimes）。</summary>
    public static async Task<string> SortByHeaderThreeTimesAsync(this IPage page, string panel, string key)
    {
        var header = page.Locator($"[data-id='Panel.{panel}'] th[data-key='{key}']");
        foreach (var sort in new[] { "ascending", "descending", "none" })
        {
            await header.Locator("button.tt-sort").ClickAsync();
            await Assertions.Expect(header).ToHaveAttributeAsync("aria-sort", sort);
        }
        return (await header.Locator(".tt-head-text").TextContentAsync())!;
    }

    public static async Task SignInAsync(this IPage page)
    {
        await page.ShowAsync("Target");
        await page.ClickIdAsync("SignIn.SignIn");
        await page.ExpectTextAsync("SignIn.Account", "mock-user@example.com");
    }

    public static async Task LoadAsync(this IPage page)
    {
        await page.ShowAsync("Deployments");
        await page.ClickIdAsync("Deployments.Load");
        await Assertions.Expect(page.PageText("deployments")).ToContainTextAsync($"全 {Deployments} 件");
    }

    /// <summary>デプロイの一覧の行から削除の画面を開き、今すぐ削除するか、予定に追加する（予定は既定の 1 時間後。WinUI の MockUiTests.Delete）。</summary>
    public static async Task DeleteAsync(this IPage page, string appName, bool schedule)
    {
        await page.ShowAsync("Deployments");
        if (await page.Id("Deployments.ClearSelection").IsEnabledAsync())
            await page.ClickIdAsync("Deployments.ClearSelection");
        await page.RowWith("Deployments", "AppName", appName).ClickAsync();
        await page.ClickIdAsync("Deployments.Delete");
        await Assertions.Expect(page.Id("Panel.Delete")).ToBeVisibleAsync();
        await Assertions.Expect(page.Id("Delete.ConfirmAppName")).ToBeVisibleAsync();
        await page.Id("Delete.ConfirmAppName").FillAsync(appName);
        if (schedule)
        {
            await page.ClickIdAsync("Delete.Schedule");
            await Assertions.Expect(page.Id("Panel.Jobs")).ToBeVisibleAsync();
        }
        else
        {
            await page.ClickIdAsync("Delete.Start");
            await Assertions.Expect(page.Id("Panel.Status")).ToBeVisibleAsync();
        }
    }

    /// <summary>条件が満たされるまで待つ（WinUI の AppSession.WaitUntil）。</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(until, because);
            await Task.Delay(200);
        }
    }

    /// <summary>
    /// モックのデータ（<paramref name="data"/> で変えられる）と終わったジョブの記録を置いてテスト用のホストを起動する（サインインはしない。WinUI の MockUiTests.WithFixture）。
    /// <paramref name="layout"/> なら、保存した列の配置（ジョブの一覧。既定は結果の列を左に。<paramref name="jobsLayout"/> で変えられる）も置く。
    /// </summary>
    public static Task<BrowserHost> WithFixtureAsync(Func<FixtureData, FixtureData>? data = null, bool layout = true, IReadOnlyList<TableColumnLayout>? jobsLayout = null,
        int width = 1600, int height = 1000) =>
        BrowserHost.StartAsync(width, height, signIn: false, prepare: directory =>
        {
            var now = DateTimeOffset.UtcNow;
            var fixture = FixtureSamples.Data(Deployments, now) with { WriteDelaySeconds = 120 };
            File.WriteAllText(Path.Combine(directory, FixtureData.FileName), (data?.Invoke(fixture) ?? fixture).ToJson());
            new FileJobHistory(Path.Combine(directory, "job-history.json")).Records = FixtureSamples.JobRecords(Records, now);
            if (layout)
            {
                new FilePreferences(Path.Combine(directory, "preferences.json")).TableLayouts = new Dictionary<string, IReadOnlyList<TableColumnLayout>>
                {
                    ["jobs"] = jobsLayout ??
                    [
                        new("Kind", 110), new("Result", 320), new("Subscription", 200), new("Target", 200), new("State", 170), new("Time", 120), new("RegisteredAt", 120),
                        new("ChangedAt", 120), new("StartedAt", 120), new("Finished", 120), new("Duration", 0),
                    ],
                };
            }
        });

    /// <summary>何も置かずに起動する（WinUI の CiUiTests: モックのデータが無く、サインインできない）。<paramref name="prepare"/> で記録などを置ける。</summary>
    public static Task<BrowserHost> EmptyAsync(Action<string>? prepare = null) => BrowserHost.StartAsync(signIn: false, prepare: prepare ?? (_ => { }));
}
