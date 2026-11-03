using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;
using Xunit;
using static TTDeployTool.Controller.Desktop.UITests.MockUiTests;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// 「この設定で新しくデプロイ」（No.84。オーナーの依頼、2026-11-03）のモックの UI テスト。終わったデプロイのジョブの記録に残した設定を、
/// ジョブの一覧・実行状況の画面・デプロイの一覧の行の詳細の 3 か所から「新しくデプロイ」の ① に読み込む。サインインするまで押せず、
/// 設定の無い記録（この機能より前のもの）は理由を出す。読み込む判断は Core の RedeployTests で確かめている。
/// </summary>
public class MockRedeployTests
{
    // いちばん新しい記録（デプロイ・完了）を、デプロイの一覧にある app03 のデプロイにし、設定を持たせる。3 番目に新しい記録（デプロイ・取り消し）は設定が無い
    private static readonly DeploymentTemplate Settings = new("", FixtureSamples.Subscription(3), FixtureSamples.ResourceGroup(3), FixtureSamples.AppName(3),
        FixtureSamples.Location, "postgresql", "timetracker", "", "0.75", "1.5", "0.5", "1.0", "0.25", "0.5", false, ExpiryMode.Default, null, "研修");

    private static readonly string Newest = $"{FixtureSamples.ResourceGroup(3)} / {FixtureSamples.AppName(3)}";

    private static readonly string WithoutSettings = $"rg-mock-{100 + Records - 3} / app{100 + Records - 3}";

    private static IReadOnlyList<JobRecord> History(IReadOnlyList<JobRecord> records) =>
    [
        .. records.Take(records.Count - 1),
        records[^1] with
        {
            Subscription = Settings.SubscriptionId!, ResourceGroup = Settings.ResourceGroup, AppName = Settings.AppName, Settings = Settings,
        },
    ];

    // 「新しくデプロイ」の ① に、記録の設定が入ったこと
    private static void ShouldOpenTheWizardWithTheSettings(AppSession app)
    {
        app.Find("Panel.Wizard");
        app.WaitForText("Settings.Message", t => t.StartsWith($"ジョブの設定（{Newest}）を入力しました。", StringComparison.Ordinal), "記録の設定を入力した");
        app.ValueOf("Target.ResourceGroup").Should().Be(Settings.ResourceGroup);
        app.ValueOf("Target.AppName").Should().Be(Settings.AppName);
    }

    [Fact]
    public void A_Finished_Deployment_Should_Load_Its_Settings_From_Three_Places() => WithFixture(app =>
    {
        // サインインする前は押せない（記録は見られる）
        app.Show("Jobs");
        app.WaitForText("Jobs.Count", t => t.Contains($"全 {Records} 件"), "記録の 45 件");
        app.SelectRowWith("Jobs.List", "Job.Target", Newest);
        app.IsEnabled("Jobs.Redeploy").Should().BeFalse("サインインするまで押せない");

        SignIn(app);
        Load(app);

        // 1. ジョブの一覧で 1 行だけ選ぶ
        app.Show("Jobs");
        app.SelectRowWith("Jobs.List", "Job.Target", Newest);
        AppSession.WaitUntil(() => app.IsEnabled("Jobs.Redeploy"), "設定のある終わったデプロイのジョブは押せる");
        app.Invoke("Jobs.Redeploy");
        ShouldOpenTheWizardWithTheSettings(app);

        // 設定の無い記録は押せず、理由を出す
        app.Show("Jobs");
        app.SelectRowWith("Jobs.List", "Job.Target", WithoutSettings);
        app.WaitForText("Jobs.RedeployNote", t => t.Contains("設定が残っていない"), "設定の無い記録は理由を出す");
        app.IsEnabled("Jobs.Redeploy").Should().BeFalse();

        // 2. 実行状況の画面
        app.SelectRowWith("Jobs.List", "Job.Target", Newest);
        app.Invoke("Jobs.Detail");
        app.Find("Panel.Status");
        app.Invoke("Status.Redeploy");
        ShouldOpenTheWizardWithTheSettings(app);

        // 3. デプロイの一覧の行の詳細（その対象の最後のデプロイの設定）
        app.Show("Deployments");
        app.Select("Deployments.PageSize", "50");
        if (app.IsEnabled("Deployments.ClearSelection"))
            app.Invoke("Deployments.ClearSelection");
        app.SelectRowWith("Deployments.Rows", "Row.AppName", Settings.AppName);
        AppSession.WaitUntil(() => app.IsEnabled("Row.Redeploy"), "その対象に設定のある終わったデプロイのジョブがあれば押せる");
        app.Invoke("Row.Redeploy");
        ShouldOpenTheWizardWithTheSettings(app);
    }, history: History);
}
