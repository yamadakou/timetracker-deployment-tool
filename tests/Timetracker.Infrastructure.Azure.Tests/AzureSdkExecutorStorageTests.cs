using System.ClientModel.Primitives;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Mocking;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Mocking;
using Azure.ResourceManager.Storage.Models;
using FluentAssertions;
using Moq;
using Timetracker.Common;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Infrastructure.Azure.Tests;

/// <summary>DB のデータの永続化（ストレージアカウント・ファイル共有・Environment のストレージ、DB アプリの停止と開始）のテスト。</summary>
public class AzureSdkExecutorStorageTests
{
    private const string SubscriptionId = "00000000-0000-0000-0000-000000000000";
    private const string RgName = "rg-test";
    private static readonly string Account = ResourceNaming.StorageAccount(SubscriptionId, RgName, "app");

    private readonly Mock<ArmClient> _arm = new();
    private readonly Mock<ResourceGroupResource> _rg = new();
    private readonly Mock<SubscriptionResource> _sub = new();
    private readonly Mock<MockableStorageSubscriptionResource> _storageSub = new();
    private readonly Mock<StorageAccountCollection> _accounts = new();
    private readonly Mock<ContainerAppCollection> _apps = new();
    private readonly Mock<ContainerAppManagedEnvironmentCollection> _envs = new();
    private readonly StringWriter _log = new();

    public AzureSdkExecutorStorageTests()
    {
        var appContainers = new Mock<MockableAppContainersResourceGroupResource>();
        var storage = new Mock<MockableStorageResourceGroupResource>();
        _arm.Setup(a => a.GetResourceGroupResource(It.IsAny<ResourceIdentifier>())).Returns(_rg.Object);
        _arm.Setup(a => a.GetSubscriptionResource(It.IsAny<ResourceIdentifier>())).Returns(_sub.Object);
        _rg.Setup(r => r.GetCachedClient(It.IsAny<Func<ArmClient, MockableAppContainersResourceGroupResource>>())).Returns(appContainers.Object);
        _rg.Setup(r => r.GetCachedClient(It.IsAny<Func<ArmClient, MockableStorageResourceGroupResource>>())).Returns(storage.Object);
        _sub.Setup(s => s.GetCachedClient(It.IsAny<Func<ArmClient, MockableStorageSubscriptionResource>>())).Returns(_storageSub.Object);
        appContainers.Setup(m => m.GetContainerApps()).Returns(_apps.Object);
        appContainers.Setup(m => m.GetContainerAppManagedEnvironments()).Returns(_envs.Object);
        storage.Setup(m => m.GetStorageAccounts()).Returns(_accounts.Object);
    }

    private AzureSdkExecutor Sut() => new(new SimpleLogger(false, _log, new StringWriter()), _arm.Object);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NullableResponse<T> Found<T>(T value) where T : class
    {
        var r = new Mock<NullableResponse<T>>();
        r.SetupGet(x => x.HasValue).Returns(true);
        r.SetupGet(x => x.Value).Returns(value);
        return r.Object;
    }

    private static NullableResponse<T> NotFound<T>() where T : class
    {
        var r = new Mock<NullableResponse<T>>();
        r.SetupGet(x => x.HasValue).Returns(false);
        return r.Object;
    }

    private static T Json<T>(string json) => ModelReaderWriter.Read<T>(BinaryData.FromString(json))!;

