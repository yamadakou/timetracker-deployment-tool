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
            // サインイン（前回のサインインを復元できなければ、利用者が WAM の画面でサインインする）。
            // 起動時の前回のサインインの確認が終わるまでは、どちらのボタンも押せないため、終わるのを待ってから決める（Desktop System Test、2026-10-26）
            AppSession.WaitUntil(() => app.IsEnabled("SignIn.SignOut") || app.IsEnabled("SignIn.SignIn"), "前回のサインインの確認が終わりませんでした。", LoadTimeout);
            if (!app.IsEnabled("SignIn.SignOut"))
            {
                Log("サインインの画面が開きます。サインインしてください（最大 5 分待ちます）。");
                app.Invoke("SignIn.SignIn");
                AppSession.WaitUntil(() => app.IsEnabled("SignIn.SignOut"), "サインインが終わりませんでした。", SignInTimeout);
            }
            app.Exists("SignIn.Error").Should().BeFalse();

            // 7f: デプロイはウィザード（① 対象 → ② DB → ③ パスワード → ④ スペックと利用可能期限 → ⑤ 確認と実行）で行う
            app.IsEnabled("Nav.Deployments").Should().BeTrue("サインインすると、デプロイの一覧を使える");
            app.Show("Wizard");
            WizardStep(app, "①");
            // サブスクリプションの読み込み（読めなかったテナントの注意は、結果に書き出す）
            app.SelectWhere("Target.Subscription", name => name.Contains(subscription!), $"サブスクリプション {subscription}", LoadTimeout);
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

            // ① 対象: リソースグループを入れるまで次へ進めない
            app.IsEnabled("Wizard.Next").Should().BeFalse("リソースグループが無ければ次へ進めない");
            app.SetText("Target.ResourceGroup", resourceGroup!);
            app.SetText("Target.AppName", appName);
            app.SetText("Target.Location", location);
            Next(app, "②");
            // ② DB は既定のまま
            Next(app, "③");
            // ③ パスワード: 入れるまで次へ進めない（誤りはこの画面に出る）
            app.IsEnabled("Wizard.Next").Should().BeFalse("パスワードが無ければ次へ進めない");
            // 誤りの一覧は、画面を切り替えた直後には UI Automation に出ていないことがあるため、出るのを待つ
            app.WaitForScreenText(t => t.Contains("db-password が不足または短すぎます。"), "パスワードの誤りがこの画面に出る");
            var dbPassword = "Ui" + Guid.NewGuid().ToString("N")[..12] + "!";
            app.SetText("Settings.DbPassword", dbPassword);
            app.SetText("Settings.TrackerPassword", "Ui" + Guid.NewGuid().ToString("N")[..12] + "!");
            Next(app, "④");
            // ④ 利用可能期限日: 「日付を指定する」で日付の入力が現れ、既定に戻すと消える
            app.TextOf("Settings.ExpiryNotice").Should().Contain("今日から 32 日後");
            app.Select("Settings.Expiry", "日付を指定する");
            AppSession.WaitUntil(() => app.Exists("Settings.ExpiryDate"), "期限日の入力が表示される");
            app.Select("Settings.Expiry", SelectedExpiryDefault(app));
            AppSession.WaitUntil(() => !app.Exists("Settings.ExpiryDate"), "期限日の入力が消える");
            app.IsEnabled("Settings.CopyMarkdown").Should().BeTrue("どの画面でも設定をコピーできる");

            // ⑤ 確認と実行: 開くと計画を作る。P3: 計画の間、処理中の表示と、Azure の何を読んでいるかが出る
            app.Invoke("Wizard.Next");
            var planning = CollectBusy(app, "Deploy.Busy");
            Log($"計画の処理中の表示: {string.Join(" → ", planning)}");
            planning.Should().Contain(t => t.Contains("確認しています") || t.Contains("読んでいます"), "Azure を読む段階を知らせる");
            AppSession.WaitUntil(() => app.Exists("Deploy.Error") || app.Exists("Deploy.Start"), "デプロイの計画が表示されませんでした。", LoadTimeout);
            app.Exists("Deploy.Error").Should().BeFalse(app.Exists("Deploy.Error") ? app.TextOf("Deploy.Error") : "");
            // 計画の一覧（ItemsControl）は UI Automation の要素にならないため、ウィンドウの中の文字列から読む
            var screen = app.ScreenText();
            screen.Should().Contain("作成する").And.Contain($"{appName}-env", "作るリソースが計画に出る");
            Log($"デプロイの計画: 作成する {screen.Split('\n').Count(l => l == "作成する")} 件");
            var template = "UI テスト " + Guid.NewGuid().ToString("N")[..6];
            app.SetText("Settings.TemplateName", template);
            app.Invoke("Settings.SaveTemplate");
            app.WaitForText("Settings.Message", t => t.Contains($"テンプレート「{template}」を保存しました"), "テンプレートとして保存できる");

            // 今すぐ実行 → ウィザードの完了の表示（Q4・No.70）→「実行状況（ログ）を見る」で実行状況の画面。
            // P2: 実行中は、まだ反映されていない場合がある旨が出て、環境が無ければ永続化の行は出ない
            app.Invoke("Deploy.Start");
            WizardStep(app, "完了");
            app.TextOf("Wizard.CompletedText").Should().StartWith("デプロイ（");
            app.TextOf("Wizard.ShowJob").Should().Be("実行状況（ログ）を見る");
            app.Invoke("Wizard.ShowJob");
            app.Find("Panel.Status");
            app.WaitForText("Job.Title", t => t.StartsWith("デプロイ"), "実行状況の画面にデプロイのジョブが出る");
            app.WaitForText("Status.Note", t => t.Contains("実行中のため"), "実行中の注意が出る", LoadTimeout);
            if (app.TextOf("Status.Summary").Contains("ありません"))
                app.TextOf("Status.Persistence").Should().NotContain("永続化していません", "環境が無いときは永続化の行を出さない");
            var deployed = app.WaitForText("Job.Status", t => t.Contains("完了") || t.Contains("失敗"), "デプロイが終わる", JobTimeout);
            deployed.Should().Contain("完了", app.Exists("Job.Error") ? app.TextOf("Job.Error") : "");
            app.IsEnabled("Deploy.OpenEndpoint").Should().BeTrue("デプロイした Timetracker の URL がある");
            // ジョブが終わると、Azure の状態を読み直す
            app.WaitForText("Status.Summary", t => t.Contains("Container Apps Environment"), "デプロイした環境が表示される", LoadTimeout);

            // 完了の表示から開いた実行状況の「戻る」は、ウィザードに戻る（No.71）。完了の表示に URL が出ている
            app.TextOf("Status.BackToList").Should().Be("新しくデプロイに戻る");
            app.Invoke("Status.BackToList");
            app.Find("Panel.Wizard");
            WizardStep(app, "完了");
            app.Exists("WizardJob.Endpoint").Should().BeTrue("終わったデプロイの URL が完了の表示に出る");

            // 7d-2: デプロイの一覧（Resource Graph に反映される前でも、このアプリのジョブの結果で出る）。パスワードの表示と、期限の変更
            app.Show("Deployments");
            // No.63: 絞り込みは表の見出しの「▽」から
            app.Invoke("Deployments.FilterResourceGroup");
            app.SetText("Deployments.ResourceGroupFilter", resourceGroup!);
            app.Invoke("Deployments.ResourceGroupFilterClose");
            app.Invoke("Deployments.Load");
            // 読み込みが終わるまで待つ（件数の表示は、デプロイの直後に重ねた結果で読み込む前から出ているため、それでは待てない。Desktop System Test、2026-10-26）
            AppSession.WaitUntil(() => app.IsEnabled("Deployments.Load"), "デプロイの一覧の読み込みが終わりませんでした。", LoadTimeout);
            app.WaitForText("Deployments.Page", t => t.Contains("件目"), "一覧にデプロイが出る", LoadTimeout);
            Log($"デプロイの一覧: {app.TextOf("Deployments.Page")}、状態 {app.TextOf("Row.State")}、期限 {app.TextOf("Row.Expiry")}");
            app.TextOf("Row.AppName").Should().Be(appName);
            // 1 行だけ選ぶと、表の下に詳細（パスワードの表示など）が出る
            app.SelectRow("Deployments.Rows");
            app.Invoke("Row.ShowPasswords");
            app.WaitForText("Row.DbPassword", t => t == "••••••••", "パスワードは伏せ字で出る", LoadTimeout);
            app.Invoke("Row.TogglePasswords");
            app.WaitForText("Row.DbPassword", t => t == dbPassword, "表示すると、デプロイした DB のパスワードが出る");
            // 選んだ行の期限を変える（まとめての操作）
            app.SetChecked("Deployments.BatchNoExpiry", true);
            app.Invoke("Deployments.ChangeExpiry");
            app.WaitForText("Deployments.BatchMessage", t => t.Contains("1 件の利用可能期限を変えました"), "期限を変えられる", LoadTimeout);
            app.TextOf("Row.Expiry").Should().Be("期限なし");

            // 一覧の「削除」→ 削除の画面（開くと計画を作る）。No.53: 本ツールのリソースを消した後の空のリソースグループも消す
            // 期限を変えて表示し直しても選択は保つ（念のため選び直す）。1 行なら今の削除の画面
            app.SelectRow("Deployments.Rows");
            app.Invoke("Deployments.Delete");
            app.Find("Panel.Delete");
            app.Find("Delete.ConfirmAppName", LoadTimeout);
            app.SetChecked("Delete.DeleteResourceGroup", true);
            app.Invoke("Delete.Plan");
            app.Find("Delete.ConfirmAppName", LoadTimeout);
            app.ScreenText().Should().Contain($"リソースグループ {resourceGroup} も削除します。元に戻せません。");
            app.IsEnabled("Delete.Start").Should().BeFalse("アプリ名を入れるまで削除できない");
            app.IsEnabled("Delete.CopyAppName").Should().BeTrue("アプリ名をコピーできる");
            app.SetText("Delete.ConfirmAppName", appName);
            app.Invoke("Delete.Start");
            // 今すぐ削除 → 実行状況の画面
            app.Find("Panel.Status");
            app.WaitForText("Job.Title", t => t.StartsWith("削除"), "実行状況の画面に削除のジョブが出る");

            // 7b: ジョブのある対象は計画できない。開き直したウィザードは完了の表示のまま（Q13）。「続けてデプロイ」で ① に戻ると、
            // 同じ対象のジョブがあることを ① で知らせる（No.70）。⑤ まで進めても計画できない
            app.Show("Wizard");
            WizardStep(app, "完了");
            app.Invoke("Wizard.Continue");
            WizardStep(app, "①");
            app.WaitForText("Wizard.ActiveJobNotice", t => t.Contains("削除（"), "① で同じ対象のジョブを知らせる");
            Next(app, "②");
            Next(app, "③");
            Next(app, "④");
            Next(app, "⑤");
            app.WaitForText("Deploy.Error", t => t.Contains("この対象のジョブ"), "同じ対象のジョブがあれば計画できない", LoadTimeout);

            // 7b: 別の対象を予定に追加し（完了の表示から、ジョブの一覧を開く）、上部の帯からジョブの一覧に移って取り消す
            var second = appName + "b";
            BackTo(app, "①");
            app.SetText("Target.AppName", second);
            app.Exists("Wizard.ActiveJobNotice").Should().BeFalse("別の対象にはジョブが無い");
            Next(app, "②");
            Next(app, "③");
            Next(app, "④");
            app.Invoke("Wizard.Next");
            AppSession.WaitUntil(() => app.Exists("Deploy.Schedule") && app.IsEnabled("Deploy.Schedule"), "2 つ目の対象の計画が表示されませんでした。", LoadTimeout);
            app.Invoke("Deploy.Schedule");
            WizardStep(app, "完了");
            app.TextOf("Wizard.ShowJob").Should().Be("ジョブの一覧を見る");
            app.Invoke("Wizard.ShowJob");
            app.Find("Panel.Jobs");
            app.WaitForText("Jobs.Banner", t => t.Contains("予定 1"), "上部の帯に予定が出る");

            // 7c: 終わっていないジョブがあれば、閉じても終了せずに通知領域に残り、もう一度起動するとウィンドウが出る
            app.Window.Close();
            AppSession.WaitUntil(() => !app.IsWindowShown, "閉じるとウィンドウが隠れる", TimeSpan.FromSeconds(15));
            app.App.HasExited.Should().BeFalse("ジョブがあるため終了しない");
            using (FlaUI.Core.Application.LaunchStoreApp(AppSession.AppUserModelId()))
                AppSession.WaitUntil(() => app.IsWindowShown && app.Exists("Jobs.Banner"), "もう一度起動すると、隠したウィンドウが出る", TimeSpan.FromSeconds(30));
            app.Invoke("Jobs.BannerShow");
            app.Find("Panel.Jobs");
            // 一覧は新しい順なので、先頭（最初に見つかる要素）が今登録したジョブ。No.63: 行を選んで、まとめての操作で取り消す
            app.WaitForText("Job.Status", t => t.StartsWith("予定"), "予定のジョブが一覧の先頭に出る");
            app.SelectRow("Jobs.List");
            app.Invoke("Jobs.CancelSelected");
            app.WaitForText("Job.Status", t => t == "取り消し", "予定を取り消せる");
            app.WaitForText("Jobs.Message", t => t.StartsWith("1 件の取り消し"), "取り消した数が出る");

            // 7b: 削除の実行中に、別の対象のデプロイを同時に実行し（既定の上限は 2）、今の手順の後で止める。
            // 完了の表示の「続けてデプロイ」で、取り消した予定と同じ入力のまま ① から進める
            app.Show("Wizard");
            WizardStep(app, "完了");
            app.Invoke("Wizard.Continue");
            WizardStep(app, "①");
            Next(app, "②");
            Next(app, "③");
            Next(app, "④");
            app.Invoke("Wizard.Next");
            AppSession.WaitUntil(() => app.Exists("Deploy.Start") && app.IsEnabled("Deploy.Start"), "2 つ目の対象の計画が表示されませんでした。", LoadTimeout);
            app.Invoke("Deploy.Start");
            WizardStep(app, "完了");
            app.Invoke("Wizard.ShowJob");
            app.Find("Panel.Status");
            app.Show("Jobs");
            var concurrent = app.WaitForText("Job.Status", t => t is "実行中" or "待機中（同時に実行できる数に達しています）" or "完了" or "失敗", "2 つ目のデプロイが始まる", LoadTimeout);
            Log($"2 つ目のデプロイを始めたときのジョブ: {app.TextOf("Jobs.Count")}");
            concurrent.Should().Be("実行中", "対象が違えば、削除の実行中でも待たずに始める");
            // No.63: 1 行だけ選んで「詳細」で実行状況を開き、止めてからジョブの一覧に戻る（取り消した行の選択が残っているため、先に外す）
            app.Invoke("Jobs.ClearSelection");
            app.SelectRow("Jobs.List");
            app.Invoke("Jobs.Detail");
            app.Find("Panel.Status");
            app.Invoke("Job.Stop");
            var stopped = app.WaitForText("Job.Status", t => t is "停止" or "完了" or "失敗", "2 つ目のデプロイが止まる", JobTimeout);
            Log($"停止: {(app.Exists("Job.Error") ? app.TextOf("Job.Error") : stopped)}");
            stopped.Should().Be("停止");
            app.TextOf("Status.BackToList").Should().Contain("ジョブの一覧に戻る");
            app.Invoke("Status.BackToList");
            app.Find("Panel.Jobs");

            // 1 つ目の対象の削除（リソースグループを含む）が終わるのを待つ
            app.WaitForText("Jobs.Count", t => !t.Contains("終わっていないもの"), "終わっていないジョブが無くなる", JobTimeout);
            var jobs = app.ScreenText();
            Log($"ジョブ: {jobs.Replace('\n', ' ')}");
            jobs.Should().Contain($"リソースグループ {resourceGroup} も削除しました。", "1 つ目の対象の削除で、空になったリソースグループも消す");

            // 削除した後は、一覧に出ない（このアプリで削除したものは、Resource Graph に反映される前でも消える）
            app.Show("Deployments");
            app.Invoke("Deployments.Load");
            AppSession.WaitUntil(() => app.IsEnabled("Deployments.Load"), "デプロイの一覧の読み込みが終わりませんでした。", LoadTimeout);
            app.WaitForText("Deployments.Page", t => t.StartsWith("表示するデプロイはありません"), "削除した環境は一覧に出ない", LoadTimeout);
        });
    }

    // ウィザードの今の画面（①〜⑤）を確かめる
    private static void WizardStep(AppSession app, string step) =>
        app.WaitForText("Wizard.Step", t => t.StartsWith(step), $"ウィザードの {step} の画面");

    // 次へ進める（誤りが無くなって「次へ」が押せるようになるのを待つ）
    private static void Next(AppSession app, string step)
    {
        app.Invoke("Wizard.Next");
        WizardStep(app, step);
    }

    // 「戻る」で前の画面に戻る
    private static void BackTo(AppSession app, string step)
    {
        while (!app.TextOf("Wizard.Step").StartsWith(step))
            app.Invoke("Wizard.Back");
    }

    // 利用可能期限日の「既定」の選択肢（文言は日数を含むため、一覧から探す）
    private static string SelectedExpiryDefault(AppSession app) => app.ItemsOf("Settings.Expiry").First(i => i.StartsWith("既定"));

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
