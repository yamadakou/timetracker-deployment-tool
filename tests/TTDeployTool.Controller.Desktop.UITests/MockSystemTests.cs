using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Fixtures;
using Xunit;
using static TTDeployTool.Controller.Desktop.UITests.MockUiTests;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// アプリの外の機能（通知・既定のアプリで開く・サインイン時の起動）をモックにした UI テスト（docs/design/desktop/desktop-ui-tests.md の 6.3）。
/// アプリが書いた記録（uitest-events.txt）と画面の表示で、期限の通知・期限での自動の削除・設定の表示・予定と記録の再起動の後を確かめる（実機の確認の代わり）。
/// </summary>
public class MockSystemTests
{
    [Fact]
    public void Expiry_Should_Notify_And_Delete_Expired_Deployments() => WithFixture(app =>
    {
        SignIn(app);
        Load(app);
        // 3 日後に期限のデプロイは通知し、期限を過ぎたデプロイ（最後の 1 件）は、自動で削除のジョブを登録する
        AppSession.WaitUntil(() => Events().Any(e => e.Kind == FixtureEvents.Notice && e.Text.StartsWith("利用可能期限が近づいています", StringComparison.Ordinal)),
            "期限の近いデプロイを通知する", TimeSpan.FromSeconds(60));
        var expired = $"rg-mock-{Deployments - 1:00} / app{Deployments - 1:00}";
        app.Show("Jobs");
        app.WaitForRowText("Jobs.List", "Job.Target", expired, "Job.Kind", t => t == "期限での削除", "期限を過ぎたデプロイの削除のジョブを登録する", TimeSpan.FromSeconds(60));
    }, data: d => FixtureSamples.Data(Deployments, DateTimeOffset.UtcNow, expired: 1) with { WriteDelaySeconds = 120 });

    [Fact]
    public void Settings_Should_Show_Startup_And_Notification_States() => WithFixture(app =>
    {
        app.Show("AppSettings");
        app.TextOf("Settings.StartupStatus").Should().Contain("タスク マネージャー", "利用者がスタートアップを無効にした");
        app.TextOf("Settings.NotificationStatus").Should().Contain("このアプリの通知が、Windows の設定で止められています");
    }, data: d => d with { StartupTask = StartupTaskStatus.DisabledByUser, Notifications = NotificationPermission.DisabledForApplication });

