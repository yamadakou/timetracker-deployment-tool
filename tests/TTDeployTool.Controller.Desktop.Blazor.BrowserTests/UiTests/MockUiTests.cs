using FluentAssertions;
using Microsoft.Playwright;
using TTDeployTool.Controller.Desktop.Fixtures;
using static TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests.Ui;

namespace TTDeployTool.Controller.Desktop.Blazor.BrowserTests.UiTests;

/// <summary>
/// WinUI 3 版の MockUiTests と同じ内容: モックのデータ（デプロイ 25 件・終わったジョブの記録 45 件）でサインインして、行のある表（並べ替え・絞り込み・ページング・
/// 6 行の表示）と、予定・待機中・一時停止・実行中のジョブを確かめる。
/// </summary>
public sealed class MockUiTests
{
    [Fact]
    public async Task The_Deployment_List_Should_Page_Sort_Filter_And_Show_The_Row()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        await Assertions.Expect(page.Rows("Deployments")).ToHaveCountAsync(20);
        await Assertions.Expect(page.PageText("deployments")).ToContainTextAsync("1〜20 件目を表示（全 25 件）・1 / 2 ページ");
        await page.Pager("deployments", "次のページ").ClickAsync();
        await Assertions.Expect(page.PageText("deployments")).ToContainTextAsync("21〜25 件目");
        await Assertions.Expect(page.Rows("Deployments")).ToHaveCountAsync(5);
        await page.Pager("deployments", "前のページ").ClickAsync();
        await Assertions.Expect(page.PageText("deployments")).ToContainTextAsync("1〜20 件目");
        await page.PageSizeAsync("deployments", "50");
        await Assertions.Expect(page.Rows("Deployments")).ToHaveCountAsync(Deployments);
        await Assertions.Expect(page.PageText("deployments")).ToContainTextAsync("1 / 1 ページ");

        // 列の中身（サブスクリプション・グループ・期限・状態）
        var first = page.RowWith("Deployments", "AppName", "app00");
        (await first.CellTextAsync("Group")).Should().Be("研修");
        (await page.RowWith("Deployments", "AppName", "app02").CellTextAsync("Group")).Should().BeEmpty("グループの無い行のセルは空");
        (await first.CellTextAsync("ExpiresAt")).Should().Be("期限なし");
        (await page.RowWith("Deployments", "AppName", "app04").CellTextAsync("State")).Should().NotBe(await first.CellTextAsync("State"), "止めたデプロイの状態は、動いているものと違う");
        (await page.Rows("Deployments").AllInnerTextsAsync()).Should().Contain(t => t.Contains($"{FixtureSamples.SubscriptionNameA}（{FixtureSamples.SubscriptionA}）"));

        // 並べ替えと、含む文字で行が減る・外すと戻る
        (await page.SortByHeaderThreeTimesAsync("Deployments", "AppName")).Should().Be("アプリ名");
        await page.TextTargetAsync("deployments", "リソースグループ");
        await page.TextFilterAsync("deployments", "rg-mock-0");
        await Assertions.Expect(page.Rows("Deployments")).ToHaveCountAsync(10);
        await Assertions.Expect(page.PageText("deployments")).ToContainTextAsync("絞り込んだ 10 件 / 全 25 件");
        await page.ClearFiltersAsync("Deployments");
        await Assertions.Expect(page.Rows("Deployments")).ToHaveCountAsync(Deployments);

