using System.ClientModel.Primitives;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Mocking;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.Authorization.Mocking;
using Azure.ResourceManager.Authorization.Models;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using FluentAssertions;
using Moq;
using TTDeployTool.Common;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Infrastructure.Azure.Tests;

/// <summary>削除・事前確認のための読み取り、削除、権限の取得、デプロイ時の管理タグのテスト。</summary>
public class AzureSdkExecutorInventoryTests
{
    private const string SubscriptionId = "00000000-0000-0000-0000-000000000000";
    private const string RgName = "rg-test";
    private const string EnvId = $"/subscriptions/{SubscriptionId}/resourceGroups/{RgName}/providers/Microsoft.App/managedEnvironments/app-env";

    private readonly Mock<ArmClient> _arm = new();
    private readonly Mock<ResourceGroupResource> _rg = new();
    private readonly Mock<ContainerAppCollection> _apps = new();
    private readonly Mock<ContainerAppManagedEnvironmentCollection> _envs = new();
    private readonly Mock<MockableAuthorizationResourceGroupResource> _authorization = new();
    private readonly StringWriter _log = new();

    public AzureSdkExecutorInventoryTests()
    {
        var mockable = new Mock<MockableAppContainersResourceGroupResource>();
        _arm.Setup(a => a.GetResourceGroupResource(It.IsAny<ResourceIdentifier>())).Returns(_rg.Object);
        _rg.Setup(r => r.GetCachedClient(It.IsAny<Func<ArmClient, MockableAppContainersResourceGroupResource>>())).Returns(mockable.Object);
        _rg.Setup(r => r.GetCachedClient(It.IsAny<Func<ArmClient, MockableAuthorizationResourceGroupResource>>())).Returns(_authorization.Object);
        mockable.Setup(m => m.GetContainerApps()).Returns(_apps.Object);
        mockable.Setup(m => m.GetContainerAppManagedEnvironments()).Returns(_envs.Object);
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

    private static AsyncPageable<T> Pageable<T>(params T[] values) where T : notnull =>
        AsyncPageable<T>.FromPages([Page<T>.FromValues(values, null, Mock.Of<Response>())]);

    // 読み取り専用のプロパティ（name・tags・environmentId）を含むため、Azure の応答と同じ JSON から作る
    private static Mock<ContainerAppResource> App(string name, string properties = "{}", string tags = "{}")
    {
        var json = $$"""{ "name": "{{name}}", "location": "japaneast", "tags": {{tags}}, "properties": {{properties}} }""";
        var app = new Mock<ContainerAppResource>();
        app.SetupGet(a => a.HasData).Returns(true);
        app.SetupGet(a => a.Data).Returns(ModelReaderWriter.Read<ContainerAppData>(BinaryData.FromString(json))!);
        return app;
    }

    private static Mock<ContainerAppManagedEnvironmentResource> Env(string tags = "{}", string properties = "{}")
    {
        var json = $$"""{ "name": "app-env", "location": "japaneast", "tags": {{tags}}, "properties": {{properties}} }""";
        var env = new Mock<ContainerAppManagedEnvironmentResource>();
        env.SetupGet(e => e.HasData).Returns(true);
        env.SetupGet(e => e.Id).Returns(new ResourceIdentifier(EnvId));
        env.SetupGet(e => e.Data).Returns(ModelReaderWriter.Read<ContainerAppManagedEnvironmentData>(BinaryData.FromString(json))!);
        return env;
    }

    [Fact]
    public async Task GetResourceGroup_Should_Return_Name_And_Location_Or_Null()
    {
        var sub = new Mock<SubscriptionResource>();
        var rgs = new Mock<ResourceGroupCollection>();
        _arm.Setup(a => a.GetSubscriptionResource(It.IsAny<ResourceIdentifier>())).Returns(sub.Object);
        sub.Setup(s => s.GetResourceGroups()).Returns(rgs.Object);
        var existing = new Mock<ResourceGroupResource>();
        existing.SetupGet(r => r.Data).Returns(ResourceManagerModelFactory.ResourceGroupData(
            name: RgName, location: AzureLocation.JapanEast, tags: new Dictionary<string, string> { ["managed-by"] = "ttdeploytool" }));
        rgs.Setup(c => c.GetIfExistsAsync(RgName, It.IsAny<CancellationToken>())).ReturnsAsync(Found(existing.Object));
        rgs.Setup(c => c.GetIfExistsAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ResourceGroupResource>());

        var rg = await Sut().GetResourceGroupAsync(SubscriptionId, RgName, Ct);
        (rg!.Name, rg.Location).Should().Be((RgName, "japaneast"));
        rg.Tags.Should().Equal(new Dictionary<string, string> { ["managed-by"] = "ttdeploytool" });
        rg.CreatedByTool.Should().BeTrue();
        (await Sut().GetResourceGroupAsync(SubscriptionId, "missing", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task ListResources_Should_Return_Names_And_Types()
    {
        var resource = new Mock<GenericResource>();
        resource.SetupGet(r => r.Data).Returns(ResourceManagerModelFactory.GenericResourceData(
            name: "app-env", resourceType: new ResourceType("Microsoft.App/managedEnvironments")));
        _rg.Setup(r => r.GetGenericResourcesAsync(null, null, null, It.IsAny<CancellationToken>())).Returns(Pageable(resource.Object));

        (await Sut().ListResourcesAsync(SubscriptionId, RgName, Ct)).Should().Equal(new AzureResourceInfo("app-env", "Microsoft.App/managedEnvironments"));
    }

    [Fact]
    public async Task DeleteResourceGroup_Should_Delete_Existing_Resource_Group_Only()
    {
        var sub = new Mock<SubscriptionResource>();
        var rgs = new Mock<ResourceGroupCollection>();
        _arm.Setup(a => a.GetSubscriptionResource(It.IsAny<ResourceIdentifier>())).Returns(sub.Object);
        sub.Setup(s => s.GetResourceGroups()).Returns(rgs.Object);
        var existing = new Mock<ResourceGroupResource>();
        existing.Setup(r => r.DeleteAsync(WaitUntil.Completed, null, It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<ArmOperation>());
        rgs.Setup(c => c.GetIfExistsAsync(RgName, It.IsAny<CancellationToken>())).ReturnsAsync(Found(existing.Object));
        rgs.Setup(c => c.GetIfExistsAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ResourceGroupResource>());

        (await Sut().DeleteResourceGroupAsync(SubscriptionId, RgName, Ct)).Should().BeTrue();
        (await Sut().DeleteResourceGroupAsync(SubscriptionId, "missing", Ct)).Should().BeFalse();
        existing.Verify(r => r.DeleteAsync(WaitUntil.Completed, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetEnvironment_Should_Return_Id_Location_And_Tags_Or_Null()
    {
        _envs.Setup(c => c.GetIfExistsAsync("app-env", It.IsAny<CancellationToken>())).ReturnsAsync(Found(Env("""{ "owner": "alice" }""").Object));
        _envs.Setup(c => c.GetIfExistsAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppManagedEnvironmentResource>());

        var env = await Sut().GetEnvironmentAsync(SubscriptionId, RgName, "app-env", Ct);

        env!.Id.Should().Be(EnvId);
        env.Name.Should().Be("app-env");
        env.Location.Should().Be("japaneast");
        env.Tags.Should().Equal(new Dictionary<string, string> { ["owner"] = "alice" });
        env.ProvisioningState.Should().BeNull();
        (await Sut().GetEnvironmentAsync(SubscriptionId, RgName, "missing", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task GetEnvironment_Should_Return_The_Provisioning_State()
    {
        _envs.Setup(c => c.GetIfExistsAsync("app-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(Env(properties: """{ "provisioningState": "Succeeded" }""").Object));

        (await Sut().GetEnvironmentAsync(SubscriptionId, RgName, "app-env", Ct))!.ProvisioningState.Should().Be("Succeeded");
    }

    [Fact]
    public async Task ListContainerAppDetails_Should_Return_State_Image_Resources_And_Fqdn()
    {
        _apps.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>())).Returns(Pageable(
            App("app-tt", """
                {
                  "provisioningState": "Succeeded", "runningStatus": "Running", "latestRevisionName": "app-tt--rev1",
                  "configuration": { "ingress": { "fqdn": "app-tt.example.io" } },
                  "template": { "containers": [ { "name": "timetracker", "image": "densocreate/timetracker:11.0.1-linux-postgres",
                                                  "resources": { "cpu": 1.5, "memory": "3Gi" } } ] }
                }
                """, """{ "owner": "alice" }""").Object,
            // 作成中などで値がまだ無い場合
            App("app-new").Object,
            App("app-db", """{ "configuration": {}, "template": { "containers": [ { "name": "db", "image": "postgres:16" } ] } }""").Object,
            App("app-empty", """{ "template": { "containers": [] } }""").Object));

        var apps = await Sut().ListContainerAppDetailsAsync(SubscriptionId, RgName, Ct);

        apps[0].Should().BeEquivalentTo(new ContainerAppDetails(
            "app-tt", "Succeeded", "Running", "densocreate/timetracker:11.0.1-linux-postgres", 1.5, "3Gi", "app-tt--rev1", "app-tt.example.io",
            new Dictionary<string, string> { ["owner"] = "alice" }, []));
        apps[1].Should().BeEquivalentTo(new ContainerAppDetails("app-new", null, null, null, null, null, null, null, new Dictionary<string, string>(), []));
        (apps[2].Image, apps[2].Cpu, apps[2].Memory, apps[2].Fqdn).Should().Be(("postgres:16", null, null, null));
        apps[3].Image.Should().BeNull();
    }

    [Fact]
    public async Task ListContainerApps_Should_Return_Environment_And_Tags()
    {
        _apps.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>())).Returns(Pageable(
            App("by-environment-id", $$"""{ "environmentId": "{{EnvId}}" }""", """{ "managed-by": "x" }""").Object,
            App("by-managed-environment-id", $$"""{ "managedEnvironmentId": "{{EnvId}}" }""").Object,
            App("no-environment").Object));

        var apps = await Sut().ListContainerAppsAsync(SubscriptionId, RgName, Ct);

        apps.Should().HaveCount(3);
        apps[0].Should().BeEquivalentTo(new ContainerAppInfo("by-environment-id", EnvId, new Dictionary<string, string> { ["managed-by"] = "x" }, []));
        apps[1].EnvironmentId.Should().Be(EnvId); // 古い API の応答では managedEnvironmentId だけが返る
        apps[2].EnvironmentId.Should().BeNull();
    }

    [Fact]
    public async Task ListContainerApps_Should_Return_The_Azure_Files_Storages_Of_The_Volumes()
    {
        const string volumes = """
            { "template": { "volumes": [
                { "name": "dbdata", "storageType": "AzureFile", "storageName": "dbdata" },
                { "name": "tmp", "storageType": "EmptyDir" },
                { "name": "noname", "storageType": "AzureFile" },
                { "name": "notype", "storageName": "other" } ] } }
            """;
        _apps.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>())).Returns(Pageable(App("app-db", volumes).Object, App("app-tt").Object));

        var apps = await Sut().ListContainerAppsAsync(SubscriptionId, RgName, Ct);
        var details = await Sut().ListContainerAppDetailsAsync(SubscriptionId, RgName, Ct);

        apps[0].StorageNames.Should().Equal("dbdata");
        apps[0].UsesDbStorage.Should().BeTrue();
        apps[1].UsesDbStorage.Should().BeFalse();
        details[0].UsesDbStorage.Should().BeTrue();
        details[1].StorageNames.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteContainerApp_Should_Delete_Existing_App_Only()
    {
        var app = App("app-tt");
        app.Setup(a => a.DeleteAsync(WaitUntil.Completed, It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<ArmOperation>());
        _apps.Setup(c => c.GetIfExistsAsync("app-tt", It.IsAny<CancellationToken>())).ReturnsAsync(Found(app.Object));
        _apps.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());

        (await Sut().DeleteContainerAppAsync(SubscriptionId, RgName, "app-tt", Ct)).Should().BeTrue();
        (await Sut().DeleteContainerAppAsync(SubscriptionId, RgName, "app-db", Ct)).Should().BeFalse();
        app.Verify(a => a.DeleteAsync(WaitUntil.Completed, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteEnvironment_Should_Delete_Existing_Environment_Only()
    {
        var env = Env();
        env.Setup(e => e.DeleteAsync(WaitUntil.Completed, It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<ArmOperation>());
        _envs.Setup(c => c.GetIfExistsAsync("app-env", It.IsAny<CancellationToken>())).ReturnsAsync(Found(env.Object));
        _envs.Setup(c => c.GetIfExistsAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppManagedEnvironmentResource>());

        (await Sut().DeleteEnvironmentAsync(SubscriptionId, RgName, "app-env", Ct)).Should().BeTrue();
        (await Sut().DeleteEnvironmentAsync(SubscriptionId, RgName, "missing", Ct)).Should().BeFalse();
        env.Verify(e => e.DeleteAsync(WaitUntil.Completed, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetPermissions_Should_Return_Actions_And_NotActions()
    {
        var permission = new RoleDefinitionPermission();
        permission.Actions.Add("*");
        permission.NotActions.Add("Microsoft.Authorization/*/Write");
        _authorization.Setup(a => a.GetAzurePermissionsForResourceGroupsAsync(It.IsAny<CancellationToken>())).Returns(Pageable(permission));

        var permissions = await Sut().GetPermissionsAsync(SubscriptionId, RgName, Ct);

        permissions.Should().ContainSingle().Which.Should().BeEquivalentTo(new PermissionSet(["*"], ["Microsoft.Authorization/*/Write"]));
    }

    [Fact]
    public async Task GetResourcePermissions_Should_Read_Permissions_On_The_Resource()
    {
        var permission = new RoleDefinitionPermission();
        permission.Actions.Add("Microsoft.App/containerApps/*");
        _authorization.Setup(a => a.GetAzurePermissionsForResourcesAsync("Microsoft.App", "", "containerApps", "app-db", It.IsAny<CancellationToken>()))
            .Returns(Pageable(permission));

        var permissions = await Sut().GetResourcePermissionsAsync(SubscriptionId, RgName, "Microsoft.App", "containerApps", "app-db", Ct);

        permissions.Should().ContainSingle().Which.Should().BeEquivalentTo(new PermissionSet(["Microsoft.App/containerApps/*"], []));
    }

    [Fact]
    public async Task CreateOrUpdateContainerApp_Should_Add_Managed_Tags_And_Keep_Existing_Tags()
    {
        var sent = new Dictionary<string, ContainerAppData>();
        _apps.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(App("app-db", tags: """{ "owner": "alice", "tt-app-name": "old" }""").Object));
        _apps.Setup(c => c.GetIfExistsAsync("app-redis", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());
        _apps.Setup(c => c.GetIfExistsAsync("app-tt", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());
        foreach (var name in new[] { "app-db", "app-redis", "app-tt" })
        {
            var op = new Mock<ArmOperation<ContainerAppResource>>();
            op.SetupGet(o => o.Value).Returns(App(name).Object);
            _apps.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, name, It.IsAny<ContainerAppData>(), It.IsAny<CancellationToken>()))
                .Callback<WaitUntil, string, ContainerAppData, CancellationToken>((_, n, data, _) => sent[n] = data)
                .ReturnsAsync(op.Object);
        }
        var opts = new DeployOptions { Subscription = SubscriptionId, ResourceGroup = RgName, AppName = "app", DbPassword = "x", TrackerPassword = "x" };

        await Sut().CreateOrUpdateContainerAppAsync(SubscriptionId, RgName, "app", "japaneast", opts, Env().Object, Ct);

        // 既存の DB アプリは利用者のタグを残し、管理タグを上書きする
        sent["app-db"].Tags.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["owner"] = "alice", ["managed-by"] = "ttdeploytool", ["tt-app-name"] = "app",
        });
        sent["app-redis"].Tags.Should().BeEquivalentTo(ManagedTags.For("app"));
        sent["app-tt"].Tags.Should().BeEquivalentTo(ManagedTags.For("app"));
        _log.ToString().Should().Contain("[WARN] DB アプリ 'app-db' は既に存在します");
    }

    [Fact]
    public async Task CreateOrUpdateContainerApp_Should_Overwrite_Lifecycle_Tags_On_All_Apps()
    {
        var sent = new Dictionary<string, ContainerAppData>();
        _apps.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(App("app-db", tags: """{ "tt-expires-at": "2026-01-01T00:00:00Z" }""").Object));
        _apps.Setup(c => c.GetIfExistsAsync("app-redis", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());
        _apps.Setup(c => c.GetIfExistsAsync("app-tt", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());
        foreach (var name in new[] { "app-db", "app-redis", "app-tt" })
        {
            var op = new Mock<ArmOperation<ContainerAppResource>>();
            op.SetupGet(o => o.Value).Returns(App(name).Object);
            _apps.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, name, It.IsAny<ContainerAppData>(), It.IsAny<CancellationToken>()))
                .Callback<WaitUntil, string, ContainerAppData, CancellationToken>((_, n, data, _) => sent[n] = data)
                .ReturnsAsync(op.Object);
        }
        var lifecycle = new Dictionary<string, string>
        {
            [DeploymentExpiry.ExpiresAtKey] = "2026-11-23T14:59:59Z", [DeploymentExpiry.DeployedAtKey] = "2026-10-22T00:00:00Z",
        };
        var opts = new DeployOptions
        {
            Subscription = SubscriptionId, ResourceGroup = RgName, AppName = "app", DbPassword = "x", TrackerPassword = "x", LifecycleTags = lifecycle,
        };

        await Sut().CreateOrUpdateContainerAppAsync(SubscriptionId, RgName, "app", "japaneast", opts, Env().Object, Ct);

        foreach (var name in new[] { "app-db", "app-redis", "app-tt" })
            sent[name].Tags.Should().Contain(lifecycle).And.Contain("managed-by", "ttdeploytool");
    }

    [Fact]
    public async Task EnsureContainerAppEnv_Should_Create_Environment_With_Managed_Tags()
    {
        ContainerAppManagedEnvironmentData? sent = null;
        var op = new Mock<ArmOperation<ContainerAppManagedEnvironmentResource>>();
        op.SetupGet(o => o.Value).Returns(Env().Object);
        _envs.Setup(c => c.GetIfExistsAsync("app-env", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppManagedEnvironmentResource>());
        _envs.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, "app-env", It.IsAny<ContainerAppManagedEnvironmentData>(), It.IsAny<CancellationToken>()))
            .Callback<WaitUntil, string, ContainerAppManagedEnvironmentData, CancellationToken>((_, _, data, _) => sent = data)
            .ReturnsAsync(op.Object);

        await Sut().EnsureContainerAppEnvAsync(SubscriptionId, RgName, "app", "japaneast", Ct);

        sent!.Tags.Should().BeEquivalentTo(ManagedTags.For("app"));
    }

    [Fact]
    public async Task GetContainerAppSecrets_Should_Return_Names_And_Values_Or_Null()
    {
        var app = App("app-tt");
        app.Setup(a => a.GetSecretsAsync(It.IsAny<CancellationToken>())).Returns(Pageable(
            ArmAppContainersModelFactory.ContainerAppSecret("db-password", "DbPassw0rd!", null, null),
            ArmAppContainersModelFactory.ContainerAppSecret("app-password", "TrackerPass!", null, null),
            // Key Vault を参照するシークレットは値が返らない
            ArmAppContainersModelFactory.ContainerAppSecret("kv", null, "System", new Uri("https://kv.example/secrets/x"))));
        _apps.Setup(c => c.GetIfExistsAsync("app-tt", It.IsAny<CancellationToken>())).ReturnsAsync(Found(app.Object));
        _apps.Setup(c => c.GetIfExistsAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());

        var secrets = await Sut().GetContainerAppSecretsAsync(SubscriptionId, RgName, "app-tt", Ct);

        secrets.Should().BeEquivalentTo(new Dictionary<string, string> { ["db-password"] = "DbPassw0rd!", ["app-password"] = "TrackerPass!" });
        secrets!["DB-PASSWORD"].Should().Be("DbPassw0rd!");
        (await Sut().GetContainerAppSecretsAsync(SubscriptionId, RgName, "missing", Ct)).Should().BeNull();
        _log.ToString().Should().NotContain("DbPassw0rd!");
    }

    [Fact]
    public async Task UpdateContainerAppTags_Should_Merge_Through_The_Tags_Api()
    {
        var tag = new Mock<TagResource>();
        ResourceIdentifier? id = null;
        TagResourcePatch? sent = null;
        _arm.Setup(a => a.GetTagResource(It.IsAny<ResourceIdentifier>())).Callback<ResourceIdentifier>(i => id = i).Returns(tag.Object);
        tag.Setup(t => t.UpdateAsync(WaitUntil.Completed, It.IsAny<TagResourcePatch>(), It.IsAny<CancellationToken>()))
            .Callback<WaitUntil, TagResourcePatch, CancellationToken>((_, patch, _) => sent = patch)
            .ReturnsAsync(Mock.Of<ArmOperation<TagResource>>());

        await Sut().UpdateContainerAppTagsAsync(SubscriptionId, RgName, "app-tt",
            new Dictionary<string, string> { [DeploymentExpiry.ExpiresAtKey] = "none" }, Ct);

        id!.ToString().Should().Be($"/subscriptions/{SubscriptionId}/resourceGroups/{RgName}/providers/Microsoft.App/containerApps/app-tt/providers/Microsoft.Resources/tags/default");
        sent!.PatchMode.Should().Be(TagPatchMode.Merge);
        sent.TagValues.Should().Equal(new Dictionary<string, string> { [DeploymentExpiry.ExpiresAtKey] = "none" });
        // アプリの PUT は使わない（シークレットの値が消えるため）
        _apps.Verify(c => c.CreateOrUpdateAsync(It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<ContainerAppData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Environment_And_Storage_Tags_Should_Be_Merged_Through_The_Tags_Api()
    {
        var ids = new List<string>();
        var tag = new Mock<TagResource>();
        _arm.Setup(a => a.GetTagResource(It.IsAny<ResourceIdentifier>())).Callback<ResourceIdentifier>(i => ids.Add(i.ToString())).Returns(tag.Object);
        tag.Setup(t => t.UpdateAsync(WaitUntil.Completed, It.Is<TagResourcePatch>(p => p.PatchMode == TagPatchMode.Merge && p.TagValues["tt-expires-at"] == "none"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<ArmOperation<TagResource>>());
        var tags = new Dictionary<string, string> { [DeploymentExpiry.ExpiresAtKey] = "none" };

        await Sut().UpdateEnvironmentTagsAsync(SubscriptionId, RgName, "app-env", tags, Ct);
        await Sut().UpdateStorageAccountTagsAsync(SubscriptionId, RgName, "ttapp12345678", tags, Ct);

        ids.Should().Equal(
            $"/subscriptions/{SubscriptionId}/resourceGroups/{RgName}/providers/Microsoft.App/managedEnvironments/app-env/providers/Microsoft.Resources/tags/default",
            $"/subscriptions/{SubscriptionId}/resourceGroups/{RgName}/providers/Microsoft.Storage/storageAccounts/ttapp12345678/providers/Microsoft.Resources/tags/default");
        tag.Verify(t => t.UpdateAsync(WaitUntil.Completed, It.IsAny<TagResourcePatch>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
