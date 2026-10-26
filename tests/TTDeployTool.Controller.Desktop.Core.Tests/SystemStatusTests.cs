using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using Xunit;
using static TTDeployTool.Controller.Desktop.Core.Tests.DeploymentListViewModelTests;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>「設定」の画面の「Windows との連携」（docs/design/desktop-jobs.md の 6.6、D22）。</summary>
public class SystemStatusTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task The_Last_Startup_Launch_Should_Be_Recorded_And_Shown()
    {
        var h = new Harness(time: _time);
        var vm = h.Main.Integration;
        vm.LastStartupText.Should().StartWith("Windows のサインイン時に起動したことは、まだありません");

        await h.Main.InitializeAtStartupAsync();
        h.Preferences.LastStartupLaunch.Should().Be(new StartupLaunch(_time.GetUtcNow(), false));
        vm.LastStartupText.Should().Be($"前回のサインイン時の起動: {_time.GetUtcNow().ToLocalTime():yyyy-MM-dd HH:mm}（対話なしでサインインできず、サインインが要ることを通知しました）");

        var restored = new Harness(time: _time, preferences: h.Preferences);
        restored.SignIn.Restored = FakeSignIn.Alice;
        await restored.Main.InitializeAtStartupAsync();
        restored.Main.Integration.LastStartupText.Should().EndWith("（対話なしでサインインし、利用可能期限と予定を確かめました）");

        // 普通の起動では記録しない
        var normal = new Harness(time: _time, preferences: new InMemoryAppPreferences());
        await normal.Main.StartAsync(atStartup: false);
        normal.Preferences.LastStartupLaunch.Should().BeNull();
    }

    [Theory]
    [InlineData(NotificationPermission.Enabled, "許可されています", false)]
    [InlineData(NotificationPermission.DisabledForApplication, "このアプリの通知が", true)]
    [InlineData(NotificationPermission.DisabledForUser, "すべてのアプリの通知が", true)]
    [InlineData(NotificationPermission.DisabledByGroupPolicy, "組織のポリシーで", true)]
    [InlineData(NotificationPermission.DisabledByManifest, "このアプリのパッケージの設定で", true)]
    [InlineData(NotificationPermission.Unavailable, "この起動の方法では", true)]
    public void The_Notification_Permission_Should_Be_Explained(NotificationPermission permission, string start, bool blocked)
    {
        var h = new Harness(time: _time);
        h.Notifications.Permission = permission;

        h.Main.Integration.NotificationPermission.Should().Be(permission);
        h.Main.Integration.NotificationText.Should().StartWith(start);
        h.Main.Integration.NotificationsBlocked.Should().Be(blocked);
    }

    [Fact]
    public void Without_Notifications_The_Permission_Should_Be_Unavailable() =>
        new Harness(time: _time, withNotifications: false).Main.Integration.NotificationPermission.Should().Be(NotificationPermission.Unavailable);

    [Theory]
    [InlineData("Enabled", NotificationPermission.Enabled)]
    [InlineData("DisabledForApplication", NotificationPermission.DisabledForApplication)]
    [InlineData("DisabledForUser", NotificationPermission.DisabledForUser)]
    [InlineData("DisabledByGroupPolicy", NotificationPermission.DisabledByGroupPolicy)]
    [InlineData("DisabledByManifest", NotificationPermission.DisabledByManifest)]
    [InlineData("Unsupported", NotificationPermission.Unavailable)]
    [InlineData("42", NotificationPermission.Unavailable)]
    public void Windows_Notification_Settings_Should_Be_Mapped(string name, NotificationPermission expected) =>
        NotificationPermissions.FromWindowsSetting(name).Should().Be(expected);

    [Fact]
    public async Task Closing_Should_Be_Explained_With_The_Same_Reason_As_The_Window_Uses()
    {
        var h = new Harness(time: _time);
        var vm = h.Main.Integration;
        vm.CloseBehaviorText.Should().Be("今ウィンドウを閉じると、アプリを終了します（終わっていないジョブも、利用可能期限日のあるデプロイもないため）。");

        h.Azure.RecordsByTenant["t1"] = [.. Deployment("sub", "rg", "a", expires: DeploymentExpiry.FormatTag(_time.GetUtcNow().AddDays(30)))];
        await h.ReadyAsync();
        vm.CloseBehaviorText.Should().Be("今ウィンドウを閉じると、利用可能期限のあるデプロイを確かめる（期限の前の通知と、期限での削除）ため、通知領域に残ります。");

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "x"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);
        raised.Should().Contain([nameof(SystemStatusViewModel.CloseBehaviorText), nameof(SystemStatusViewModel.ScheduledText)]);
        vm.CloseBehaviorText.Should().Be("今ウィンドウを閉じると、ジョブ（予定 1）があるため、通知領域に残ります。");
    }

    [Fact]
    public async Task The_Jobs_That_Will_Be_Lost_Or_Restored_Should_Be_Counted()
    {
        var h = new Harness(time: _time, preferences: new InMemoryAppPreferences
        {
            SavedJobs = [new SavedJob(JobKind.Delete, "sub", "rg", "old", null, false, "bob@example.com", new SavedDeletion(false, false, false, "old", [], false))],
        });
        var vm = h.Main.Integration;
        vm.ScheduledText.Should().Be("まだ始めていないジョブ（予定・待機中・一時停止）はありません。\n前回のアプリの削除の予定 1 件を、保存したアカウントでサインインしたときに戻します。");

        var at = _time.GetUtcNow().AddHours(1);
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "a"), at, () => new TaskCompletionSource<Job>().Task);
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "b"), at, () => new TaskCompletionSource<Job>().Task,
            saved: new SavedDeletion(false, false, false, "b", [], false));

        vm.ScheduledText.Should().StartWith("まだ始めていないジョブ: デプロイ 1 件はアプリの終了・Windows の再起動・サインアウトで消えます（パスワードを保存しないため）。削除 1 件は、次に");
    }

    [Fact]
    public async Task Opening_The_Settings_Page_Should_Read_The_State_Again_And_Open_Windows_Settings()
    {
        var h = new Harness(time: _time);
        var vm = h.Main.Integration;
        h.StartupTask.Status = StartupTaskStatus.Enabled;

        await h.Main.PageShownAsync(Pages.Deployments);
        vm.Startup.IsEnabled.Should().BeFalse("ほかの画面では読み直さない");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await h.Main.PageShownAsync(Pages.Settings);
        vm.Startup.IsEnabled.Should().BeTrue();
        raised.Should().Contain([nameof(SystemStatusViewModel.LastStartupText), nameof(SystemStatusViewModel.NotificationText)]);

        h.StartupTask.Status = StartupTaskStatus.DisabledByUser;
        await vm.RefreshCommand.ExecuteAsync();
        vm.Startup.Status.Should().Be(StartupTaskStatus.DisabledByUser);

        await vm.OpenStartupSettingsCommand.ExecuteAsync();
        await vm.OpenNotificationSettingsCommand.ExecuteAsync();
        h.Launcher.Opened.Should().Equal("ms-settings:startupapps", "ms-settings:notifications");
    }

    [Fact]
    public void Saved_Jobs_And_The_Last_Startup_Launch_Should_Be_Kept_In_The_File()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "preferences.json");
        var at = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        var deletion = new SavedDeletion(true, true, true, "demo", [new("demo-tt", true), new("demo-st", false)], true);
        new FilePreferences(path)
        {
            SavedJobs = [new SavedJob(JobKind.Delete, "sub", "rg", "demo", at, true, "alice@example.com", deletion)],
            LastStartupLaunch = new StartupLaunch(at, true),
        };

        var read = new FilePreferences(path);
        var job = read.SavedJobs.Should().ContainSingle().Subject;
        job.Should().BeEquivalentTo(new { Kind = JobKind.Delete, AppName = "demo", ScheduledAt = (DateTimeOffset?)at, Paused = true, Account = "alice@example.com" });
        job.Deletion.Should().BeEquivalentTo(deletion);
        read.LastStartupLaunch.Should().Be(new StartupLaunch(at, true));
        File.ReadAllText(path).Should().NotContain("\"Text\"").And.NotContain("\"Target\"");
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
