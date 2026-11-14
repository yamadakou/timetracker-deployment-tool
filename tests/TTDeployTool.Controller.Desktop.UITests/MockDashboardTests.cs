using FluentAssertions;
using Xunit;
using static TTDeployTool.Controller.Desktop.UITests.MockUiTests;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// 件数のカードと期限の残りと延長（No.86。オーナーの判断、2026-11-13、Blazor 版の desktop-blazor-hybrid.md の 16.12）のモックの UI テスト。
/// 件数と絞り込みの判断は Core の ModernDesignTests で確かめている。ここでは、画面の数・カードの「絞り込む」・「+7 日」が Core につながることを見る。
/// </summary>
public class MockDashboardTests
{
    // モックのデータ（FixtureSamples）: 期限は 4 件ごとに なし・3 日後・40 日後・90 日後。25 件のうち 3 日後（7 日以内）は 6 件
    private const int ExpiringSoon = 6;

    // 終わったジョブの記録 45 件（1 時間ずつ前に終わった。どれも 7 日以内）: 完了・失敗・取り消し・停止の順。完了は 12 件、失敗は 11 件
    private const int RecentSucceeded = 12;

    private const int RecentFailed = 11;

    [Fact]
    public void The_Deployment_Cards_Should_Count_And_Filter_And_A_Row_Should_Extend_Its_Expiry() => WithFixture(app =>
    {
        SignIn(app);
        Load(app);
        app.Select("Deployments.PageSize", "50");
        app.TextOf("Deployments.TotalCount").Should().Be($"{Deployments}");
        app.TextOf("Deployments.ExpiringSoonCount").Should().Be($"{ExpiringSoon}");

        // 「期限が 7 日以内」のカードで絞り込み、もう一度押すと外す
        app.Invoke("Deployments.Filter.ExpiringSoon");
        app.WaitForText("Deployments.Page", t => t.Contains($"絞り込んだ {ExpiringSoon} 件 / 全 {Deployments} 件"), "期限の近いものだけに絞る");
        app.Invoke("Deployments.Filter.ExpiringSoon");
        app.WaitForText("Deployments.Page", t => t.Contains($"（全 {Deployments} 件）"), "もう一度押すと外す");

        // 期限の残りと「+7 日」（3 日後の期限を延ばす）
        app.SelectRowWith("Deployments.Rows", "Row.AppName", "app01");
        app.WaitForText("Row.DaysLeft", t => t.StartsWith("あと ", StringComparison.Ordinal), "期限の残りを出す");
        var before = app.TextOf("Row.DaysLeft");
        string Expiry() => Cell(app.RowWith("Deployments.Rows", "Row.AppName", "app01"), "Row.Expiry").Name;
        var expiry = Expiry();
        app.Invoke("Row.Extend7");
        AppSession.WaitUntil(() => app.TextOf("Row.DaysLeft") != before, "「+7 日」で期限の残りが延びる", TimeSpan.FromSeconds(60));
        int.Parse(app.TextOf("Row.DaysLeft").Split(' ')[1]).Should().BeGreaterThan(int.Parse(before.Split(' ')[1]) + 5, "7 日延びる");
        Expiry().Should().NotBe(expiry, "一覧の期限日も変わる");
    }, data: d => d with { WriteDelaySeconds = 0.3 });

    [Fact]
    public void The_Job_Cards_Should_Count_And_Filter_Without_Signing_In() => WithFixture(app =>
    {
        app.Show("Jobs");
        app.WaitForText("Jobs.Count", t => t.Contains($"全 {Records} 件"), "記録の 45 件");
        app.TextOf("Jobs.RecentSucceededCount").Should().Be($"{RecentSucceeded}");
        app.TextOf("Jobs.RecentFailedCount").Should().Be($"{RecentFailed}");
        app.TextOf("Jobs.RunningCount").Should().Be("0");

        app.Invoke("Jobs.Filter.RecentFailed");
        app.WaitForText("Jobs.Count", t => t.Contains($"絞り込んだ {RecentFailed} 件 / 全 {Records} 件"), "最近の失敗だけに絞る");
        app.Invoke("Jobs.Filter.RecentFailed");
        app.WaitForText("Jobs.Count", t => t.Contains($"（全 {Records} 件）"), "もう一度押すと外す");
    });

    [Fact]
    public void The_Deployment_List_Should_Show_Cost_Estimates_In_Yen_Or_Dollars() => WithFixture(app =>
    {
        // 料金の目安（No.87。スペックから。UI テストのビルドはアプリに入れた価格）: 行ごとの期限まで・1 日あたり、上部の全体と今月の見込み、円・USD の切り替え
        SignIn(app);
        Load(app);
        app.Select("Deployments.PageSize", "50");
        app.WaitForText("Deployments.CostTotal", t => t.StartsWith("約 ", StringComparison.Ordinal) && t.EndsWith(" 円", StringComparison.Ordinal), "全体の料金の目安を円で出す");
        app.TextOf("Deployments.CostThisMonth").Should().EndWith(" 円");
        string Cost() => Cell(app.RowWith("Deployments.Rows", "Row.AppName", "app02"), "Row.Cost").Name;
        AppSession.WaitUntil(() => Cost().Contains('円'), "行の料金の目安を出す");

        app.Select("Deployments.CostCurrency", "USD");
        app.WaitForText("Deployments.CostTotal", t => t.Contains('$'), "USD に切り替える");
        AppSession.WaitUntil(() => Cost().Contains('$'), "行も USD になる");
    });
}
