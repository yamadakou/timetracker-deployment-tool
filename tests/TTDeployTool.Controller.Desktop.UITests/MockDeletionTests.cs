using FluentAssertions;
using TTDeployTool.Controller.Desktop.Fixtures;
using TTDeployTool.Domain.Deployment;
using Xunit;
using static TTDeployTool.Controller.Desktop.UITests.MockUiTests;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// リソースグループだけの行の削除と、まだ始めていない削除のジョブの削除の設定の変更（No.86。オーナーの判断、2026-11-13、Blazor 版の
/// desktop-blazor-hybrid.md の 16.11）のモックの UI テスト。判断は Core の ResourceGroupOnlyTests・DeletionSettingsTests で確かめている。
/// </summary>
public class MockDeletionTests
{
    private const string EmptyGroup = "rg-mock-empty";

    [Fact]
    public void A_Resource_Group_Only_Row_Should_Delete_Just_The_Group() => WithFixture(app =>
    {
        SignIn(app);
        app.Show("Deployments");
        app.Invoke("Deployments.Load");
        // 管理タグがあり、本ツールのアプリのリソースが無いリソースグループも、一覧の 1 行になる
        app.WaitForText("Deployments.Page", t => t.Contains($"全 {Deployments + 1} 件"), "リソースグループだけの行も一覧に出る");
        app.Select("Deployments.PageSize", "50");
        app.SelectRowWith("Deployments.Rows", "Row.ResourceGroup", EmptyGroup);
        app.Invoke("Deployments.Delete");
        app.Find("Panel.Delete");

        // リソースグループだけを計画し、「リソースグループも削除する」は出さず、確かめはリソースグループ名
        AppSession.WaitUntil(() => app.Exists("Delete.ConfirmAppName"), "削除の計画ができる");
        app.TextOf("Delete.ConfirmLabel").Should().Be("リソースグループ名");
        app.Exists("Delete.DeleteResourceGroup").Should().BeFalse("リソースグループだけのときは、チェックを出さない");
        app.SetText("Delete.ConfirmAppName", EmptyGroup);
        app.Invoke("Delete.Start");
        app.Find("Panel.Status");
        app.WaitForText("Job.Status", t => t.StartsWith("完了", StringComparison.Ordinal), "リソースグループの削除が終わる", TimeSpan.FromSeconds(60));
    }, data: d => d with
    {
        WriteDelaySeconds = 0.5,
        ResourceGroups = [.. d.ResourceGroups!, new FixtureResourceGroup(FixtureSamples.SubscriptionA, EmptyGroup, FixtureSamples.Location,
            new Dictionary<string, string> { [ManagedTags.ManagedByKey] = ManagedTags.ManagedByValue })],
    });

    [Fact]
    public void A_Scheduled_Deletion_Should_Change_Whether_The_Group_Is_Deleted() => WithFixture(app =>
    {
        SignIn(app);
        Load(app);
        app.Select("Deployments.PageSize", "50");
        Delete(app, "app06", schedule: true);

        // ジョブの一覧から実行状況の画面を開き、「リソースグループも削除する」に広げる（確かめのダイアログ）
        app.SelectRowWith("Jobs.List", "Job.Target", "rg-mock-06 / app06");
        app.Invoke("Jobs.Detail");
        app.Find("Panel.Status");
        app.TextOf("Job.DeletionSettings").Should().Be("リソースグループは残す");
        app.Find("Job.EditDeleteResourceGroup").Patterns.Toggle.Pattern.Toggle();
        app.Invoke("Job.ChangeDeletion");
        app.FindAnywhere("PrimaryButton").Patterns.Invoke.Pattern.Invoke();
        app.WaitForText("Job.DeletionMessage", t => t.StartsWith("削除の設定を変えました", StringComparison.Ordinal), "削除の設定が変わる");
        app.TextOf("Job.DeletionSettings").Should().StartWith("リソースグループも削除する");
        app.TextOf("Job.Status").Should().StartWith("予定", "予定のまま");
    });
}
