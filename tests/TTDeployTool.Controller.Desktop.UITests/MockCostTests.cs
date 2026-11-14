using FluentAssertions;
using Xunit;
using static TTDeployTool.Controller.Desktop.UITests.MockUiTests;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// 料金の目安とスペックのスライダー（No.86。オーナーの判断、2026-11-13、Blazor 版の desktop-blazor-hybrid.md の 16.12）のモックの UI テスト。
/// UI テスト用のビルドは、Azure の価格表を読まず、アプリに入れた価格を使う（App.xaml.cs）。金額の計算は Core の ModernDesignTests で確かめている。
/// </summary>
public class MockCostTests
{
    // ④ スペックと期限まで進める（モックのサインインで、パスワードは画面で入れる）
    internal static void OpenSpec(AppSession app)
    {
        SignIn(app);
        app.Show("Wizard");
        app.Find("Panel.Wizard");
        // モックのサブスクリプションは 2 つあるため、どちらかを選ぶまで次へ進めない
        AppSession.WaitUntil(() => app.ItemsOf("Target.Subscription").Count > 0, "サブスクリプションの一覧が読まれる");
        app.Select("Target.Subscription", app.ItemsOf("Target.Subscription")[0]);
        app.SetText("Target.ResourceGroup", "rg-mock-cost");
        app.SetText("Target.AppName", "cost");
        app.SetText("Target.Location", "japaneast");
        Next(app, "DB（2");
        Next(app, "パスワード（3");
        app.SetText("Settings.DbPassword", "UiTestDbPass1!");
        app.SetText("Settings.TrackerPassword", "UiTestTracker1!");
        Next(app, "スペックと利用可能期限（4");
    }

    private static void Next(AppSession app, string step)
    {
        AppSession.WaitUntil(() => app.IsEnabled("Wizard.Next"), $"{step} へ進める");
        app.Invoke("Wizard.Next");
        app.WaitForText("Wizard.Step", t => t.StartsWith(step, StringComparison.Ordinal), $"ウィザードの {step} の画面");
    }

    [Fact]
    public void The_Spec_Sliders_Should_Change_The_Spec_And_The_Cost() => WithFixture(app =>
    {
        OpenSpec(app);
        app.WaitForText("Cost.Total", t => t.Length > 0, "料金の目安を出す");
        app.TextOf("Cost.Source").Should().Contain("アプリに入れた", "UI テストのビルドは Azure の価格表を読まない");
        var total = app.TextOf("Cost.Total");

        // TimeTracker のスライダーを 2.0 vCPU（いちばん右。初期値は 1.5）にすると、CPU とメモリ（CPU の 2 倍）の欄と、料金が変わる
        app.TextOf("Settings.TtSpecText").Should().Be("1.5 vCPU / 3 GiB", "初期値");
        app.Find("Settings.TtSpec").Patterns.RangeValue.Pattern.SetValue(7);
        app.WaitForText("Settings.TtSpecText", t => t == "2 vCPU / 4 GiB", "スライダーの値を出す");
        app.ValueOf("Settings.TtCpu").Should().Be("2.0");
        AppSession.WaitUntil(() => app.TextOf("Cost.Total") != total, "スペックを変えると料金の目安が変わる");

        // 単位を USD にする
        app.Select("Cost.Currency", "USD");
        AppSession.WaitUntil(() => app.TextOf("Cost.Total").Contains('$') || app.TextOf("Cost.Total").Contains("USD"), "USD で出す");
    });
}