    [Fact]
    public void The_Startup_Toggle_And_Open_Folder_Should_Reach_Windows() => WithFixture(app =>
    {
        app.Show("AppSettings");
        app.Invoke("Settings.StartupToggle");
        AppSession.WaitUntil(() => Events().Contains((FixtureEvents.StartupTask, nameof(StartupTaskStatus.Enabled))), "サインイン時の起動を Windows に登録する");

        // Dry-run で保存し、「フォルダーを開く」で保存したフォルダーを開く
        var folder = Path.Combine(Path.GetTempPath(), "ttdeploytool-uitest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            app.Show("DryRun");
            app.SetText("DryRun.DbPassword", "UiTestDbPass1!");
            app.SetText("DryRun.TrackerPassword", "UiTestTracker1!");
            app.Invoke("DryRun.Save");
            app.PickFolder(folder);
            app.WaitForText("DryRun.Compose", t => t.Length > 0, "保存できる");
            app.Invoke("DryRun.OpenFolder");
            AppSession.WaitUntil(() => Events().Any(e => e.Kind == FixtureEvents.OpenFolder && e.Text.TrimEnd('\\') == folder), "保存したフォルダーを開く");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void Schedules_And_Deleted_Records_Should_Survive_A_Restart() => WithFixture(app =>
    {
        SignIn(app);
        Load(app);
        app.Select("Deployments.PageSize", "50");
        Delete(app, "app06", schedule: true);
        app.WaitForText("Jobs.Count", t => t.Contains($"全 {Records + 1} 件"), "記録と予定");

        // 記録を 1 件消す（確認のダイアログで「記録から削除」）
        var newest = $"rg-mock-{100 + Records - 1} / app{100 + Records - 1}";
        app.SelectRowWith("Jobs.List", "Job.Target", newest);
        app.Invoke("Jobs.DeleteSelectedRecords");
        app.FindAnywhere("PrimaryButton").Patterns.Invoke.Pattern.Invoke();
        app.WaitForText("Jobs.Count", t => t.Contains($"全 {Records} 件"), "記録から消える");

        // 予定があると閉じても通知領域に残るため、終了させてから起動し直す。サインインすると予定が戻り、消した記録は戻らない
        app.CloseAndWait(TimeSpan.FromSeconds(5));
        if (!app.App.HasExited)
            app.App.Kill();
        using var again = AppSession.Launch();
        again.Show("Jobs");
        again.WaitForText("Jobs.Count", t => t.Contains($"全 {Records - 1} 件"), "サインインする前は、記録だけ（消した 1 件は戻らない）");
        SignIn(again);
        again.Show("Jobs");
        again.WaitForRowText("Jobs.List", "Job.Target", "rg-mock-06 / app06", "Job.Status", t => t.StartsWith("予定", StringComparison.Ordinal), "サインインすると予定が戻る");
        again.Exists("Jobs.List").Should().BeTrue();
        again.RowsText("Jobs.List").Should().NotContain(newest, "消した記録は戻らない");
    });

    [Fact]
    public void Schedule_Times_Not_After_Now_Should_Be_Refused_On_Every_Screen() => WithFixture(app =>
    {
        // 予定の時刻を今の分にすると、どの画面でも「今より後にしてください」で断る（No.80。境界の値は Core の ScheduleBoundaryTests）。
        // 予定の日付は今から 1 時間後の日のため、日をまたぐ時間帯（23 時台）は確かめられない
        if (DateTime.Now.AddMinutes(70).Date != DateTime.Now.Date)
            Assert.Skip("予定の日付の既定が明日になる時間帯のため確かめない");
        SignIn(app);
        Load(app);
        app.Select("Deployments.PageSize", "50");

        // 予定を入れたジョブ（1 時間後）を、ジョブの一覧で選んで今の時刻に変えようとすると断る
        Delete(app, "app06", schedule: true);
        var target = "rg-mock-06 / app06";
        app.SelectRowWith("Jobs.List", "Job.Target", target);
        var now = DateTime.Now;
        app.SetTime("Jobs.ScheduleTime", now.Hour, now.Minute);
        app.Invoke("Jobs.RescheduleSelected");
        app.WaitForText("Jobs.Message", t => t.EndsWith("は、今より後にしてください。", StringComparison.Ordinal), "ジョブの一覧で、今の時刻への予定の変更を断る");

        // 実行状況の画面のジョブ 1 件の「予定を変える」も断り、予定のまま残す
        app.Invoke("Jobs.Detail");
        app.Find("Panel.Status");
        now = DateTime.Now;
        app.SetTime("Job.EditTime", now.Hour, now.Minute);
        app.Invoke("Job.Reschedule");
        app.WaitForText("Job.RescheduleMessage", t => t.EndsWith("は、今より後にしてください。", StringComparison.Ordinal), "実行状況の画面で、今の時刻への予定の変更を断る");
        app.TextOf("Job.Status").Should().StartWith("予定", "すぐ実行にしない");

        // 削除の画面: 「予定に追加」の理由に出る（削除の画面は入れた時刻を覚えるため、予定を入れた後に確かめる）
        OpenDeletion(app, "app03");
        now = DateTime.Now;
        app.SetTime("Delete.ScheduleTime", now.Hour, now.Minute);
        app.WaitForText("Delete.Blocked", t => t.EndsWith("は、今より後にしてください。", StringComparison.Ordinal), "削除の予定を今の時刻では入れられない");
    });
}