    // ストレージアカウント（ファイル共有の一覧と、キーの一覧を持つ）
    private static (Mock<StorageAccountResource> account, Mock<FileShareCollection> shares) StorageAccount(string keysJson = """{ "keys": [ { "keyName": "key1", "value": "secret-key" } ] }""")
    {
        var account = new Mock<StorageAccountResource>();
        var fileService = new Mock<FileServiceResource>();
        var shares = new Mock<FileShareCollection>();
        account.SetupGet(a => a.HasData).Returns(true);
        account.SetupGet(a => a.Data).Returns(Json<StorageAccountData>(
            $$"""{ "name": "{{Account}}", "location": "japaneast", "tags": { "owner": "alice" }, "properties": {} }"""));
        account.Setup(a => a.GetFileService()).Returns(fileService.Object);
        account.Setup(a => a.GetKeysAsync(It.IsAny<ListKeysRequestExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(Json<StorageAccountListKeysResult>(keysJson), Mock.Of<Response>()));
        fileService.Setup(f => f.GetFileShares()).Returns(shares.Object);
        return (account, shares);
    }

    private static (Mock<ContainerAppManagedEnvironmentResource> env, Mock<ContainerAppManagedEnvironmentStorageCollection> storages) Environment()
    {
        var env = new Mock<ContainerAppManagedEnvironmentResource>();
        var storages = new Mock<ContainerAppManagedEnvironmentStorageCollection>();
        env.Setup(e => e.GetContainerAppManagedEnvironmentStorages()).Returns(storages.Object);
        return (env, storages);
    }

    [Fact]
    public async Task EnsureDbStorage_Should_Create_The_Account_The_Share_And_Register_The_Key()
    {
        var (account, shares) = StorageAccount();
        var (env, storages) = Environment();
        StorageAccountCreateOrUpdateContent? sentAccount = null;
        ContainerAppManagedEnvironmentStorageData? sentStorage = null;
        var accountOp = new Mock<ArmOperation<StorageAccountResource>>();
        accountOp.SetupGet(o => o.Value).Returns(account.Object);
        _accounts.Setup(c => c.GetIfExistsAsync(Account, It.IsAny<StorageAccountExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NotFound<StorageAccountResource>());
        _accounts.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, Account, It.IsAny<StorageAccountCreateOrUpdateContent>(), It.IsAny<CancellationToken>()))
            .Callback<WaitUntil, string, StorageAccountCreateOrUpdateContent, CancellationToken>((_, _, c, _) => sentAccount = c)
            .ReturnsAsync(accountOp.Object);
        shares.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NotFound<FileShareResource>());
        shares.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, "app-db", It.IsAny<FileShareData>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<ArmOperation<FileShareResource>>());
        storages.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, "dbdata", It.IsAny<ContainerAppManagedEnvironmentStorageData>(), It.IsAny<CancellationToken>()))
            .Callback<WaitUntil, string, ContainerAppManagedEnvironmentStorageData, CancellationToken>((_, _, d, _) => sentStorage = d)
            .ReturnsAsync(Mock.Of<ArmOperation<ContainerAppManagedEnvironmentStorageResource>>());

        await Sut().EnsureDbStorageAsync(SubscriptionId, RgName, "app", "japaneast", env.Object, Ct);

        sentAccount!.Tags.Should().BeEquivalentTo(ManagedTags.For("app"));
        shares.Verify(c => c.CreateOrUpdateAsync(WaitUntil.Completed, "app-db",
            It.Is<FileShareData>(d => d.ShareQuota == DbPersistence.ShareQuotaGiB), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        sentStorage!.ManagedEnvironmentStorageAzureFile.Should().BeEquivalentTo(new
        {
            AccountName = Account, AccountKey = "secret-key", ShareName = "app-db", AccessMode = ContainerAppAccessMode.ReadWrite,
        });
        _log.ToString().Should().Contain($"ストレージアカウントを作成: {Account}").And.NotContain("secret-key");
    }

    [Fact]
    public async Task EnsureDbStorage_Should_Reuse_The_Existing_Account_And_Share_But_Register_The_Key_Again()
    {
        var (account, shares) = StorageAccount();
        var (env, storages) = Environment();
        _accounts.Setup(c => c.GetIfExistsAsync(Account, It.IsAny<StorageAccountExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(account.Object));
        shares.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(new Mock<FileShareResource>().Object));
        storages.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, "dbdata", It.IsAny<ContainerAppManagedEnvironmentStorageData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<ArmOperation<ContainerAppManagedEnvironmentStorageResource>>());

        await Sut().EnsureDbStorageAsync(SubscriptionId, RgName, "app", "japaneast", env.Object, Ct);

        // 既存のストレージアカウント・ファイル共有は変更しない
        _accounts.Verify(c => c.CreateOrUpdateAsync(It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<StorageAccountCreateOrUpdateContent>(), It.IsAny<CancellationToken>()), Times.Never);
        shares.Verify(c => c.CreateOrUpdateAsync(It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<FileShareData>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        storages.Verify(c => c.CreateOrUpdateAsync(WaitUntil.Completed, "dbdata", It.IsAny<ContainerAppManagedEnvironmentStorageData>(), It.IsAny<CancellationToken>()), Times.Once);
        _log.ToString().Should().Contain("既存のストレージアカウントを使用").And.Contain("既存のファイル共有を使用");
    }

    [Fact]
    public async Task EnsureDbStorage_Should_Fail_When_No_Key_Is_Returned()
    {
        var (account, shares) = StorageAccount("""{ "keys": [] }""");
        var (env, _) = Environment();
        _accounts.Setup(c => c.GetIfExistsAsync(Account, It.IsAny<StorageAccountExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(account.Object));
        shares.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(new Mock<FileShareResource>().Object));

        var act = () => Sut().EnsureDbStorageAsync(SubscriptionId, RgName, "app", "japaneast", env.Object, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{Account}*アカウントキー*");
    }

    [Fact]
    public async Task GetStorageAccount_Should_Return_Name_Location_And_Tags_Or_Null()
    {
        var (account, _) = StorageAccount();
        _accounts.Setup(c => c.GetIfExistsAsync(Account, It.IsAny<StorageAccountExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(account.Object));
        _accounts.Setup(c => c.GetIfExistsAsync("missing", It.IsAny<StorageAccountExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NotFound<StorageAccountResource>());

        var info = await Sut().GetStorageAccountAsync(SubscriptionId, RgName, Account, Ct);

        info.Should().BeEquivalentTo(new StorageAccountInfo(Account, "japaneast", new Dictionary<string, string> { ["owner"] = "alice" }));
        (await Sut().GetStorageAccountAsync(SubscriptionId, RgName, "missing", Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData("""{ "nameAvailable": true }""", true)]
    [InlineData("""{ "nameAvailable": false, "reason": "AlreadyExists" }""", false)]
    [InlineData("{}", false)]
    public async Task IsStorageAccountNameAvailable_Should_Return_The_Result(string json, bool expected)
    {
        _storageSub.Setup(s => s.CheckStorageAccountNameAvailabilityAsync(
                It.Is<StorageAccountNameAvailabilityContent>(c => c.Name == Account), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(Json<StorageAccountNameAvailabilityResult>(json), Mock.Of<Response>()));

        (await Sut().IsStorageAccountNameAvailableAsync(SubscriptionId, Account, Ct)).Should().Be(expected);
    }

    [Fact]
    public async Task FileShareExists_Should_Be_False_When_The_Account_Or_The_Share_Is_Missing()
    {
        var (account, shares) = StorageAccount();
        _accounts.Setup(c => c.GetIfExistsAsync(Account, It.IsAny<StorageAccountExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(account.Object));
        _accounts.Setup(c => c.GetIfExistsAsync("missing", It.IsAny<StorageAccountExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NotFound<StorageAccountResource>());
        shares.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(new Mock<FileShareResource>().Object));
        shares.Setup(c => c.GetIfExistsAsync("other", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NotFound<FileShareResource>());

        (await Sut().FileShareExistsAsync(SubscriptionId, RgName, Account, "app-db", Ct)).Should().BeTrue();
        (await Sut().FileShareExistsAsync(SubscriptionId, RgName, Account, "other", Ct)).Should().BeFalse();
        (await Sut().FileShareExistsAsync(SubscriptionId, RgName, "missing", "app-db", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task EnvironmentStorageExists_Should_Be_False_When_The_Environment_Or_The_Storage_Is_Missing()
    {
        var (env, storages) = Environment();
        _envs.Setup(c => c.GetIfExistsAsync("app-env", It.IsAny<CancellationToken>())).ReturnsAsync(Found(env.Object));
        _envs.Setup(c => c.GetIfExistsAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppManagedEnvironmentResource>());
        storages.Setup(c => c.GetIfExistsAsync("dbdata", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(new Mock<ContainerAppManagedEnvironmentStorageResource>().Object));
        storages.Setup(c => c.GetIfExistsAsync("other", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NotFound<ContainerAppManagedEnvironmentStorageResource>());

        (await Sut().EnvironmentStorageExistsAsync(SubscriptionId, RgName, "app-env", "dbdata", Ct)).Should().BeTrue();
        (await Sut().EnvironmentStorageExistsAsync(SubscriptionId, RgName, "app-env", "other", Ct)).Should().BeFalse();
        (await Sut().EnvironmentStorageExistsAsync(SubscriptionId, RgName, "missing", "dbdata", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteStorageAccount_Should_Delete_Existing_Account_Only()
    {
        var (account, _) = StorageAccount();
        account.Setup(a => a.DeleteAsync(WaitUntil.Completed, It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<ArmOperation>());
        _accounts.Setup(c => c.GetIfExistsAsync(Account, It.IsAny<StorageAccountExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(account.Object));
        _accounts.Setup(c => c.GetIfExistsAsync("missing", It.IsAny<StorageAccountExpand?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NotFound<StorageAccountResource>());

        (await Sut().DeleteStorageAccountAsync(SubscriptionId, RgName, Account, Ct)).Should().BeTrue();
        (await Sut().DeleteStorageAccountAsync(SubscriptionId, RgName, "missing", Ct)).Should().BeFalse();
        account.Verify(a => a.DeleteAsync(WaitUntil.Completed, It.IsAny<CancellationToken>()), Times.Once);
    }

    // DB アプリ（既存なら Azure Files を使っているかを指定）。Redis・Timetracker は新規
    private (Mock<ContainerAppResource>? existingDb, Mock<ContainerAppResource> updatedDb, List<string> calls) SetupDbApp(bool? existingPersisted)
    {
        var calls = new List<string>();
        Mock<ContainerAppResource> App(string name, bool persisted)
        {
            var volumes = persisted ? """, "template": { "volumes": [ { "name": "dbdata", "storageType": "AzureFile", "storageName": "dbdata" } ] }""" : "";
            var app = new Mock<ContainerAppResource>();
            app.SetupGet(a => a.HasData).Returns(true);
            app.SetupGet(a => a.Data).Returns(Json<ContainerAppData>(
                $$"""{ "name": "{{name}}", "location": "japaneast", "properties": { "configuration": {}{{volumes}} } }"""));
            return app;
        }

        Mock<ContainerAppResource>? existing = null;
        if (existingPersisted is { } persisted)
        {
            existing = App("app-db", persisted);
            existing.Setup(a => a.StopAsync(WaitUntil.Completed, It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("stop")).ReturnsAsync(Mock.Of<ArmOperation<ContainerAppResource>>());
            _apps.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<CancellationToken>())).ReturnsAsync(Found(existing.Object));
        }
        else
        {
            _apps.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());
        }
        var updated = App("app-db", true);
        updated.Setup(a => a.StartAsync(WaitUntil.Completed, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("start")).ReturnsAsync(Mock.Of<ArmOperation<ContainerAppResource>>());
        foreach (var name in new[] { "app-db", "app-redis", "app-tt" })
        {
            if (name != "app-db")
                _apps.Setup(c => c.GetIfExistsAsync(name, It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());
            var op = new Mock<ArmOperation<ContainerAppResource>>();
            op.SetupGet(o => o.Value).Returns(name == "app-db" ? updated.Object : App(name, false).Object);
            _apps.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, name, It.IsAny<ContainerAppData>(), It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("put " + name))
                .ReturnsAsync(op.Object);
        }
        return (existing, updated, calls);
    }

    private Task Deploy(bool? persistDb) => Sut().CreateOrUpdateContainerAppAsync(
        SubscriptionId, RgName, "app", "japaneast",
        new DeployOptions { Subscription = SubscriptionId, ResourceGroup = RgName, AppName = "app", DbPassword = "x", TrackerPassword = "x", PersistDb = persistDb },
        new Mock<ContainerAppManagedEnvironmentResource>().Object, Ct);

    [Fact]
    public async Task CreateOrUpdate_Should_Stop_Update_And_Start_A_Persisted_Db_App()
    {
        // 新旧の DB コンテナが同じデータを同時に開かないように
        var (_, _, calls) = SetupDbApp(existingPersisted: true);

        await Deploy(true);

        calls.Should().Equal("stop", "put app-db", "start", "put app-redis", "put app-tt");
        _log.ToString().Should().Contain(DbPersistence.RestartNotice("app-db")).And.NotContain("[WARN] DB アプリ");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task CreateOrUpdate_Should_Warn_Without_Stopping_When_The_Data_Is_Not_Kept(bool existingPersisted, bool persistDb)
    {
        var (existing, updated, calls) = SetupDbApp(existingPersisted);

        await Deploy(persistDb);

        calls.Should().Equal("put app-db", "put app-redis", "put app-tt");
        _log.ToString().Should().Contain("[WARN] " + DbPersistence.DataLossWarning("app-db", existingPersisted, persistDb));
        existing!.Verify(a => a.StopAsync(It.IsAny<WaitUntil>(), It.IsAny<CancellationToken>()), Times.Never);
        updated.Verify(a => a.StartAsync(It.IsAny<WaitUntil>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrUpdate_Should_Treat_An_App_Without_A_Template_As_Not_Persisted_And_Keep_Tags_Of_Other_Apps()
    {
        // 応答に template が無い既存の DB アプリ（永続化していない扱い）と、既存の Redis アプリ（タグを引き継ぐ）
        var (_, _, calls) = SetupDbApp(existingPersisted: null);
        var db = new Mock<ContainerAppResource>();
        db.SetupGet(a => a.Data).Returns(new ContainerAppData(AzureLocation.JapanEast));
        _apps.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<CancellationToken>())).ReturnsAsync(Found(db.Object));
        var redis = new Mock<ContainerAppResource>();
        redis.SetupGet(a => a.Data).Returns(Json<ContainerAppData>("""{ "location": "japaneast", "tags": { "owner": "alice" }, "properties": {} }"""));
        _apps.Setup(c => c.GetIfExistsAsync("app-redis", It.IsAny<CancellationToken>())).ReturnsAsync(Found(redis.Object));
        ContainerAppData? sentRedis = null;
        _apps.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, "app-redis", It.IsAny<ContainerAppData>(), It.IsAny<CancellationToken>()))
            .Callback<WaitUntil, string, ContainerAppData, CancellationToken>((_, _, d, _) => sentRedis = d)
            .ReturnsAsync(Mock.Of<ArmOperation<ContainerAppResource>>(o => o.Value == redis.Object));

        await Deploy(true);

        calls.Should().Equal("put app-db", "put app-tt");
        _log.ToString().Should().Contain("[WARN] " + DbPersistence.DataLossWarning("app-db", false, true));
        sentRedis!.Tags.Should().ContainKey("owner");
        db.Verify(a => a.StopAsync(It.IsAny<WaitUntil>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrUpdate_Should_Not_Stop_Or_Warn_For_A_New_Db_App()
    {
        var (_, _, calls) = SetupDbApp(existingPersisted: null);

        await Deploy(true);

        calls.Should().Equal("put app-db", "put app-redis", "put app-tt");
        _log.ToString().Should().NotContain("[WARN] DB アプリ");
    }
}
