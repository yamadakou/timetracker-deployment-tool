using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Application.Tests;

/// <summary>デプロイの一覧のまとめ方（docs/design/desktop-jobs.md の 6.4）。</summary>
public class DeploymentCatalogTests
{
    private static Dictionary<string, string> Tags(string appName, params (string Key, string Value)[] extra)
    {
        var tags = new Dictionary<string, string>(ManagedTags.For(appName), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in extra)
            tags[key] = value;
        return tags;
    }

    private static ManagedResourceRecord App(
        string name, IReadOnlyDictionary<string, string> tags, string sub = "s1", string rg = "rg", ContainerAppSnapshot? snapshot = null) =>
        new($"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.App/containerApps/{name}", name, ManagedResourceRecord.ContainerAppType,
            sub, rg, "japaneast", tags, snapshot ?? new ContainerAppSnapshot("Succeeded", "Running", null, 0.5, "1Gi", null, []));

    private static ManagedResourceRecord Env(string name, IReadOnlyDictionary<string, string> tags, string sub = "s1", string rg = "rg", string location = "japaneast") =>
        new($"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.App/managedEnvironments/{name}", name, ManagedResourceRecord.EnvironmentType,
            sub, rg, location, tags);

    private static ManagedResourceRecord Storage(string name, IReadOnlyDictionary<string, string> tags, string sub = "s1", string rg = "rg") =>
        new($"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{name}", name, ManagedResourceRecord.StorageAccountType,
            sub, rg, "", tags);