        // 1 行選ぶと詳細（パスワードはモックのシークレット）、2 行選ぶとまとめての操作
        await page.RowWith("Deployments", "AppName", "app00").ClickAsync();
        await page.ClickIdAsync("Row.ShowPasswords");
        await Assertions.Expect(page.Id("Row.DbPassword")).ToHaveTextAsync("••••••••");
        await page.ClickIdAsync("Row.TogglePasswords");
        await Assertions.Expect(page.Id("Row.DbPassword")).ToHaveTextAsync("Db-Pass-00");
        await page.RowWith("Deployments", "AppName", "app02").ClickAsync();
        await Assertions.Expect(page.Id("Row.ShowPasswords")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Id("Deployments.ChangeExpiry")).ToBeEnabledAsync();
        await Assertions.Expect(page.Id("Deployments.Delete")).ToBeEnabledAsync();
    }

    [Fact]
    public async Task The_Job_List_Should_Have_Every_State_And_Page_Sort_Filter_While_Jobs_Run()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.SignInAsync();
        await page.LoadAsync();
        await page.PageSizeAsync("deployments", "50");
        await Assertions.Expect(page.Rows("Deployments")).ToHaveCountAsync(Deployments);

        // 実行中 2 件（同時に実行できる数は 2）・待機中 1 件・予定 2 件（うち 1 件を一時停止）。書き込みは 1 回 120 秒かかるため、テストの間は実行中のまま
        await page.DeleteAsync("app00", schedule: false);
        await page.DeleteAsync("app02", schedule: false);
        await page.DeleteAsync("app04", schedule: false);
        await page.DeleteAsync("app06", schedule: true);
        await page.DeleteAsync("app08", schedule: true);
        await page.ShowAsync("Deployments");
        await Assertions.Expect(page.RowWith("Deployments", "AppName", "app00").Locator("td[data-key='Job']")).ToContainTextAsync("削除");

        await page.ShowAsync("Jobs");
        await page.ExpectTextAsync("Jobs.Count", $"全 {Records + 5} 件");
        await Assertions.Expect(page.Id("Jobs.CancelSelected")).ToBeDisabledAsync();
        (await page.Id("Jobs.SignInNote").CountAsync()).Should().Be(0, "サインインした後は、操作できない旨を出さない");

        // ページング（20 件ずつで 3 ページ）
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(20);
        await page.Pager("jobs", "次のページ").ClickAsync();
        await page.Pager("jobs", "次のページ").ClickAsync();
        await page.ExpectTextAsync("Jobs.Count", "41〜50 件目");
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(10);
        await page.Pager("jobs", "前のページ").ClickAsync();
        await page.ExpectTextAsync("Jobs.Count", "21〜40 件目");
        await page.PageSizeAsync("jobs", "50");
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(Records + 5);

        // 予定の 1 件を一時停止し、どの状態の行もそろう
        await page.RowWith("Jobs", "Target", "rg-mock-08 / app08").ClickAsync();
        await page.ClickIdAsync("Jobs.PauseSelected");
        await Assertions.Expect(page.RowWith("Jobs", "Target", "rg-mock-08 / app08").Locator("td[data-key='State']")).ToHaveTextAsync("一時停止");
        await page.ClickIdAsync("Jobs.ClearSelection");
        await WaitUntilAsync(async () =>
        {
            var states = string.Join("\n", await page.Rows("Jobs").AllInnerTextsAsync());
            return new[] { "実行中", "待機中", "予定（", "一時停止", "完了", "失敗", "取り消し", "停止" }.All(states.Contains);
        }, "予定・待機中・一時停止・実行中・終わった状態の行がそろう");

        // ジョブが動いている間に、並べ替え・絞り込みをしても落ちない
        (await page.SortByHeaderThreeTimesAsync("Jobs", "State")).Should().Be("状態");
        await page.TextTargetAsync("jobs", "リソースグループ / アプリ名");
        await page.TextFilterAsync("jobs", "rg-mock-0");
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(5);
        await page.RowWith("Jobs", "Target", "rg-mock-04 / app04").ClickAsync();
        await Assertions.Expect(page.Id("Jobs.PauseSelected")).ToBeEnabledAsync();
        await Assertions.Expect(page.Id("Jobs.CancelSelected")).ToBeEnabledAsync();
        await page.ClickIdAsync("Jobs.ClearSelection");
        await page.RowWith("Jobs", "Target", "rg-mock-08 / app08").ClickAsync();
        await Assertions.Expect(page.Id("Jobs.ResumeSelected")).ToBeEnabledAsync();
        await page.ClickIdAsync("Jobs.ClearSelection");
        await page.ClearFiltersAsync("Jobs");
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(Records + 5);
        (await page.Locator("#blazor-error-ui").IsVisibleAsync()).Should().BeFalse("画面の中の例外で落ちていない");
    }

    [Fact]
    public async Task Long_Text_Should_Stop_At_Six_Lines_With_A_Tool_Tip()
    {
        await using var host = await WithFixtureAsync();
        var page = host.Page;
        await page.ShowAsync("Jobs");
        await page.ExpectTextAsync("Jobs.Count", $"全 {Records} 件");
        await page.PageSizeAsync("jobs", "50");
        await Assertions.Expect(page.Rows("Jobs")).ToHaveCountAsync(Records);
        // いちばん新しい 2 件の結果（誤り）は長い文字。1 行の文字（種類）の高さと比べる
        var row = page.RowWith("Jobs", "Target", $"rg-mock-{100 + Records - 1} / app{100 + Records - 1}");
        var result = row.Locator("td[data-key='Result'] .tt-cell");
        var line = (await row.Locator("td[data-key='Kind'] .tt-cell").BoundingBoxAsync())!.Height;
        var height = (await result.BoundingBoxAsync())!.Height;
        ((double)height / line).Should().BeInRange(5, 6.6, $"6 行で切れる（高さ {height}・1 行 {line}）");
        await result.HoverAsync();
        (await result.GetAttributeAsync("title")).Should().Be(FixtureSamples.LongText, "切れたセルは、マウスを置くと全体が出る");
        var shortRow = page.RowWith("Jobs", "Target", $"rg-mock-{100 + Records - 4} / app{100 + Records - 4}");
        var shortCell = shortRow.Locator("td[data-key='Result'] .tt-cell");
        (await shortCell.BoundingBoxAsync())!.Height.Should().BeLessThan(line * 2, "短い文字は 1 行");
        await shortCell.HoverAsync();
        (await shortCell.GetAttributeAsync("title")).Should().BeNull("切れていなければツールチップは出さない");
    }
}
