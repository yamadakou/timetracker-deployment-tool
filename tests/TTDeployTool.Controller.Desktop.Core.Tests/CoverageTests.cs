using Azure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;
using static TTDeployTool.Controller.Desktop.Core.Tests.DeploymentListViewModelTests;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// 画面の細かな分岐（同じ値を入れ直したとき・値が無いとき・ほかの画面のテストで通らない側）。
/// </summary>
public class CoverageTests
{
    private readonly MovableTime _time = new();

    private async Task<Harness> LoadedAsync(params IEnumerable<ManagedResourceRecord>[] deployments)
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.Azure.RecordsByTenant["t1"] = deployments.SelectMany(d => d).ToList();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        return h;
    }

    // 2 つのデプロイ（demo と other。同じ Environment を使う）を選んで、まとめての削除の画面を開く
    private async Task<Harness> BatchSelectedAsync()
    {
        var h = new Harness(time: _time);
        h.Azure.RecordsByTenant["t1"] = [.. Deployment("sub", "rg", "demo"), .. Deployment("sub", "rg", "other")];
        h.Azure.Deployed();
        foreach (var suffix in new[] { "-tt", "-db", "-redis" })
            h.Azure.Apps.Add(new ContainerAppInfo("other" + suffix, FakeAzure.EnvId, ManagedTags.For("other")));
        await h.ReadyAsync();
        var list = h.Main.Deployments;
        list.SetSelection(list.Rows);
        await list.DeleteSelectedCommand.ExecuteAsync();
        return h;
    }

    private static Dictionary<string, string> WithGroup(IReadOnlyDictionary<string, string> tags, string group) =>
        new(tags, StringComparer.OrdinalIgnoreCase) { [DeploymentGroup.Key] = group };

    // グループのタグを Container App に付けたデプロイ
    private static IEnumerable<ManagedResourceRecord> Grouped(string app, string group) =>
        Deployment("sub", "rg", app).Select(r => r.Type == ManagedResourceRecord.ContainerAppType ? r with { Tags = WithGroup(r.Tags, group) } : r);

    [Fact]
    public async Task Choosing_The_Same_Batch_Option_Again_Should_Keep_The_Plans()
    {
        var h = await BatchSelectedAsync();
        var vm = h.Main.BatchDeletion;
        vm.Items.Should().HaveCount(2);
        vm.ConfirmCount = "2";

        // 同じ値を入れ直しても、計画と確認の入力は消さない
        vm.DeleteResourceGroup = false;

        vm.Items.Should().HaveCount(2);
        vm.ConfirmCount.Should().Be("2");
        vm.CanStart.Should().BeTrue();
    }

    [Fact]
    public async Task A_Batch_Where_Nothing_Could_Be_Registered_Should_Still_Open_The_Job_List()
    {
        var h = await BatchSelectedAsync();
        var vm = h.Main.BatchDeletion;
        vm.ConfirmCount = "2";
        // 計画の後に、どちらの対象にもジョブが登録された
        foreach (var app in new[] { "demo", "other" })
            h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", app), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);

        await vm.StartCommand.ExecuteAsync();

        vm.Error.Should().StartWith("登録できなかったもの:\n- demo（rg）: この対象のジョブ").And.Contain("\n- other（rg）: この対象のジョブ");
        h.Main.Queue.Jobs.Should().OnlyContain(j => j.IsDeployment, "削除のジョブは 1 つも登録していない");
        h.Main.CurrentDeletion.Should().BeNull("登録したジョブが無ければ、ジョブの一覧で見るものも無い");
        h.Navigations.Should().EndWith("jobs");
    }

    [Fact]
    public async Task The_Deletion_Plan_Markdown_Should_Exist_Only_While_There_Is_A_Plan()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.Azure.Deployed();
        var vm = h.Main.Deletion;
        vm.ToMarkdown().Should().BeNull("計画が無い");

        await vm.PlanCommand.ExecuteAsync();
        vm.ToMarkdown().Should().StartWith("# TTDeployTool の削除の計画（rg / demo）");

        // 対象を変えると、計画は無くなる
        h.Main.Target.AppName = "other";
        vm.ToMarkdown().Should().BeNull();
    }

    [Fact]
    public async Task Group_Suggestions_Should_Contain_The_Input_And_Leave_Out_The_Same_Name()
    {
        var h = await LoadedAsync(Grouped("a", "営業"), Grouped("b", "研修"));
        var vm = h.Main.Deployments;

        vm.BatchGroup = "営";
        vm.BatchGroupSuggestions.Should().Equal(["営業"], "入力を含まない「研修」は出さない");
        vm.BatchGroup = " 営業 ";
        vm.BatchGroupSuggestions.Should().BeEmpty("入力と同じ名前は出さない");
    }

    [Fact]
    public async Task Changes_Made_In_The_Row_Should_Not_Bring_Back_A_Deployment_Deleted_Elsewhere()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo", "2026-11-23T14:59:59Z"));
        h.Azure.Deployed();
        h.Azure.GatewayMock.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var vm = h.Main.Deployments;
        var row = vm.Rows.Single();
        row.EditNoExpiry = true;
        await row.ChangeExpiryCommand.ExecuteAsync();
        (await row.ApplyGroupAsync("営業")).Should().BeNull();
        (row.ExpiryText, row.GroupText).Should().Be(("期限なし", "営業"));

        // ほかの PC などで削除され、15 分のうちに読み直しても、変えた期限・グループで行を作り直さない
        h.Azure.RecordsByTenant["t1"] = [];
        await vm.LoadCommand.ExecuteAsync();

        vm.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Tenants_Without_A_Name_Should_Be_Shown_By_Id_Or_As_The_Default_Tenant()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var target = h.Main.Target;
        // テナントの名前の分からないサブスクリプションと、テナントも分からないサブスクリプション
        target.Subscriptions.Add(new SubscriptionChoice("sub-x", "名前なし", "Enabled", "t9", null));
        target.Subscriptions.Add(new SubscriptionChoice("sub-y", "テナントなし", "Enabled", null, null));
        h.Azure.DirectoryErrors["t9"] = new RequestFailedException(403, "Forbidden");
        var vm = h.Main.Deployments;
        var reported = new List<string>();
        vm.Busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Busy.Text))
                reported.Add(vm.Busy.Text);
        };

        await vm.LoadCommand.ExecuteAsync();

        reported.Should().Contain([
            "テナント 1 / 3（開発）のデプロイを読んでいます…",
            "テナント 2 / 3（t9）のデプロイを読んでいます…",
            "テナント 3 / 3（既定のテナント）のデプロイを読んでいます…",
        ]);
        vm.Notices.Should().Contain(n => n.StartsWith("テナント t9 のデプロイを読めませんでした: ", StringComparison.Ordinal));
        // テナントの分からないものは、アカウントのテナントで読む
        h.Azure.DirectoryQueries.Should().Contain(q => q.TenantId == null && q.Subscriptions.SequenceEqual(new[] { "sub-y" }));
        vm.Error.Should().BeNull();
    }

    [Fact]
    public async Task A_Deployment_Without_Expiry_Should_Be_Listed_As_Having_No_Expiry()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo", "2026-11-23T14:59:59Z"));
        h.Azure.Deployed();
        h.Main.Settings.Expiry = SettingsViewModel.ExpiryOptions[2];

        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        h.Main.Deployment.DataLossAcknowledged = true;
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        var row = h.Main.Deployments.Rows.Single();
        (row.ExpiryText, row.Entry.ExpiryKind, row.Entry.ExpiresAt).Should().Be(("期限なし", ExpiryKind.None, (DateTimeOffset?)null));
    }

    [Fact]
    public async Task A_Deletion_That_Left_Some_Apps_Should_Keep_Them_In_The_Row()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo"));
        h.Azure.Deployed();
        // Redis のアプリには管理タグが無いため、削除しない（Environment も使われているため残す）
        var redis = h.Azure.Apps.FindIndex(a => a.Name == "demo-redis");
        h.Azure.Apps[redis] = new ContainerAppInfo("demo-redis", FakeAzure.EnvId, new Dictionary<string, string>());
        var deletion = h.Main.Deletion;

        await deletion.PlanCommand.ExecuteAsync();
        deletion.ConfirmAppName = "demo";
        await deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        h.Main.CurrentJob!.StatusText.Should().Be("完了");
        var row = h.Main.Deployments.Rows.Single();
        (row.Entry.Timetracker, row.Entry.Db).Should().Be((null, null));
        row.Entry.Redis!.Name.Should().Be("demo-redis");
        row.Entry.EnvironmentName.Should().Be("demo-env");
    }

    [Fact]
    public async Task Passwords_Should_Not_Be_Shown_Before_They_Are_Read()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo"));
        var row = h.Main.Deployments.Rows.Single();

        row.HasPasswords.Should().BeFalse();
        (row.DbPasswordText, row.TrackerPasswordText).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public async Task Saving_To_The_Same_Folder_Again_Should_Update_The_Content_Without_Changing_The_Folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "tt-dryrun-" + Guid.NewGuid().ToString("N"));
        try
        {
            var h = new Harness();
            h.Main.Settings.DbPassword = "DbPassw0rd!";
            h.Main.Settings.TrackerPassword = "TrackerPass!";
            h.Dialogs.Folder = root;
            h.Dialogs.Confirm = true;
            var vm = h.Main.DryRun;
            await vm.SaveCommand.ExecuteAsync();
            vm.SavedDirectory.Should().Be(root);
            var changes = new ChangeRecorder(vm);

            h.Main.Settings.TtMemoryGi = "2";
            await vm.SaveCommand.ExecuteAsync();

            vm.SavedDirectory.Should().Be(root);
            changes.Names.Should().NotContain(nameof(DryRunViewModel.SavedDirectory), "同じフォルダーなら知らせない");
            // 2 回目の保存の内容（警告）は出し直す
            vm.Warnings.Should().ContainSingle().Which.Should().StartWith("tt-memory が 2Gi です。");
            vm.ComposeContent.Should().Be(File.ReadAllText(Path.Combine(root, "docker-compose.yml")));
            vm.OpenFolderCommand.CanExecute(null).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_Job_List_Without_A_Subscription_Display_Should_Show_Ids_And_Offer_Every_Value()
    {
        var h = await new Harness().ReadyAsync();
        var list = new JobListViewModel(h.Main.Queue, TimeProvider.System, h.Dialogs);
        var jobs = new JobManager(new InMemoryJobStore(), TimeProvider.System, NullLogger<JobManager>.Instance);
        var scheduled = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "a"), DateTimeOffset.Now.AddHours(1), () => new TaskCompletionSource<Job>().Task);
        var deleted = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "b"), null, () => Task.FromResult(
            jobs.TryStart(JobKind.Delete, new JobTarget("sub", "rg", "b"), "local", _ => Task.FromResult<object?>(new DeletionOutcome("rg", ["b-tt"], []))).Job!));
        await deleted.Finished;

        deleted.State.Should().Be(DesktopJobState.Succeeded);
        deleted.ResultText.Should().Be(deleted.DeletionSummary).And.Be("削除したもの: b-tt。既に無かったもの: なし。リソースグループ rg は残しています。");
        list.Filters.ValuesOf(nameof(JobSortKey.Subscription)).Select(v => v.Value).Should().Equal(["sub"], "サブスクリプションの表示が無ければ ID");
        list.Filters.ValuesOf(nameof(JobSortKey.Time)).Select(v => v.Value).Should().Equal(
            DateTimeOffset.Now.AddHours(1).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), "すぐ実行");
        list.Filters.ValuesOf(nameof(JobSortKey.Duration)).Select(v => v.Value).Should().Equal(deleted.DurationText!, "（なし）");
        list.Filters.ValuesOf(JobListViewModel.ResultColumn).Select(v => v.Value).Should().Equal(deleted.ResultText!, "（なし）");
        scheduled.ResultText.Should().BeNull();
    }

    [Fact]
    public async Task A_Deployment_Result_Without_An_Expiry_Should_Have_No_Expiry_Summary()
    {
        // 期限を記録しなかったデプロイの結果（デスクトップアプリのデプロイは必ず記録するが、結果の型としては無いこともある）
        var jobs = new JobManager(new InMemoryJobStore(), TimeProvider.System, NullLogger<JobManager>.Instance);
        var job = jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "demo"), "local",
            _ => Task.FromResult<object?>(new DeploymentResult("rg", "demo-env", "demo-tt", "demo-db", "demo-redis", "https://demo-tt.example.io"))).Job!;
        var vm = new JobViewModel(job);

        await vm.WatchAsync(TimeProvider.System, TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);

        vm.StatusText.Should().Be("完了");
        vm.ExpirySummary.Should().BeNull();
        vm.ResultText.Should().Be("https://demo-tt.example.io/");
    }

    [Fact]
    public async Task The_History_Should_Record_Jobs_That_Finished()
    {
        var h = await new Harness().ReadyAsync();
        h.Main.History.Should().NotBeNull();

        var job = h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "demo"), null,
            () => Task.FromException<Job>(new InvalidOperationException("失敗しました")));
        await job.Finished;

        h.History.Records.Should().ContainSingle().Which.State.Should().Be(DesktopJobState.Failed);
    }

    [Fact]
    public async Task A_Template_Saved_Without_A_Subscription_Should_Not_Change_The_Subscription()
    {
        var h = new Harness(time: _time);
        h.Azure.SubscriptionsByTenant["t1"].Add(new("sub2", "本番", "Enabled", "t1"));
        await h.ReadyAsync();
        var vm = h.Main.Settings;
        h.Main.Target.SelectedSubscription.Should().BeNull("2 つ以上あれば選ばない");
        vm.TemplateName = "サブスクリプションなし";

        await vm.SaveTemplateCommand.ExecuteAsync();

        h.Preferences.Templates.Should().ContainSingle().Which.SubscriptionId.Should().BeNull();
        h.Main.Target.SelectedSubscription = h.Main.Target.Subscriptions[0];
        await vm.ApplyTemplateCommand.ExecuteAsync();
        h.Main.Target.SelectedSubscription!.SubscriptionId.Should().Be(h.Main.Target.Subscriptions[0].SubscriptionId);
        vm.Message.Should().Be("テンプレート「サブスクリプションなし」を入力しました（パスワードはそのままです）。");
    }

    [Fact]
    public async Task Jobs_That_Already_Finished_Should_Not_Be_Counted_As_Not_Started()
    {
        var h = new Harness(time: _time);
        var failed = h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "a"), null,
            () => Task.FromException<Job>(new InvalidOperationException("失敗しました")));
        await failed.Finished;
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "b"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);

        h.Main.Integration.ScheduledText.Should().Be(
            "まだ始めていないジョブ: 0 件は、次にアプリを起動してサインインしたときに戻します（予定の時刻を過ぎていたら一時停止で戻します）。" +
            "デプロイ 1 件は残さないと選んだため、アプリの終了・Windows の再起動・サインアウトで消えます。");
    }

    [Fact]
    public void Typing_The_Same_Filter_Text_Again_Should_Not_Filter_Again()
    {
        var filters = new TableFilters<string>(
            [new TableColumn<string>("Name", "名前", s => s), new TableColumn<string>("At", "日付", _ => "-", date: _ => null)], () => ["alpha", "beta"]);
        var changed = 0;
        filters.Changed += () => changed++;

        filters.Text = "beta";
        filters.Text = " beta ";

        changed.Should().Be(1, "前後の空白を除くと同じ文字");
        filters.Text.Should().Be("beta");
        filters.Matches("alpha").Should().BeFalse();
    }

    [Fact]
    public async Task A_Session_For_A_Subscription_Not_In_The_List_Should_Use_The_Account_Credential()
    {
        var h = await new Harness().ReadyAsync();
        var logger = new DesktopDeploymentLogger(NullLogger.Instance);

        h.Main.Target.CreateSessionFor("sub").CreateGateway(logger);
        h.Main.Target.CreateSessionFor("unknown").CreateGateway(logger);

        h.Azure.Credentials[^2].Should().BeOfType<TenantScopedCredential>().Which.TenantId.Should().Be("t1");
        h.Azure.Credentials[^1].Should().BeSameAs(h.SignIn.Credential, "一覧に無ければテナントが分からないため、アカウントのテナントで操作する");
    }
}