    [Fact]
    public void A_Complete_Deployment_Should_Be_One_Entry()
    {
        var tags = Tags("demo", (DeploymentExpiry.ExpiresAtKey, "2026-11-23T14:59:59Z"), (DeploymentExpiry.DeployedAtKey, "2026-10-22T01:00:00Z"));
        var entries = DeploymentCatalog.Build(
        [
            App("demo-tt", tags, snapshot: new ContainerAppSnapshot("Succeeded", "Running", "densocreate/timetracker:11", 1.5, "3Gi", "demo-tt.example.io", [])),
            App("demo-db", Tags("demo"), snapshot: new ContainerAppSnapshot("Succeeded", "Running", "mcr.microsoft.com/mssql/server:2022", 1, "2Gi", null, ["dbdata"])),
            App("demo-redis", Tags("demo")),
            Env("demo-env", Tags("demo")),
            Storage("ttdemo12345678", Tags("demo")),
        ]);

        var e = entries.Should().ContainSingle().Subject;
        (e.SubscriptionId, e.ResourceGroup, e.AppName, e.Location, e.Url).Should().Be(("s1", "rg", "demo", "japaneast", "https://demo-tt.example.io"));
        e.DeployedAt.Should().Be(new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.Zero));
        (e.ExpiryKind, e.ExpiresAt).Should().Be((ExpiryKind.Date, new DateTimeOffset(2026, 11, 23, 14, 59, 59, TimeSpan.Zero)));
        e.Timetracker.Should().Be(new AppEntry("demo-tt", "Succeeded", "Running", "densocreate/timetracker:11", 1.5, "3Gi"));
        e.Db!.Cpu.Should().Be(1);
        e.Redis!.Name.Should().Be("demo-redis");
        (e.EnvironmentName, e.StorageAccountName, e.DbPersisted, e.DbType).Should().Be(("demo-env", "ttdemo12345678", true, "sqlserver"));
        e.IsComplete.Should().BeTrue();
        e.Missing.Should().BeEmpty();
    }

    [Fact]
    public void Expiry_Should_Come_From_The_First_App_With_The_Tag()
    {
        var entries = DeploymentCatalog.Build(
        [
            App("demo-tt", Tags("demo")),
            App("demo-db", Tags("demo", (DeploymentExpiry.ExpiresAtKey, "NONE"))),
            App("demo-redis", Tags("demo", (DeploymentExpiry.ExpiresAtKey, "2026-11-23T14:59:59Z"))),
        ]);

        (entries[0].ExpiryKind, entries[0].ExpiresAt, entries[0].DeployedAt).Should().Be((ExpiryKind.None, (DateTimeOffset?)null, (DateTimeOffset?)null));
    }

    [Fact]
    public void Expiry_Should_Be_Combined_Over_Every_Resource_And_Differences_Noted()
    {
        // 期限は遅い方、デプロイ日は新しい方。Environment・ストレージアカウントのタグも見る
        var entries = DeploymentCatalog.Build(
        [
            App("demo-tt", Tags("demo", (DeploymentExpiry.ExpiresAtKey, "2026-11-01T14:59:59Z"), (DeploymentExpiry.DeployedAtKey, "2026-10-01T00:00:00Z"))),
            Env("demo-env", Tags("demo", (DeploymentExpiry.ExpiresAtKey, "2026-11-30T14:59:59Z"), (DeploymentExpiry.DeployedAtKey, "2026-10-02T00:00:00Z"))),
            Storage("ttdemo12345678", Tags("demo", (DeploymentExpiry.DeployedAtKey, "bad"))),
        ]);

        var e = entries.Single();
        (e.ExpiryKind, e.ExpiresAt).Should().Be((ExpiryKind.Date, new DateTimeOffset(2026, 11, 30, 14, 59, 59, TimeSpan.Zero)));
        e.DeployedAt.Should().Be(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        e.ExpiryDiffers.Should().BeTrue();

        // 残ったストレージアカウントの期限なしは、期限なし
        DeploymentCatalog.Build([Storage("ttold12345678", Tags("old", (DeploymentExpiry.ExpiresAtKey, "none")))]).Single().ExpiryKind.Should().Be(ExpiryKind.None);
    }

    [Fact]
    public void A_Partial_Deployment_Should_Tell_What_Is_Missing()
    {
        var entries = DeploymentCatalog.Build(
        [
            Env("part-env", Tags("part"), location: "eastus"),
            Storage("ttpart12345678", Tags("part")),
            // 読めない期限は「無い」と同じ
            App("part-db", Tags("part", (DeploymentExpiry.ExpiresAtKey, "someday")),
                snapshot: new ContainerAppSnapshot(null, null, "postgres:16", null, null, null, [])),
        ]);

        var e = entries.Single();
        e.Location.Should().Be("eastus");
        e.Url.Should().BeNull();
        e.ExpiryKind.Should().Be(ExpiryKind.Missing);
        e.DbType.Should().Be("postgresql");
        e.DbPersisted.Should().BeFalse();
        e.IsComplete.Should().BeFalse();
        e.Missing.Should().Equal("Timetracker", "Redis");
    }

    [Fact]
    public void Leftovers_Without_Apps_Or_Environment_Should_Still_Be_Listed()
    {
        var entries = DeploymentCatalog.Build([Storage("ttold12345678", Tags("old"))]);

        var e = entries.Single();
        e.Location.Should().BeNull();
        e.DbType.Should().BeNull();
        e.Missing.Should().Equal("Container Apps Environment", "Timetracker", "DB", "Redis");
    }

    [Fact]
    public void Entries_Should_Be_Grouped_Case_Insensitively_And_Sorted()
    {
        var entries = DeploymentCatalog.Build(
        [
            App("b-tt", Tags("B"), sub: "s2", rg: "rg"),
            App("a-tt", Tags("a"), sub: "S1", rg: "RG"),
            App("a-db", Tags("a"), sub: "s1", rg: "rg", snapshot: new ContainerAppSnapshot(null, null, "redis:7", null, null, null, [])),
            App("c-tt", Tags("c"), sub: "s1", rg: "other"),
            // アプリ名のタグが無いものは出さない
            new("/x", "x-tt", ManagedResourceRecord.ContainerAppType, "s1", "rg", "japaneast", new Dictionary<string, string> { ["managed-by"] = "ttdeploytool" }),
            // 名前の規則に合わない Container App は、どの役割にもしない
            App("unrelated", Tags("a"), sub: "s1", rg: "rg"),
        ]);

        entries.Select(e => (e.SubscriptionId, e.ResourceGroup, e.AppName)).Should().Equal(("s1", "other", "c"), ("S1", "RG", "a"), ("s2", "rg", "b"));
        entries[1].Timetracker!.Name.Should().Be("a-tt");
        entries[1].DbType.Should().BeNull();
        entries[1].IsFor("s1", "rg", "A").Should().BeTrue();
        entries[1].IsFor("s1", "rg", "b").Should().BeFalse();
        entries[1].IsFor("s1", "x", "a").Should().BeFalse();
        entries[1].IsFor("s2", "rg", "a").Should().BeFalse();
    }
}

