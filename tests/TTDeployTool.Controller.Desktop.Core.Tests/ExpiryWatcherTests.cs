using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>期限の前の通知と、期限での削除（docs/design/desktop/desktop-jobs.md の 6.2・6.5）。</summary>
public class ExpiryWatcherTests
{
    // 2026-10-22 05:00（日本時間）
    private readonly MovableTime _time = new();

    private static Dictionary<string, string> Tags(string appName, string? expires = null)
    {
        var tags = new Dictionary<string, string>(ManagedTags.For(appName));
        if (expires != null)
            tags[DeploymentExpiry.ExpiresAtKey] = expires;
        return tags;
    }

    // Resource Graph の結果（Timetracker のアプリと Environment）
    private static List<ManagedResourceRecord> Listed(string app, string? expires) =>
    [
        new($"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/{app}-tt", $"{app}-tt", ManagedResourceRecord.ContainerAppType,
            "sub", "rg", "japaneast", Tags(app, expires), new ContainerAppSnapshot("Succeeded", "Running", "densocreate/timetracker:11", 1.5, "3Gi", null, [])),
        new($"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/{app}-env", $"{app}-env", ManagedResourceRecord.EnvironmentType,
            "sub", "rg", "japaneast", Tags(app)),
    ];

    // 期限が今から delta 後のデプロイを一覧に出し、Azure（直前の読み直し）にも同じ期限で置く
    private async Task<Harness> SignedInAsync(TimeSpan delta, Action<Harness>? arrange = null)
    {
        var h = new Harness(time: _time);
        var expires = DeploymentExpiry.FormatTag(_time.GetUtcNow() + delta);
        h.Azure.RecordsByTenant["t1"] = Listed("demo", expires);
        h.Azure.Deployed();
        SetAzureExpiry(h, expires);
        arrange?.Invoke(h);
        await h.ReadyAsync();
        return h;
    }

    private static void SetAzureExpiry(Harness h, string expires)
    {
        var apps = h.Azure.Apps.Select(a => a with { Tags = Tags("demo", expires) }).ToList();
        h.Azure.Apps.Clear();
        h.Azure.Apps.AddRange(apps);
    }

    private static void Relist(Harness h, string? expires) => h.Azure.RecordsByTenant["t1"] = Listed("demo", expires);

    [Fact]
    public async Task Nothing_Should_Be_Checked_Before_Signing_In()
    {
        var h = new Harness(time: _time);
        h.Azure.RecordsByTenant["t1"] = Listed("demo", DeploymentExpiry.FormatTag(_time.GetUtcNow().AddHours(-1)));

        await h.Main.Expiry.CheckAsync();
        // 一覧が変わっても、始めるまでは次の確認を決めない
        h.Main.Deployments.Filters.Text = "rg";

        h.Azure.DirectoryQueries.Should().BeEmpty();
        h.Main.Jobs.Should().BeEmpty();
        h.Main.Expiry.NextCheckAt.Should().BeNull();
    }

    [Fact]
    public async Task The_Three_Days_And_Last_Day_Notices_Should_Be_Shown_Once_Each()
    {
        var h = await SignedInAsync(TimeSpan.FromDays(2.5));

        var notice = h.Notifications.Shown.Should().ContainSingle().Subject;
        notice.Title.Should().Be("利用可能期限が近づいています");
        notice.Message.Should().Be("demo（rg）の利用可能期限は 2026-10-24 です。期限を過ぎると、このアプリが DB のデータも含めて削除します。延ばすには、デプロイの一覧で期限を変えてください。");
        notice.Page.Should().Be("deployments");
        h.Preferences.ExpiryNotices.Should().Equal("sub/rg/demo/3d/2026-10-24t08:00:00z");

        // 同じ通知は出さない
        await h.Main.Expiry.CheckAsync();
        h.Notifications.Shown.Should().HaveCount(1);

        // 1 日以内になると「前日」
        _time.Now += TimeSpan.FromDays(1.75);
        await h.Main.Expiry.CheckAsync();
        h.Notifications.Shown.Should().HaveCount(2);
        h.Notifications.Shown[1].Title.Should().Be("利用可能期限は明日までです");
        await h.Main.Expiry.CheckAsync();
        h.Notifications.Shown.Should().HaveCount(2);

        // 期限を延ばすと、新しい期限で通知し直す。過ぎた記録は消す
        Relist(h, DeploymentExpiry.FormatTag(_time.GetUtcNow().AddDays(2)));
        h.Preferences.ExpiryNotices = [.. h.Preferences.ExpiryNotices, "壊れた記録", "sub/rg/old/1d/2026-10-01T00:00:00Z"];
        await h.Main.Expiry.CheckAsync();
        h.Notifications.Shown.Should().HaveCount(3);
        h.Preferences.ExpiryNotices.Should().Equal(
            "sub/rg/demo/3d/2026-10-24t08:00:00z", "sub/rg/demo/1d/2026-10-24t08:00:00z", "sub/rg/demo/3d/2026-10-25t14:00:00z");
    }

