using Azure;
using FluentAssertions;
using Moq;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>時刻を進められる時計（UTC+9。タイマーはシステムのものを使う）。</summary>
internal sealed class MovableTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TokyoTime.Zone;
}

/// <summary>デプロイの一覧（docs/design/desktop-jobs.md の 6.3・6.4）。</summary>
public class DeploymentListViewModelTests
{
    private readonly MovableTime _time = new();

    private static Dictionary<string, string> Tags(string appName, string? expires = null, string? deployed = null)
    {
        var tags = new Dictionary<string, string>(ManagedTags.For(appName), StringComparer.OrdinalIgnoreCase);
        if (expires != null)
            tags[DeploymentExpiry.ExpiresAtKey] = expires;
        if (deployed != null)
            tags[DeploymentExpiry.DeployedAtKey] = deployed;
        return tags;
    }

    /// <summary>3 つの Container App と Environment（期限とデプロイ日は Timetracker のタグ）。</summary>
    private static IEnumerable<ManagedResourceRecord> Deployment(
        string sub, string rg, string app, string? expires = null, string? deployed = null, string? runningStatus = "Running", string? provisioning = "Succeeded")
    {
        ManagedResourceRecord App(string suffix, IReadOnlyDictionary<string, string> tags, ContainerAppSnapshot snapshot) =>
            new($"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.App/containerApps/{app}{suffix}", app + suffix,
                ManagedResourceRecord.ContainerAppType, sub, rg, "japaneast", tags, snapshot);
        yield return App("-tt", Tags(app, expires, deployed),
            new ContainerAppSnapshot(provisioning, runningStatus, "densocreate/timetracker:11", 1.5, "3Gi", $"{app}-tt.example.io", []));
        yield return App("-db", Tags(app), new ContainerAppSnapshot("Succeeded", "Running", "postgres:16", 1, "2Gi", null, []));
        yield return App("-redis", Tags(app), new ContainerAppSnapshot("Succeeded", "Running", "redis:7", null, null, null, []));
        yield return new ManagedResourceRecord($"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.App/managedEnvironments/{app}-env",
            app + "-env", ManagedResourceRecord.EnvironmentType, sub, rg, "japaneast", Tags(app));
    }

