using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// 本物の Azure を使う UI テスト（詳細設計 6・7）。ローカルの半自動（WAM のサインインだけ人が行う。scripts/run-desktop-ui-tests.ps1）と、
/// UI テストの System Test（Azure CLI のサインインを使うテスト用のビルド。.github/workflows/desktop-system-test.yml）で実行する。
/// 実機の確認で見つかった問題（desktop-jobs.md の P1〜P4）も確かめる。
/// 料金が発生するため、TT_UITEST_SUBSCRIPTION_ID と TT_UITEST_RESOURCE_GROUP が無ければスキップする。
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
            if (app.ScreenText().Split('\n').FirstOrDefault(l => l.StartsWith("テナント ") && l.Contains("読めませんでした")) is { } notice)
                Log($"一覧の注意: {notice}");

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

            // P1: パスワードを入れずに計画するとエラーが出て、直して計画し直すとエラーの領域ごと消える
            app.Show("Deploy");
            app.Invoke("Deploy.Plan");
            app.WaitForText("Deploy.Error", t => t.Contains("設定に誤りがあります"), "パスワードの無い計画でエラーが出る");
            app.Show("Settings");
            app.SetText("Settings.DbPassword", "Ui" + Guid.NewGuid().ToString("N")[..12] + "!");
            app.SetText("Settings.TrackerPassword", "Ui" + Guid.NewGuid().ToString("N")[..12] + "!");

            // デプロイ（計画 → 実行）。P3: 計画の間、処理中の表示と、Azure の何を読んでいるかが出る
            app.Show("Deploy");
            app.Invoke("Deploy.Plan");
            var planning = CollectBusy(app, "Deploy.Busy");
            Log($"計画の処理中の表示: {string.Join(" → ", planning)}");
            planning.Should().Contain(t => t.Contains("確認しています") || t.Contains("読んでいます"), "Azure を読む段階を知らせる");
            AppSession.WaitUntil(() => app.Exists("Deploy.Error") || app.Exists("Deploy.Start"), "デプロイの計画が表示されませんでした。", LoadTimeout);
            app.Exists("Deploy.Error").Should().BeFalse(app.Exists("Deploy.Error") ? app.TextOf("Deploy.Error") : "P1: 計画し直すと、前のエラーの領域が消える");
            // 計画の一覧（ItemsControl）は UI Automation の要素にならないため、ウィンドウの中の文字列から読む
            var screen = app.ScreenText();
            screen.Should().Contain("作成する").And.Contain($"{appName}-env", "作るリソースが計画に出る");
            // 計画の各行は、操作・名前・種類を横に並べた TextBlock なので、操作の数を書き出す
            Log($"デプロイの計画: 作成する {screen.Split('\n').Count(l => l == "作成する")} 件");
            app.Invoke("Deploy.Start");
            app.WaitForText("Job.Title", t => t.StartsWith("デプロイ"), "デプロイのジョブが表示される");

            // P2: 実行中に状態を確認すると、まだ反映されていない場合がある旨が出て、環境が無ければ永続化の行は出ない
            app.Show("Status");
            app.Invoke("Status.Refresh");
            app.WaitForText("Status.Note", t => t.Contains("実行中のため"), "実行中の注意が出る", LoadTimeout);
            if (app.TextOf("Status.Summary").Contains("ありません"))
                app.TextOf("Status.Persistence").Should().NotContain("永続化していません", "環境が無いときは永続化の行を出さない");
            app.Show("Deploy");
            app.WaitForText("Job.Title", t => t.StartsWith("デプロイ"), "デプロイの画面に戻ると、デプロイのジョブが表示される");
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

            // P4: 同じ対象を削除している間、デプロイの画面には削除のジョブもデプロイの結果も出ず、「Timetracker を開く」は押せない
            app.Show("Deploy");
            app.Exists("Job.Title").Should().BeFalse("デプロイの画面に削除のジョブを出さない");
            app.IsEnabled("Deploy.OpenEndpoint").Should().BeFalse("削除した環境の URL は開けない");
            app.Show("Delete");
            app.WaitForText("Job.Title", t => t.StartsWith("削除"), "削除の画面には削除のジョブが表示される");
            var deleted = app.WaitForText("Job.Status", t => t.Contains("完了") || t.Contains("失敗"), "削除が終わる", JobTimeout);
            deleted.Should().Contain("完了", app.Exists("Job.Error") ? app.TextOf("Job.Error") : "");

            app.Show("Status");
            app.Invoke("Status.Refresh");
            app.WaitForText("Status.Summary", t => t.Contains("ありません"), "削除した後は環境が無い", LoadTimeout);
        });
    }

    // 処理中の表示の段階を、表示が消えるまで集める（短い間隔で読む）
    private static List<string> CollectBusy(AppSession app, string automationId)
    {
        var texts = new List<string>();
        var deadline = DateTime.UtcNow + LoadTimeout;
        AppSession.WaitUntil(() => app.Exists(automationId), $"{automationId} が表示されませんでした。", TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline && app.Exists(automationId))
        {
            try
            {
                var text = app.TextOf(automationId);
                if (text.Length > 0 && (texts.Count == 0 || texts[^1] != text))
                    texts.Add(text);
            }
            catch (Exception)
            {
                // 読んでいる間に表示が消えた
            }
            Thread.Sleep(100);
        }
        return texts;
    }

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