    [Fact]
    public async Task Deployments_Far_From_Their_Expiry_Or_Without_One_Should_Be_Left_Alone()
    {
        var h = await SignedInAsync(TimeSpan.FromDays(10));
        h.Azure.RecordsByTenant["t1"] = [.. Listed("demo", DeploymentExpiry.FormatTag(_time.GetUtcNow().AddDays(10))), .. Listed("keep", "none"), .. Listed("old", null)];

        await h.Main.Expiry.CheckAsync();

        h.Notifications.Shown.Should().BeEmpty();
        h.Main.Jobs.Should().BeEmpty();
        h.Preferences.ExpiryNotices.Should().BeEmpty();
    }

    [Fact]
    public async Task An_Expired_Deployment_Should_Be_Deleted_With_Its_Data_And_The_Resource_Group_The_Tool_Created()
    {
        var h = await SignedInAsync(TimeSpan.FromMinutes(-5), h =>
        {
            h.Azure.HasStorageAccount = true;
            h.Azure.ResourceGroup = new ResourceGroupInfo("rg", "japaneast", ManagedTags.ForResourceGroup());
        });

        var job = h.Main.Jobs.Should().ContainSingle().Subject;
        job.Title.Should().Be("期限での削除（rg / demo）");
        job.IsExpiryDeletion.Should().BeTrue();
        // 画面のスレッドが無いテストでは、偽の Azure がすぐ返すため、削除の終わりの通知が先に出ることがある（順は問わない）
        h.Notifications.Shown.Should().Contain(new AppNotice(
            "利用可能期限を過ぎたため削除します",
            "demo（rg）は利用可能期限を過ぎたため、DB のデータも含めて削除します。進み具合はジョブの画面で確かめられます。", job.Id));
        await h.Main.Queue.WhenIdleAsync();

        job.State.Should().Be(DesktopJobState.Succeeded);
        h.Azure.GatewayMock.Verify(g => g.DeleteContainerAppAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>()));
        h.Azure.GatewayMock.Verify(g => g.DeleteStorageAccountAsync("sub", "rg", FakeAzure.StorageAccountName, It.IsAny<CancellationToken>()));
        h.Azure.GatewayMock.Verify(g => g.DeleteResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>()));
        h.Notifications.Shown.Should().Contain(n => n.Title == "期限での削除（rg / demo）: 完了");
    }

    [Fact]
    public async Task A_Resource_Group_The_Tool_Did_Not_Create_Should_Be_Kept()
    {
        var h = await SignedInAsync(TimeSpan.FromMinutes(-5));

        await h.Main.Queue.WhenIdleAsync();

        h.Main.Jobs.Single().State.Should().Be(DesktopJobState.Succeeded);
        h.Azure.GatewayMock.Verify(g => g.DeleteResourceGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_Expiry_Extended_Elsewhere_Should_Stop_The_Deletion_And_Wait_Before_Trying_Again()
    {
        // Resource Graph には古い期限が残っているが、別の PC で延ばされた
        var h = await SignedInAsync(TimeSpan.FromMinutes(-5), h => SetAzureExpiry(h, "2026-11-30T14:59:59Z"));

        await h.Main.Queue.WhenIdleAsync();

        var job = h.Main.Jobs.Single();
        job.State.Should().Be(DesktopJobState.Failed);
        job.Error.Should().Be("実行しませんでした: 利用可能期限が延ばされていたため、削除しませんでした（今の期限: 2026-11-30）。");
        h.Azure.GatewayMock.Verify(g => g.DeleteContainerAppAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // 失敗した対象は、6 時間たつまで登録し直さない
        _time.Now += TimeSpan.FromHours(5);
        await h.Main.Expiry.CheckAsync();
        h.Main.Jobs.Should().HaveCount(1);
        _time.Now += TimeSpan.FromHours(1);
        SetAzureExpiry(h, "none");
        await h.Main.Expiry.CheckAsync();
        await h.Main.Queue.WhenIdleAsync();
        h.Main.Jobs.Should().HaveCount(2);
        h.Main.Jobs[0].Error.Should().EndWith("（今の期限: 期限なし）。");

        // タグが読めなければ「記録なし」
        h.Azure.Apps.Clear();
        h.Azure.Environment = null;
        _time.Now += TimeSpan.FromHours(6);
        await h.Main.Expiry.CheckAsync();
        await h.Main.Queue.WhenIdleAsync();
        h.Main.Jobs[0].Error.Should().EndWith("（今の期限: 記録なし）。");
    }

    [Fact]
    public async Task A_Deletion_That_Cannot_Run_Should_Fail_With_The_Reason()
    {
        // 削除の権限が無い
        var h = await SignedInAsync(TimeSpan.FromMinutes(-5), h => h.Azure.Permissions.Clear());

        await h.Main.Queue.WhenIdleAsync();

        h.Main.Jobs.Single().Error.Should().StartWith("実行しませんでした: 実行の直前の確認で、実行できない状態になっていました:");
    }

    [Fact]
    public async Task A_Deployment_Already_Gone_Should_End_Without_Deleting()
    {
        // 期限を読み直した後、計画するまでの間に、ほかの PC が消した
        var h = await SignedInAsync(TimeSpan.FromMinutes(-5), h =>
            h.Azure.GatewayMock.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>()))
                .Callback(() =>
                {
                    if (h.Main.Jobs.Count == 0)
                        return;
                    h.Azure.Apps.Clear();
                    h.Azure.Environment = null;
                    h.Azure.ResourceGroup = null;
                })
                .ReturnsAsync(() => h.Azure.ResourceGroup));

        await h.Main.Queue.WhenIdleAsync();

        h.Main.Jobs.Single().Error.Should().Be("実行しませんでした: 削除するものはありませんでした（既に削除されています）。");
    }

    [Fact]
    public async Task A_Target_With_An_Unfinished_Job_Should_Not_Get_Another()
    {
        var h = await SignedInAsync(TimeSpan.FromDays(10));
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "demo"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);
        Relist(h, DeploymentExpiry.FormatTag(_time.GetUtcNow().AddMinutes(-1)));

        await h.Main.Expiry.CheckAsync();

        h.Main.Jobs.Should().ContainSingle().Which.IsDeployment.Should().BeTrue();
    }

    [Fact]
    public async Task A_Check_Should_Not_Start_While_Another_Runs_Or_When_The_List_Cannot_Be_Read()
    {
        var h = await SignedInAsync(TimeSpan.FromDays(10));
        var release = new TaskCompletionSource();
        h.Azure.DirectoryRelease = release.Task;
        h.Azure.DirectoryQueries.Clear();

        var first = h.Main.Expiry.CheckAsync();
        var second = h.Main.Expiry.CheckAsync();

        second.Should().BeSameAs(first);
        h.Azure.DirectoryQueries.Should().HaveCount(1);
        release.SetResult();
        await first;

        // 一覧を読めなければ何もしない
        h.Azure.DirectoryErrors["t1"] = new InvalidOperationException("読めません");
        Relist(h, DeploymentExpiry.FormatTag(_time.GetUtcNow().AddMinutes(-1)));
        await h.Main.Expiry.CheckAsync();
        h.Main.Jobs.Should().BeEmpty();
    }

    // 時計を進められる環境で、サインインまで済ませる（タイマーの投稿は、作ったときの SynchronizationContext に送る）
    private static async Task<(Harness H, FakeTimeProvider Time)> WithTimerAsync(Func<DateTimeOffset, List<ManagedResourceRecord>> listed)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero));
        var h = new Harness(time: time);
        h.Azure.RecordsByTenant["t1"] = listed(time.GetUtcNow());
        await h.ReadyAsync();
        return (h, time);
    }

    [Fact]
    public async Task The_Next_Check_Should_Be_At_The_Nearest_Expiry_And_Post_To_The_Context()
    {
        // 次の確認は、最も近い時刻（ここでは期限）。3 日前・前日は過ぎている（D21）
        var context = new CountingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var (h, time) = await WithTimerAsync(now => Listed("demo", DeploymentExpiry.FormatTag(now.AddHours(2))));
            var start = time.GetUtcNow();
            h.Azure.DirectoryQueries.Should().HaveCount(1);
            h.Main.Expiry.NextCheckAt.Should().Be(start.AddHours(2) + ExpiryWatcher.Margin);
            await h.Main.Expiry.StartAsync();
            h.Azure.DirectoryQueries.Should().HaveCount(2, "もう一度始めても、タイマーは増やさない");

            time.Advance(TimeSpan.FromHours(2));
            h.Azure.DirectoryQueries.Should().HaveCount(2, "期限の時刻の少し後に確かめる");
            time.Advance(ExpiryWatcher.Margin);
            await h.Main.Expiry.Checking;

            h.Azure.DirectoryQueries.Should().HaveCount(3);
            context.Posts.Should().BeGreaterThan(0);
            h.Main.Jobs.Should().ContainSingle().Which.IsExpiryDeletion.Should().BeTrue();
            // 期限を過ぎたものの次は、外の変化を拾う読み直し
            h.Main.Expiry.NextCheckAt.Should().Be(time.GetUtcNow() + ExpiryWatcher.RefreshInterval);
            h.Main.Dispose();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task The_Notices_Should_Be_Shown_At_Three_Days_And_One_Day_Before()
    {
        var (h, time) = await WithTimerAsync(now => Listed("demo", DeploymentExpiry.FormatTag(now.AddDays(3).AddHours(4))));
        var expires = time.GetUtcNow().AddDays(3).AddHours(4);
        h.Main.Expiry.NextCheckAt.Should().Be(expires - ExpiryWatcher.SoonNotice + ExpiryWatcher.Margin);

        time.Advance(TimeSpan.FromHours(4) + ExpiryWatcher.Margin);
        await h.Main.Expiry.Checking;

        h.Notifications.Shown.Should().ContainSingle().Which.Title.Should().Be("利用可能期限が近づいています");
        // 前日までは 6 時間ごとに読み直す
        h.Main.Expiry.NextCheckAt.Should().Be(time.GetUtcNow() + ExpiryWatcher.RefreshInterval);
    }

    [Fact]
    public async Task Without_Near_Expiries_The_List_Should_Be_Read_Again_Every_Six_Hours()
    {
        var (h, time) = await WithTimerAsync(now => Listed("demo", DeploymentExpiry.FormatTag(now.AddDays(10))));
        h.Main.Expiry.NextCheckAt.Should().Be(time.GetUtcNow() + ExpiryWatcher.RefreshInterval);

        time.Advance(ExpiryWatcher.RefreshInterval);
        await h.Main.Expiry.Checking;

        h.Azure.DirectoryQueries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Changing_The_Expiry_In_This_App_Should_Move_The_Next_Check()
    {
        // 一覧で期限を今日にすると、6 時間後の読み直しを待たずに、期限の時刻に確かめる
        var (h, time) = await WithTimerAsync(now => Listed("demo", DeploymentExpiry.FormatTag(now.AddDays(10))));
        h.Azure.Deployed();
        h.Azure.GatewayMock.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var row = h.Main.Deployments.Rows.Single();
        row.EditNoExpiry = false;
        row.EditExpiryDate = time.GetUtcNow();

        await row.ChangeExpiryCommand.ExecuteAsync();

        row.Error.Should().BeNull();
        // 期限は今日の 23:59:59（この時計の時刻帯は UTC）
        h.Main.Expiry.NextCheckAt.Should().Be(new DateTimeOffset(2026, 10, 21, 23, 59, 59, TimeSpan.Zero) + ExpiryWatcher.Margin);
    }

    [Fact]
    public async Task Without_Notifications_The_Deletion_Should_Still_Be_Registered()
    {
        var h = new Harness(time: _time, withNotifications: false);
        h.Azure.RecordsByTenant["t1"] = Listed("demo", DeploymentExpiry.FormatTag(_time.GetUtcNow().AddDays(2)));
        await h.ReadyAsync();
        h.Main.RequestClose().Should().Be(CloseAction.Hide);
        Relist(h, DeploymentExpiry.FormatTag(_time.GetUtcNow().AddMinutes(-1)));

        await h.Main.Expiry.CheckAsync();

        h.Main.Jobs.Should().ContainSingle().Which.IsExpiryDeletion.Should().BeTrue();
        h.Preferences.ExpiryNotices.Should().ContainSingle("通知を出せなくても、出したものとして記録する");
        await h.Main.Queue.WhenIdleAsync();

        var startup = new Harness(time: _time, withNotifications: false);
        await startup.Main.InitializeAtStartupAsync();
        startup.Main.SignIn.IsSignedIn.Should().BeFalse();
    }

    private sealed class CountingContext : SynchronizationContext
    {
        public int Posts { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Posts++;
            d(state);
        }
    }
}

/// <summary>閉じたときの常駐（D18）と、サインイン時の起動（D16・D19）。</summary>
public class StartupAndResidencyTests
{
    private readonly MovableTime _time = new();

    [Fact]
    public async Task Closing_Should_Stay_In_The_Tray_While_A_Deployment_Has_An_Expiry()
    {
        var h = new Harness(time: _time);
        h.Main.RequestClose().Should().Be(CloseAction.Exit, "サインインしていない");
        h.Azure.RecordsByTenant["t1"] =
        [
            new("/s/tt", "demo-tt", ManagedResourceRecord.ContainerAppType, "sub", "rg", "japaneast",
                new Dictionary<string, string>(ManagedTags.For("demo")) { [DeploymentExpiry.ExpiresAtKey] = "2026-11-30T14:59:59Z" }),
        ];
        await h.ReadyAsync();

        h.Main.Deployments.HasExpiringDeployments.Should().BeTrue();
        h.Main.RequestClose().Should().Be(CloseAction.Hide);
        h.Notifications.Shown.Should().ContainSingle().Which.Message.Should().StartWith(
            "利用可能期限のあるデプロイを確かめる（期限の前の通知と、期限での削除）ため、通知領域に残ります。");

        // 期限なしだけなら終了する
        h.Azure.RecordsByTenant["t1"] = [];
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        h.Main.RequestClose().Should().Be(CloseAction.Exit);
    }

    [Fact]
    public async Task Starting_At_Windows_Sign_In_Should_Ask_To_Sign_In_When_It_Cannot_Restore()
    {
        var h = new Harness(time: _time);
        h.StartupTask.Status = StartupTaskStatus.Enabled;

        await h.Main.InitializeAtStartupAsync();

        h.Main.Startup.IsEnabled.Should().BeTrue();
        h.Notifications.Shown.Should().ContainSingle().Which.Should().Be(new AppNotice(
            "サインインが必要です",
            "利用可能期限の確認と、期限を過ぎたデプロイの削除ができません。この通知か通知領域のアイコンを押して、サインインしてください。",
            Page: Pages.Target));

        // 復元できれば通知しない
        var restored = new Harness(time: _time);
        restored.SignIn.Restored = FakeSignIn.Alice;
        await restored.Main.InitializeAtStartupAsync();
        restored.Notifications.Shown.Should().BeEmpty();
        restored.Azure.DirectoryQueries.Should().HaveCount(1, "サインインできたら期限を確かめる");
    }

    [Fact]
    public async Task The_First_Deployment_With_An_Expiry_Should_Ask_Once_To_Start_At_Sign_In()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.StartupTask.Status = StartupTaskStatus.Disabled;
        h.Dialogs.Confirm = true;
        h.Azure.ResourceGroup = null;

        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.ScheduleCommand.ExecuteAsync();
        await h.Main.StartupQuestion;

        h.Dialogs.Confirmations.Should().Equal("Windows のサインイン時に起動しますか？");
        h.Preferences.StartupTaskAsked.Should().BeTrue();
        h.StartupTask.Status.Should().Be(StartupTaskStatus.Enabled);
        h.Main.Startup.IsEnabled.Should().BeTrue();

        // 二度目は尋ねない
        await h.Main.CurrentJob!.CancelCommand.ExecuteAsync();
        h.StartupTask.Status = StartupTaskStatus.Disabled;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.ScheduleCommand.ExecuteAsync();
        await h.Main.StartupQuestion;
        h.Dialogs.Confirmations.Should().HaveCount(1);
    }

    [Fact]
    public async Task Declining_Or_Not_Being_Able_To_Change_Should_Not_Enable_It()
    {
        var prefs = new InMemoryAppPreferences();
        var dialogs = new FakeDialogs { Confirm = false };
        var service = new FakeStartupTask { Status = StartupTaskStatus.Disabled };
        var vm = new StartupSettingViewModel(service, prefs, dialogs, NullLogger.Instance);

        await vm.AskOnceAsync();
        (service.Status, prefs.StartupTaskAsked).Should().Be((StartupTaskStatus.Disabled, true));

        // 利用者がタスク マネージャーで無効にした場合は尋ねない（記録もしない）
        prefs.StartupTaskAsked = false;
        service.Status = StartupTaskStatus.DisabledByUser;
        await vm.AskOnceAsync();
        prefs.StartupTaskAsked.Should().BeFalse();
        dialogs.Confirmations.Should().HaveCount(1);
        vm.CanChange.Should().BeFalse();
        vm.ToggleCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task The_Setting_Should_Toggle_And_Explain_Each_State()
    {
        var service = new FakeStartupTask { Status = StartupTaskStatus.Disabled };
        var vm = new StartupSettingViewModel(service, new InMemoryAppPreferences(), new FakeDialogs(), NullLogger.Instance);
        vm.Status.Should().Be(StartupTaskStatus.Unavailable, "読むまでは使えない扱い");
        await vm.RefreshAsync();
        vm.StatusText.Should().StartWith("無効:");
        vm.ToggleText.Should().Be("有効にする");

        await vm.ToggleCommand.ExecuteAsync();
        (vm.IsEnabled, vm.StatusText, vm.ToggleText).Should().Be((true, "有効: Windows にサインインすると、ウィンドウを出さずに通知領域で起動し、利用可能期限を確かめます。", "無効にする"));
        await vm.ToggleCommand.ExecuteAsync();
        vm.IsEnabled.Should().BeFalse();

        // 有効にしようとしても、ポリシーで無効
        service.EnableResult = StartupTaskStatus.DisabledByPolicy;
        await vm.ToggleCommand.ExecuteAsync();
        vm.StatusText.Should().StartWith("組織のポリシーで無効");
        service.Status = StartupTaskStatus.DisabledByUser;
        await vm.RefreshAsync();
        vm.StatusText.Should().StartWith("タスク マネージャーの「スタートアップ アプリ」");

        var none = new StartupSettingViewModel(null, new InMemoryAppPreferences(), new FakeDialogs(), NullLogger.Instance);
        await none.RefreshAsync();
        none.StatusText.Should().StartWith("この起動の方法では使えません");
        StartupSettingViewModel.Explanation.Should().Contain("どの PC でもアプリが動いていなければ");
    }

    [Fact]
    public async Task A_Deployment_Without_An_Expiry_Should_Not_Ask()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.StartupTask.Status = StartupTaskStatus.Disabled;
        h.Azure.ResourceGroup = null;
        h.Main.Settings.Expiry = SettingsViewModel.ExpiryOptions.First(o => o.Mode == ExpiryMode.None);

        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.ScheduleCommand.ExecuteAsync();
        await h.Main.StartupQuestion;

        h.Dialogs.Confirmations.Should().BeEmpty();
        await h.Main.CurrentJob!.CancelCommand.ExecuteAsync();
    }
}
