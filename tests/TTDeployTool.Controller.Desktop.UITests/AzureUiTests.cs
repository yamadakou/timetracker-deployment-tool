using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// 本物のサインイン（WAM）と Azure を使う、ローカルの半自動の UI テスト（詳細設計 6）。サインインだけ人が行う。
/// 料金が発生するため、TT_UITEST_SUBSCRIPTION_ID と TT_UITEST_RESOURCE_GROUP が無ければスキップする
/// （scripts/run-desktop-ui-tests.ps1 が、実行の前に確認してから設定する）。
/// </summary>
public class AzureUiTests
{
    // サインインの画面での操作と、デプロイ・削除を待つ上限
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(40);

    private static string? Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    [Fact]
    public void Sign_In_Then_Deploy_Check_And_Delete_On_Azure()
    {
        var subscription = Setting("TT_UITEST_SUBSCRIPTION_ID");
        var resourceGroup = Setting("TT_UITEST_RESOURCE_GROUP");
        Assert.SkipWhen(subscription == null || resourceGroup == null,
            "TT_UITEST_SUBSCRIPTION_ID と TT_UITEST_RESOURCE_GROUP が無いため、Azure を使う UI テストはスキップします（scripts/run-desktop-ui-tests.ps1）。");
        var appName = Setting("TT_UITEST_APP_NAME") ?? "ttui";
        var location = Setting("TT_UITEST_LOCATION") ?? "japaneast";

        AppSession.Run(app =>
        {
            // サインイン（前回のサインインを復元できなければ、利用者が WAM の画面でサインインする）
            if (!app.IsEnabled("SignIn.SignOut"))
            {
                Log("サインインの画面が開きます。サインインしてください（最大 5 分待ちます）。");
                app.Invoke("SignIn.SignIn");
                AppSession.WaitUntil(() => app.IsEnabled("SignIn.SignOut"), "サインインが終わりませんでした。", SignInTimeout);
            }
            app.Exists("SignIn.Error").Should().BeFalse();

            // サブスクリプションの読み込み（読めなかったテナントの注意は、結果に書き出す）
            app.SelectWhere("Target.Subscription", name => name.Contains(subscription!), $"サブスクリプション {subscription}", LoadTimeout);
            app.Exists("Target.Error").Should().BeFalse();
            Log($"サブスクリプションの一覧: {string.Join(" / ", app.ItemsOf("Target.Subscription"))}");
            if (app.Exists("Target.Notices"))
                Log($"一覧の注意: {app.TextOf("Target.Notices")}");

            // 「読み直す」を続けて押しても、一覧が重複せず、選んだサブスクリプションが残る
            app.Invoke("Target.Reload");
            app.Invoke("Target.Reload");
            AppSession.WaitUntil(() => app.IsEnabled("Target.Reload"), "読み直しが終わりませんでした。", LoadTimeout);
            var items = app.ItemsOf("Target.Subscription");
            items.Should().OnlyHaveUniqueItems();
            app.SelectedOf("Target.Subscription").Should().Contain(subscription);

            app.SetText("Target.ResourceGroup", resourceGroup!);
            app.SetText("Target.AppName", appName);
            app.SetText("Target.Location", location);
            app.WaitForText("Target.TargetError", t => t.Length == 0, "対象の誤りが無い");

            // まだ何も無いリソースグループの状態
            app.Show("Status");
            app.Invoke("Status.Refresh");
            AppSession.WaitUntil(() => app.Exists("Status.Error") || app.TextOf("Status.Summary").Contains("ありません"),
                "状態の確認の結果が表示されませんでした。", LoadTimeout);
            Log($"デプロイ前の状態: {(app.Exists("Status.Error") ? app.TextOf("Status.Error") : app.TextOf("Status.Summary"))}");

            // デプロイ（計画 → 実行）
            app.Show("Settings");
            app.SetText("Settings.DbPassword", "Ui" + Guid.NewGuid().ToString("N")[..12] + "!");
            app.SetText("Settings.TrackerPassword", "Ui" + Guid.NewGuid().ToString("N")[..12] + "!");
            app.Show("Deploy");
            app.Invoke("Deploy.Plan");
            AppSession.WaitUntil(() => app.Exists("Deploy.Error") || app.Exists("Deploy.Start"), "デプロイの計画が表示されませんでした。", LoadTimeout);
            app.Exists("Deploy.Error").Should().BeFalse(app.Exists("Deploy.Error") ? app.TextOf("Deploy.Error") : "");
            Log($"デプロイの計画: {app.TextOf("Deploy.Operations")}");
            app.Invoke("Deploy.Start");
            app.WaitForText("Job.Title", t => t.StartsWith("デプロイ"), "デプロイのジョブが表示される");
            var deployed = app.WaitForText("Job.Status", t => t.Contains("完了") || t.Contains("失敗"), "デプロイが終わる", JobTimeout);
            deployed.Should().Contain("完了", app.Exists("Job.Error") ? app.TextOf("Job.Error") : "");
            app.IsEnabled("Deploy.OpenEndpoint").Should().BeTrue("デプロイした Timetracker の URL がある");

            // デプロイした環境の状態
            app.Show("Status");
            app.Invoke("Status.Refresh");
            app.WaitForText("Status.Summary", t => t.Contains("Container Apps Environment"), "デプロイした環境が表示される", LoadTimeout);

            // 削除（計画 → アプリ名の入力 → 実行）
            app.Show("Delete");
            app.Invoke("Delete.Plan");
            app.Find("Delete.ConfirmAppName", LoadTimeout);
            app.IsEnabled("Delete.Start").Should().BeFalse("アプリ名を入れるまで削除できない");
            app.SetText("Delete.ConfirmAppName", appName);
            app.Invoke("Delete.Start");
            // 表示が削除のジョブに切り替わってから、終わるのを待つ（デプロイのジョブの「完了」を読まない）
            app.WaitForText("Job.Title", t => t.StartsWith("削除"), "削除のジョブが表示される");
            var deleted = app.WaitForText("Job.Status", t => t.Contains("完了") || t.Contains("失敗"), "削除が終わる", JobTimeout);
            deleted.Should().Contain("完了", app.Exists("Job.Error") ? app.TextOf("Job.Error") : "");

            app.Show("Status");
            app.Invoke("Status.Refresh");
            app.WaitForText("Status.Summary", t => t.Contains("ありません"), "削除した後は環境が無い", LoadTimeout);
        });
    }

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