    private async Task<Harness> LoadedAsync(params IEnumerable<ManagedResourceRecord>[] deployments)
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.Azure.RecordsByTenant["t1"] = deployments.SelectMany(d => d).ToList();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        return h;
    }

    [Fact]
    public async Task Loading_Without_Subscriptions_Should_Ask_To_Sign_In()
    {
        var vm = new Harness(time: _time).Main.Deployments;
        vm.PageText.Should().StartWith("「読み込む」を押すと");

        await vm.LoadCommand.ExecuteAsync();

        vm.Error.Should().StartWith("サブスクリプションがありません。");
        vm.HasError.Should().BeTrue();
        vm.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_Tenant_Should_Be_Queried_With_Its_Subscriptions_And_Failures_Noted()
    {
        var h = new Harness(time: _time);
        h.Azure.TenantList.Add(new("t2", "本番", null));
        h.Azure.TenantList.Add(new("t3", null, "t3.example.com"));
        h.Azure.SubscriptionsByTenant["t1"].Add(new("sub2", "検証 2", "Enabled", "t1"));
        h.Azure.SubscriptionsByTenant["t2"] = [new("sub3", "本番", "Enabled", "t2")];
        h.Azure.SubscriptionsByTenant["t3"] = [new("sub4", "別", "Enabled", "t3")];
        h.Azure.DirectoryErrors["t2"] = new RequestFailedException(403, "拒否");
        h.Azure.RecordsByTenant["t1"] = Deployment("sub", "rg", "demo").ToList();
        h.Azure.RecordsByTenant["t3"] = Deployment("sub4", "rg4", "other").ToList();
        await h.ReadyAsync();
        var vm = h.Main.Deployments;

        await vm.LoadCommand.ExecuteAsync();

        h.Azure.DirectoryQueries.Should().HaveCount(3);
        h.Azure.DirectoryQueries.Single(q => q.TenantId == "t1").Subscriptions.Should().BeEquivalentTo("sub", "sub2");
        vm.Error.Should().BeNull();
        vm.Notices.Should().HaveCount(2);
        vm.Notices[0].Should().StartWith("テナント 本番 のデプロイを読めませんでした: Azure がエラーを返しました（403");
        vm.Notices[1].Should().StartWith("Azure に反映されるまで数分かかることがあります。");
        // 既定の並びは、サブスクリプションの表示（名前と ID）の順
        vm.Rows.Select(r => r.AppName).Should().Equal("other", "demo");
        vm.TotalCount.Should().Be(2);
        vm.PageText.Should().Be("1〜2 件目 / 2 件　1 / 1 ページ");
        vm.Busy.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task When_Every_Tenant_Fails_The_List_Should_Not_Change()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.Azure.DirectoryErrors["t1"] = new RequestFailedException(500, "失敗");

        await h.Main.Deployments.LoadCommand.ExecuteAsync();

        h.Main.Deployments.Error.Should().Be("デプロイの一覧を読めませんでした。注意の内容を確認してください。");
        h.Main.Deployments.PageText.Should().StartWith("「読み込む」を押すと");
    }

    [Fact]
    public async Task A_Row_Should_Show_The_Deployment()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo", "2026-11-23T14:59:59Z", "2026-10-21T01:30:00Z"));
        var row = h.Main.Deployments.Rows.Single();

        row.Subscription.Should().Be("検証（sub）");
        (row.ResourceGroup, row.AppName).Should().Be(("rg", "demo"));
        row.Url.Should().Be(new Uri("https://demo-tt.example.io"));
        row.HasUrl.Should().BeTrue();
        row.DeployedText.Should().Be("2026-10-21 10:30");
        row.ExpiryText.Should().Be("2026-11-23");
        row.Level.Should().Be(ExpiryLevel.Normal);
        (row.IsExpired, row.IsExpirySoon).Should().Be((false, false));
        row.DbTypeText.Should().Be("PostgreSQL");
        row.PersistenceText.Should().Be("永続化していない");
        row.SpecText.Should().Be("Timetracker 1.5 vCPU / 3Gi、DB 1 vCPU / 2Gi、Redis ? vCPU / ?");
        row.StateText.Should().Be("Running");
        row.Entry.Location.Should().Be("japaneast");

        await row.OpenCommand.ExecuteAsync();
        h.Launcher.Opened.Should().Equal("https://demo-tt.example.io/");
    }

    [Theory]
    [InlineData("2026-10-21T14:59:59Z", "2026-10-21（過ぎています）", ExpiryLevel.Expired)]
    [InlineData("2026-10-28T14:59:59Z", "2026-10-28（7 日以内）", ExpiryLevel.Soon)]
    [InlineData("none", "期限なし", ExpiryLevel.None)]
    [InlineData("someday", "記録なし", ExpiryLevel.None)]
    public async Task The_Expiry_Should_Be_Colored_By_How_Near_It_Is(string tag, string text, ExpiryLevel level)
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo", tag));
        var row = h.Main.Deployments.Rows.Single();

        (row.ExpiryText, row.Level).Should().Be((text, level));
        row.IsExpired.Should().Be(level == ExpiryLevel.Expired);
        row.IsExpirySoon.Should().Be(level == ExpiryLevel.Soon);
        row.DeployedText.Should().Be("記録なし");
    }

    [Fact]
    public async Task Other_States_Should_Be_Shown()
    {
        var persisted = Deployment("sub", "rg", "p", provisioning: null, runningStatus: null).Select(r => r.Name == "p-db"
            ? r with { App = new ContainerAppSnapshot("Succeeded", "Running", "mcr.microsoft.com/mssql/server:2022", 1, "2Gi", null, ["dbdata"]) }
            : r);
        var leftover = new ManagedResourceRecord("/s/old", "ttold12345678", ManagedResourceRecord.StorageAccountType, "unknown-sub", "rg", "japaneast", Tags("old"));
        var provisioning = Deployment("sub", "rg", "q", runningStatus: null, provisioning: "InProgress").Select(r => r.Name == "q-db"
            ? r with { App = new ContainerAppSnapshot(null, null, "custom/db", null, null, null, []) }
            : r);
        var h = await LoadedAsync(persisted, [leftover], provisioning);
        var rows = h.Main.Deployments.Rows.ToDictionary(r => r.AppName);

        (rows["p"].DbTypeText, rows["p"].PersistenceText, rows["p"].StateText).Should().Be(("SQL Server", "Azure Files に永続化", "不明"));
        (rows["q"].DbTypeText, rows["q"].StateText).Should().Be(("不明", "InProgress"));
        var old = rows["old"];
        old.Subscription.Should().Be("unknown-sub");
        old.PersistenceText.Should().Be("永続化していない（以前の DB のデータが残っています）");
        old.StateText.Should().Be("一部だけ（Container Apps Environment・Timetracker・DB・Redis がありません）");
        old.SpecText.Should().BeEmpty();
        old.HasUrl.Should().BeFalse();
        old.OpenCommand.CanExecute(null).Should().BeFalse();
        old.ShowPasswordsCommand.CanExecute(null).Should().BeFalse();
        old.ChangeExpiryCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Filters_Should_Narrow_The_List()
    {
        var h = new Harness(time: _time);
        h.Azure.SubscriptionsByTenant["t1"].Add(new("sub2", "本番", "Enabled", "t1"));
        h.Azure.RecordsByTenant["t1"] =
        [
            .. Deployment("sub", "rg-a", "a", "2026-10-25T14:59:59Z", "2026-10-01T00:00:00Z"),
            .. Deployment("sub", "rg-b", "b", "none", "2026-10-10T00:00:00Z"),
            .. Deployment("sub2", "rg-c", "c", "2026-12-31T14:59:59Z", "2026-10-20T00:00:00Z"),
            .. Deployment("sub2", "other", "d"),
        ];
        await h.ReadyAsync();
        var vm = h.Main.Deployments;
        await vm.LoadCommand.ExecuteAsync();
        vm.Sort = DeploymentListViewModel.SortOptions[2];
        string[] Names() => vm.Rows.Select(r => r.AppName).ToArray();
        DateTimeOffset Day(int month, int day) => new(2026, month, day, 0, 0, 0, TimeSpan.FromHours(9));

        vm.SubscriptionFilter = " 本番 ";
        Names().Should().Equal("d", "c");
        vm.SubscriptionFilter = "SUB2";
        Names().Should().Equal("d", "c");
        vm.SubscriptionFilter = "none";
        Names().Should().BeEmpty();
        vm.PageText.Should().Be("表示するデプロイはありません（全 4 件）。");
        vm.SubscriptionFilter = "";

        vm.ResourceGroupFilter = "RG-";
        Names().Should().Equal("a", "b", "c");
        vm.PageText.Should().Be("1〜3 件目 / 3 件（全 4 件）　1 / 1 ページ");
        vm.ResourceGroupFilter = "";

        vm.DeployedFrom = Day(10, 10);
        Names().Should().Equal("b", "c");
        vm.DeployedTo = Day(10, 15);
        Names().Should().Equal("b");
        vm.DeployedFrom = null;
        Names().Should().Equal("a", "b");
        vm.DeployedTo = null;

        var changes = new ChangeRecorder(vm);
        vm.ExpiryFilter = DeploymentListViewModel.ExpiryFilters[1];
        Names().Should().Equal("a");
        vm.ExpiryFilter = DeploymentListViewModel.ExpiryFilters[2];
        Names().Should().Equal("b");
        vm.IsExpiryRange.Should().BeFalse();
        vm.ExpiryFilter = DeploymentListViewModel.ExpiryFilters[3];
        changes.Names.Should().Contain(nameof(DeploymentListViewModel.IsExpiryRange));
        vm.IsExpiryRange.Should().BeTrue();
        Names().Should().Equal("a", "c");
        vm.ExpiryFrom = Day(11, 1);
        Names().Should().Equal("c");
        vm.ExpiryTo = Day(12, 30);
        Names().Should().BeEmpty();
        vm.ExpiryFrom = null;
        Names().Should().Equal("a");
        vm.ExpiryTo = null;
        vm.ExpiryFilter = DeploymentListViewModel.ExpiryFilters[0];
        Names().Should().HaveCount(4);
    }

    [Fact]
    public async Task Sorting_Should_Put_Missing_Values_Last()
    {
        var h = new Harness(time: _time);
        h.Azure.SubscriptionsByTenant["t1"].Add(new("sub2", "A 番", "Enabled", "t1"));
        h.Azure.RecordsByTenant["t1"] =
        [
            .. Deployment("sub", "rg-b", "a", "2026-11-01T14:59:59Z", "2026-10-02T00:00:00Z"),
            .. Deployment("sub", "rg-a", "b", "none", "2026-10-01T00:00:00Z"),
            .. Deployment("sub2", "rg-c", "c", "2026-10-30T14:59:59Z"),
        ];
        await h.ReadyAsync();
        var vm = h.Main.Deployments;
        await vm.LoadCommand.ExecuteAsync();
        string[] Sorted(int option)
        {
            vm.Sort = DeploymentListViewModel.SortOptions[option];
            return vm.Rows.Select(r => r.AppName).ToArray();
        }

        Sorted(0).Should().Equal("c", "b", "a");
        Sorted(1).Should().Equal("b", "a", "c");
        Sorted(2).Should().Equal("b", "a", "c");
        Sorted(3).Should().Equal("c", "a", "b");
        Sorted(4).Should().Equal("b", "a", "c");
        Sorted(5).Should().Equal("a", "b", "c");
        Sorted(6).Should().Equal("c", "a", "b");
        Sorted(7).Should().Equal("a", "c", "b");
    }

    [Fact]
    public async Task Pages_Should_Move_And_Reset_When_The_Size_Changes()
    {
        var h = await LoadedAsync(Enumerable.Range(1, 45).Select(i => Deployment("sub", "rg", $"app{i:00}")).ToArray());
        var vm = h.Main.Deployments;

        vm.PageCount.Should().Be(3);
        vm.Rows.Should().HaveCount(20);
        vm.PreviousPageCommand.CanExecute(null).Should().BeFalse();
        await vm.NextPageCommand.ExecuteAsync();
        await vm.NextPageCommand.ExecuteAsync();
        vm.PageIndex.Should().Be(2);
        vm.Rows.Select(r => r.AppName).Should().Equal("app41", "app42", "app43", "app44", "app45");
        vm.PageText.Should().Be("41〜45 件目 / 45 件　3 / 3 ページ");
        vm.NextPageCommand.CanExecute(null).Should().BeFalse();
        await vm.PreviousPageCommand.ExecuteAsync();
        vm.Rows[0].AppName.Should().Be("app21");

        vm.PageSize = 50;
        (vm.PageIndex, vm.PageCount, vm.Rows.Count).Should().Be((0, 1, 45));
        vm.PageSize = 50;
        DeploymentListViewModel.PageSizes.Should().Equal(20, 50, 100);
    }

    [Fact]
    public async Task Row_Actions_Should_Put_The_Deployment_In_The_Target()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo"));
        h.Azure.Deployed();
        h.Main.Target.ResourceGroup = "elsewhere";
        var row = h.Main.Deployments.Rows.Single();

        await row.UseAsTargetCommand.ExecuteAsync();
        (h.Main.Target.Subscription, h.Main.Target.ResourceGroup, h.Main.Target.AppName).Should().Be(("sub", "rg", "demo"));
        await row.DeleteCommand.ExecuteAsync();
        await row.CheckStatusCommand.ExecuteAsync();

        h.Navigations.Should().Equal("settings", "delete", "status");
        h.Main.Status.Summary.Should().Be("Container Apps Environment demo-env（japaneast、Succeeded）");
        h.Main.Deployments.Error.Should().BeNull();
    }

    [Fact]
    public async Task A_Subscription_No_Longer_Listed_Should_Not_Become_The_Target()
    {
        var h = await LoadedAsync(Deployment("gone", "rg", "demo"));

        await h.Main.Deployments.Rows.Single().UseAsTargetCommand.ExecuteAsync();

        h.Main.Deployments.Error.Should().StartWith("サブスクリプション gone が「サインインと対象」の一覧にありません。");
        h.Navigations.Should().BeEmpty();
    }

    [Fact]
    public async Task Passwords_Should_Be_Read_Only_When_Asked_And_Masked()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo"));
        h.Azure.Deployed();
        h.Azure.GatewayMock.Setup(g => g.GetContainerAppSecretsAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["db-password"] = "DbPassw0rd!" });
        var row = h.Main.Deployments.Rows.Single();
        row.HasPasswords.Should().BeFalse();
        row.DbPasswordText.Should().BeNull();
        row.TogglePasswordsCommand.CanExecute(null).Should().BeFalse();
        row.CopyDbPasswordCommand.CanExecute(null).Should().BeFalse();
        var changes = new ChangeRecorder(row);

        await row.ShowPasswordsCommand.ExecuteAsync();

        row.HasPasswords.Should().BeTrue();
        (row.DbPasswordText, row.TrackerPasswordText).Should().Be((DeploymentRowViewModel.Mask, "（ありません）"));
        await row.TogglePasswordsCommand.ExecuteAsync();
        row.IsPasswordRevealed.Should().BeTrue();
        changes.Names.Should().Contain(nameof(DeploymentRowViewModel.DbPasswordText)).And.Contain(nameof(DeploymentRowViewModel.TrackerPasswordText));
        row.DbPasswordText.Should().Be("DbPassw0rd!");
        row.CopyTrackerPasswordCommand.CanExecute(null).Should().BeFalse();
        await row.CopyDbPasswordCommand.ExecuteAsync();
        h.Clipboard.Copied.Should().Equal("DbPassw0rd!");
        row.Message.Should().Be("DB のパスワードをコピーしました（クリップボードの履歴には残しません）。");

        // 読み直すと行を作り直し、パスワードは消える
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        h.Main.Deployments.Rows.Single().HasPasswords.Should().BeFalse();
    }

    [Fact]
    public async Task Both_Passwords_Should_Be_Copyable()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo"));
        h.Azure.Deployed();
        h.Azure.GatewayMock.Setup(g => g.GetContainerAppSecretsAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["db-password"] = "DbPassw0rd!", ["app-password"] = "TrackerPass!" });
        var row = h.Main.Deployments.Rows.Single();

        await row.ShowPasswordsCommand.ExecuteAsync();
        await row.CopyTrackerPasswordCommand.ExecuteAsync();

        h.Clipboard.Copied.Should().Equal("TrackerPass!");
        row.TrackerPasswordText.Should().Be(DeploymentRowViewModel.Mask);
    }

    [Fact]
    public async Task Without_A_Clipboard_Passwords_Cannot_Be_Copied()
    {
        var h = await new Harness(time: _time, withClipboard: false).ReadyAsync();
        h.Azure.RecordsByTenant["t1"] = Deployment("sub", "rg", "demo").ToList();
        h.Azure.Deployed();
        h.Azure.GatewayMock.Setup(g => g.GetContainerAppSecretsAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["db-password"] = "DbPassw0rd!", ["app-password"] = "TrackerPass!" });
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        var row = h.Main.Deployments.Rows.Single();

        await row.ShowPasswordsCommand.ExecuteAsync();

        row.CopyDbPasswordCommand.CanExecute(null).Should().BeFalse();
        row.CopyTrackerPasswordCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Password_Failures_Should_Be_Shown()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo"));
        var row = h.Main.Deployments.Rows.Single();

        // 本ツールで作った Timetracker アプリが（もう）無い
        await row.ShowPasswordsCommand.ExecuteAsync();
        row.Error.Should().Contain("本ツールで作った Timetracker の Container App（demo-tt）がありません");
        row.HasError.Should().BeTrue();

        h.Azure.Deployed();
        h.Azure.GatewayMock.Setup(g => g.GetContainerAppSecretsAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "listSecrets が拒否されました"));
        await row.ShowPasswordsCommand.ExecuteAsync();
        row.Error.Should().StartWith("Azure がエラーを返しました（403");
        row.HasPasswords.Should().BeFalse();
    }

    [Fact]
    public async Task The_Expiry_Should_Be_Changed_And_Kept_Until_Azure_Reflects_It()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo", "2026-11-23T14:59:59Z"));
        h.Azure.Deployed();
        var updated = new List<(string Name, IReadOnlyDictionary<string, string> Tags)>();
        h.Azure.GatewayMock.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, n, t, _) => updated.Add((n, t)))
            .Returns(Task.CompletedTask);
        var vm = h.Main.Deployments;
        var row = vm.Rows.Single();
        row.EditNoExpiry.Should().BeFalse();
        row.EditExpiryDate.Should().Be(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.FromHours(9)));

        row.EditExpiryDate = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(9));
        await row.ChangeExpiryCommand.ExecuteAsync();

        updated.Select(u => u.Name).Should().Equal("demo-tt", "demo-db", "demo-redis");
        updated[0].Tags.Should().Equal(new Dictionary<string, string> { [DeploymentExpiry.ExpiresAtKey] = "2026-12-01T14:59:59Z" });
        // 同じ行のまま、内容だけ変わる
        vm.Rows.Single().Should().BeSameAs(row);
        row.ExpiryText.Should().Be("2026-12-01");
        row.Message.Should().Be("利用可能期限を 2026-12-01 にしました。");

        // Resource Graph に反映される前に読み直しても、変えた期限を出す
        await vm.LoadCommand.ExecuteAsync();
        vm.Rows.Single().ExpiryText.Should().Be("2026-12-01");
        // 15 分を過ぎたら、Azure から読んだ値に戻す
        _time.Now += DeploymentListViewModel.RecentWindow + TimeSpan.FromSeconds(1);
        await vm.LoadCommand.ExecuteAsync();
        vm.Rows.Single().ExpiryText.Should().Be("2026-11-23");

        vm.Rows.Single().EditNoExpiry = true;
        await vm.Rows.Single().ChangeExpiryCommand.ExecuteAsync();
        vm.Rows.Single().ExpiryText.Should().Be("期限なし");
    }

    [Fact]
    public async Task Expiry_Failures_Should_Be_Shown()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo", "none"));
        var row = h.Main.Deployments.Rows.Single();
        row.EditNoExpiry.Should().BeTrue();
        row.EditExpiryDate.Should().Be(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.FromHours(9)));
        row.EditNoExpiry = false;

        row.EditExpiryDate = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(9));
        await row.ChangeExpiryCommand.ExecuteAsync();
        row.Error.Should().StartWith("利用可能期限日（2026-10-21）は、今日以降の日付を指定してください。");

        row.EditExpiryDate = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(9));
        await row.ChangeExpiryCommand.ExecuteAsync();
        row.Error.Should().Contain("本ツールで作った Container App がありません");

        h.Azure.Deployed();
        h.Azure.GatewayMock.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "タグを書けません"));
        await row.ChangeExpiryCommand.ExecuteAsync();
        row.Error.Should().StartWith("Azure がエラーを返しました（403");
        row.ExpiryText.Should().Be("期限なし");
    }

    [Fact]
    public async Task A_Deployment_Finished_In_This_App_Should_Appear_Before_Azure_Reflects_It()
    {
        var h = await LoadedAsync();
        h.Azure.ResourceGroup = null;
        h.Main.Deployments.Rows.Should().BeEmpty();

        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        var row = h.Main.Deployments.Rows.Single();
        (row.AppName, row.Url, row.DeployedText, row.ExpiryText).Should().Be(("demo", new Uri("https://demo-tt.example.io"), "2026-10-22 05:00", "2026-11-23"));
        row.StateText.Should().Be("不明");
        row.PersistenceText.Should().Be("永続化していない");
    }

    [Fact]
    public async Task A_Redeployment_Should_Update_The_Listed_Row()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo", "none"));
        h.Azure.Deployed();
        h.Azure.DeployedUrl = null;
        h.Main.Settings.Expiry = SettingsViewModel.ExpiryOptions[1];
        h.Main.Settings.ExpiryDate = new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.FromHours(9));

        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        h.Main.Deployment.DataLossAcknowledged = true;
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        var row = h.Main.Deployments.Rows.Single();
        (row.ExpiryText, row.Url, row.StateText).Should().Be(("2026-12-05", new Uri("https://demo-tt.example.io"), "Running"));
    }

    [Fact]
    public async Task A_Deletion_Finished_In_This_App_Should_Remove_The_Deleted_Resources()
    {
        var records = Deployment("sub", "rg", "demo").Append(new ManagedResourceRecord(
            "/s/st", FakeAzure.StorageAccountName, ManagedResourceRecord.StorageAccountType, "sub", "rg", "japaneast", Tags("demo"))).ToList();
        var h = await LoadedAsync(records);
        h.Azure.Deployed(persisted: true);
        var deletion = h.Main.Deletion;
        deletion.KeepDbData = true;

        await deletion.PlanCommand.ExecuteAsync();
        deletion.ConfirmAppName = "demo";
        await deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        var row = h.Main.Deployments.Rows.Single();
        row.StateText.Should().Be("一部だけ（Container Apps Environment・Timetracker・DB・Redis がありません）");
        row.Entry.StorageAccountName.Should().Be(FakeAzure.StorageAccountName);

        // DB のデータも消すと、一覧から消える
        deletion.KeepDbData = false;
        await deletion.PlanCommand.ExecuteAsync();
        deletion.ConfirmAppName = "demo";
        await deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        h.Main.Deployments.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_The_Resource_Group_Should_Remove_The_Row()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo"));
        h.Azure.Deployed();
        h.Azure.ResourceGroup = new ResourceGroupInfo("rg", "japaneast", ManagedTags.ForResourceGroup());
        var deletion = h.Main.Deletion;

        deletion.DeleteResourceGroup = true;
        await deletion.PlanCommand.ExecuteAsync();
        deletion.ConfirmAppName = "demo";
        await deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        h.Main.CurrentJob!.StatusText.Should().Be("完了");
        h.Main.Deployments.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Deletion_Of_Something_Not_Listed_Should_Change_Nothing()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "other"));
        h.Azure.Deployed();
        var deletion = h.Main.Deletion;

        await deletion.PlanCommand.ExecuteAsync();
        deletion.ConfirmAppName = "demo";
        await deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        h.Main.Deployments.Rows.Select(r => r.AppName).Should().Equal("other");
    }

    [Fact]
    public async Task Signing_Out_Should_Clear_The_List()
    {
        var h = await LoadedAsync(Deployment("sub", "rg", "demo"));

        await h.Main.SignIn.SignOutCommand.ExecuteAsync();

        h.Main.Deployments.Rows.Should().BeEmpty();
        h.Main.Deployments.PageText.Should().StartWith("「読み込む」を押すと");
    }
}