/// <summary>一覧の行からの期限の変更とパスワードの読み取り（docs/design/desktop-jobs.md の 6.4）。</summary>
public class DeploymentMaintenanceTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero));
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];
    private readonly List<(string Name, IReadOnlyDictionary<string, string> Tags)> _updated = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeploymentMaintenanceTests()
    {
        _time.SetLocalTimeZone(Tokyo);
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps);
        _gateway.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, name, tags, _) => _updated.Add((name, tags)))
            .Returns(Task.CompletedTask);
    }

    private DeploymentMaintenance Sut() => new(_gateway.Object, _time);

    [Fact]
    public async Task ChangeExpiry_Should_Rewrite_The_Tag_Of_The_Managed_Apps_Only()
    {
        _apps.Add(new ContainerAppInfo("demo-tt", "env", ManagedTags.For("demo")));
        _apps.Add(new ContainerAppInfo("DEMO-DB", "env", ManagedTags.For("demo")));
        // 管理タグの無い・別のアプリ名のものは書き換えない
        _apps.Add(new ContainerAppInfo("demo-redis", "env", new Dictionary<string, string>()));
        _apps.Add(new ContainerAppInfo("other-tt", "env", ManagedTags.For("other")));

        var decision = await Sut().ChangeExpiryAsync("sub", "rg", "demo", "2026-12-01", Ct);

        decision.Should().Be(new ExpiryDecision(new DateTimeOffset(2026, 12, 1, 23, 59, 59, TimeSpan.FromHours(9)), ExpirySource.Specified));
        _updated.Select(u => u.Name).Should().Equal("demo-tt", "demo-db");
        _updated.Should().AllSatisfy(u => u.Tags.Should().Equal(new Dictionary<string, string> { [DeploymentExpiry.ExpiresAtKey] = "2026-12-01T14:59:59Z" }));
    }

    [Fact]
    public async Task ChangeExpiry_Should_Also_Rewrite_The_Managed_Environment_And_Storage_Account()
    {
        var account = ResourceNaming.StorageAccount("sub", "rg", "demo");
        var written = new List<string>();
        _apps.Add(new ContainerAppInfo("demo-tt", "env", ManagedTags.For("demo")));
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo("id", "demo-env", "japaneast", ManagedTags.For("demo")));
        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageAccountInfo(account, "japaneast", ManagedTags.For("demo")));
        _gateway.Setup(g => g.UpdateEnvironmentTagsAsync("sub", "rg", "demo-env", It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, n, t, _) => written.Add($"{n}={t[DeploymentExpiry.ExpiresAtKey]}"))
            .Returns(Task.CompletedTask);
        _gateway.Setup(g => g.UpdateStorageAccountTagsAsync("sub", "rg", account, It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, n, t, _) => written.Add($"{n}={t[DeploymentExpiry.ExpiresAtKey]}"))
            .Returns(Task.CompletedTask);

        await Sut().ChangeExpiryAsync("sub", "rg", "demo", "none", Ct);

        _updated.Select(u => u.Name).Should().Equal("demo-tt");
        written.Should().Equal("demo-env=none", $"{account}=none");
    }

    [Fact]
    public async Task ChangeExpiry_Should_Work_For_Leftovers_Without_Apps()
    {
        // 途中で失敗して、ストレージアカウントだけが残った
        var account = ResourceNaming.StorageAccount("sub", "rg", "demo");
        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageAccountInfo(account, "japaneast", ManagedTags.For("demo")));

        await Sut().ChangeExpiryAsync("sub", "rg", "demo", "2026-12-01", Ct);

        _updated.Should().BeEmpty();
        _gateway.Verify(g => g.UpdateStorageAccountTagsAsync("sub", "rg", account, It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
        _gateway.Verify(g => g.UpdateEnvironmentTagsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangeExpiry_Should_Allow_No_Expiry()
    {
        _apps.Add(new ContainerAppInfo("demo-redis", "env", ManagedTags.For("demo")));

        var decision = await Sut().ChangeExpiryAsync("sub", "rg", "demo", " none ", Ct);

        decision.ExpiresAt.Should().BeNull();
        _updated.Single().Tags[DeploymentExpiry.ExpiresAtKey].Should().Be("none");
    }

    [Theory]
    [InlineData("", "利用可能期限日（yyyy-MM-dd）か none を指定してください。")]
    [InlineData("12/01", "expires-on には yyyy-MM-dd の日付か none を指定してください: 12/01")]
    [InlineData("2026-10-21", "利用可能期限日（2026-10-21）は、今日以降の日付を指定してください。")]
    public async Task ChangeExpiry_Should_Reject_Wrong_Input_Without_Reading_Azure(string input, string message)
    {
        var act = () => Sut().ChangeExpiryAsync("sub", "rg", "demo", input, Ct);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().StartWith(message);
        _gateway.Verify(g => g.ListContainerAppsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangeExpiry_Should_Fail_When_No_App_Is_Left()
    {
        var act = () => Sut().ChangeExpiryAsync("sub", "rg", "demo", "2026-12-01", Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("本ツールで作ったリソースがありません");
        _updated.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadPasswords_Should_Read_The_Secrets_Of_The_Timetracker_App()
    {
        _apps.Add(new ContainerAppInfo("demo-tt", "env", ManagedTags.For("demo")));
        _gateway.Setup(g => g.GetContainerAppSecretsAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["db-password"] = "DbPassw0rd!", ["app-password"] = "TrackerPass!" });

        var passwords = await new DeploymentMaintenance(_gateway.Object).ReadPasswordsAsync("sub", "rg", "demo", Ct);

        passwords.Should().Be(new DeploymentPasswords("DbPassw0rd!", "TrackerPass!"));
    }

    [Fact]
    public async Task ReadPasswords_Should_Leave_Missing_Secrets_Empty()
    {
        _apps.Add(new ContainerAppInfo("demo-tt", "env", ManagedTags.For("demo")));
        _gateway.Setup(g => g.GetContainerAppSecretsAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<string, string>());

        (await Sut().ReadPasswordsAsync("sub", "rg", "demo", Ct)).Should().Be(new DeploymentPasswords(null, null));
    }

    [Fact]
    public async Task ReadPasswords_Should_Not_Read_An_App_That_Is_Not_Managed()
    {
        _apps.Add(new ContainerAppInfo("demo-tt", "env", new Dictionary<string, string>()));

        var act = () => Sut().ReadPasswordsAsync("sub", "rg", "demo", Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("demo-tt");
        _gateway.Verify(g => g.GetContainerAppSecretsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadPasswords_Should_Fail_When_The_App_Was_Deleted_Meanwhile()
    {
        _apps.Add(new ContainerAppInfo("demo-tt", "env", ManagedTags.For("demo")));
        _gateway.Setup(g => g.GetContainerAppSecretsAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string>?)null);

        var act = () => Sut().ReadPasswordsAsync("sub", "rg", "demo", Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
