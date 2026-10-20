using System.ClientModel.Primitives;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Mocking;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Resources;
using FluentAssertions;
using Moq;
using TTDeployTool.Common;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Infrastructure.Azure.Tests;

public class AzureSdkExecutorTests
{
    private const string SubscriptionId = "00000000-0000-0000-0000-000000000000";
    private const string RgName = "rg-test";

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

    private static (Mock<ArmClient> arm, Mock<ResourceGroupCollection> rgs) SetupResourceGroups()
    {
        var arm = new Mock<ArmClient>();
        var sub = new Mock<SubscriptionResource>();
        var rgs = new Mock<ResourceGroupCollection>();
        arm.Setup(a => a.GetSubscriptionResource(It.IsAny<ResourceIdentifier>())).Returns(sub.Object);
        sub.Setup(s => s.GetResourceGroups()).Returns(rgs.Object);
        return (arm, rgs);
    }

    [Fact]
    public async Task EnsureResourceGroup_Should_Not_Overwrite_Existing_ResourceGroup()
    {
        var (arm, rgs) = SetupResourceGroups();
        var existing = new Mock<ResourceGroupResource>();
        existing.SetupGet(r => r.HasData).Returns(true);
        existing.SetupGet(r => r.Data).Returns(
            ResourceManagerModelFactory.ResourceGroupData(location: AzureLocation.EastUS, tags: new Dictionary<string, string> { ["owner"] = "x" }));
        rgs.Setup(c => c.GetIfExistsAsync(RgName, It.IsAny<CancellationToken>())).ReturnsAsync(Found(existing.Object));

        var sut = new AzureSdkExecutor(new SimpleLogger(false), arm.Object);
        await sut.EnsureResourceGroupAsync(SubscriptionId, RgName, "japaneast", TestContext.Current.CancellationToken);

        rgs.Verify(c => c.CreateOrUpdateAsync(It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<ResourceGroupData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureResourceGroup_Should_Create_When_Not_Exists()
    {
        var (arm, rgs) = SetupResourceGroups();
        rgs.Setup(c => c.GetIfExistsAsync(RgName, It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ResourceGroupResource>());
        rgs.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, RgName, It.IsAny<ResourceGroupData>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(Mock.Of<ArmOperation<ResourceGroupResource>>());

        var sut = new AzureSdkExecutor(new SimpleLogger(false), arm.Object);
        await sut.EnsureResourceGroupAsync(SubscriptionId, RgName, "japaneast", TestContext.Current.CancellationToken);

        rgs.Verify(c => c.CreateOrUpdateAsync(WaitUntil.Completed, RgName,
            It.Is<ResourceGroupData>(d => d.Location == new AzureLocation("japaneast") && d.Tags["managed-by"] == "ttdeploytool" && d.Tags.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (Mock<ArmClient> arm, Mock<ContainerAppManagedEnvironmentCollection> envs) SetupEnvironments()
    {
        var arm = new Mock<ArmClient>();
        var rg = new Mock<ResourceGroupResource>();
        var mockable = new Mock<MockableAppContainersResourceGroupResource>();
        var envs = new Mock<ContainerAppManagedEnvironmentCollection>();
        arm.Setup(a => a.GetResourceGroupResource(It.IsAny<ResourceIdentifier>())).Returns(rg.Object);
        rg.Setup(r => r.GetCachedClient(It.IsAny<Func<ArmClient, MockableAppContainersResourceGroupResource>>())).Returns(mockable.Object);
        mockable.Setup(m => m.GetContainerAppManagedEnvironments()).Returns(envs.Object);
        return (arm, envs);
    }

    private static Mock<ContainerAppManagedEnvironmentResource> ExistingEnvironment(string location)
    {
        var env = new Mock<ContainerAppManagedEnvironmentResource>();
        env.SetupGet(e => e.HasData).Returns(true);
        env.SetupGet(e => e.Data).Returns(new ContainerAppManagedEnvironmentData(new AzureLocation(location)));
        return env;
    }

    [Fact]
    public async Task EnsureContainerAppEnv_Should_Fail_When_Existing_Environment_Is_In_Other_Region()
    {
        // Container App は Environment と同じリージョンに作成する必要があるため、異なる場合は明確なエラーで止める
        var (arm, envs) = SetupEnvironments();
        envs.Setup(c => c.GetIfExistsAsync("app-env", It.IsAny<CancellationToken>())).ReturnsAsync(Found(ExistingEnvironment("eastus").Object));

        var sut = new AzureSdkExecutor(new SimpleLogger(false), arm.Object);
        var act = () => sut.EnsureContainerAppEnvAsync(SubscriptionId, RgName, "app", "japaneast", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*app-env*eastus*japaneast*");
    }

    [Fact]
    public async Task EnsureContainerAppEnv_Should_Reuse_Existing_Environment()
    {
        var (arm, envs) = SetupEnvironments();
        var existing = ExistingEnvironment("japaneast");
        envs.Setup(c => c.GetIfExistsAsync("app-env", It.IsAny<CancellationToken>())).ReturnsAsync(Found(existing.Object));

        var sut = new AzureSdkExecutor(new SimpleLogger(false), arm.Object);
        var result = await sut.EnsureContainerAppEnvAsync(SubscriptionId, RgName, "app", "JapanEast", TestContext.Current.CancellationToken);

        result.Should().BeSameAs(existing.Object);
        envs.Verify(c => c.CreateOrUpdateAsync(It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<ContainerAppManagedEnvironmentData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureContainerAppEnv_Should_Create_When_Not_Exists()
    {
        var (arm, envs) = SetupEnvironments();
        var created = new Mock<ContainerAppManagedEnvironmentResource>();
        var op = new Mock<ArmOperation<ContainerAppManagedEnvironmentResource>>();
        op.SetupGet(o => o.Value).Returns(created.Object);
        envs.Setup(c => c.GetIfExistsAsync("app-env", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppManagedEnvironmentResource>());
        envs.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, "app-env", It.IsAny<ContainerAppManagedEnvironmentData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(op.Object);

        var sut = new AzureSdkExecutor(new SimpleLogger(false), arm.Object);
        var result = await sut.EnsureContainerAppEnvAsync(SubscriptionId, RgName, "app", "japaneast", TestContext.Current.CancellationToken);

        result.Should().BeSameAs(created.Object);
    }

    private static (Mock<ArmClient> arm, Mock<ContainerAppCollection> apps) SetupContainerApps()
    {
        var arm = new Mock<ArmClient>();
        var rg = new Mock<ResourceGroupResource>();
        var mockable = new Mock<MockableAppContainersResourceGroupResource>();
        var apps = new Mock<ContainerAppCollection>();
        arm.Setup(a => a.GetResourceGroupResource(It.IsAny<ResourceIdentifier>())).Returns(rg.Object);
        rg.Setup(r => r.GetCachedClient(It.IsAny<Func<ArmClient, MockableAppContainersResourceGroupResource>>())).Returns(mockable.Object);
        mockable.Setup(m => m.GetContainerApps()).Returns(apps.Object);
        return (arm, apps);
    }

    [Fact]
    public async Task WarnIfDbAppExists_Should_Warn_About_Data_Loss_When_Db_App_Exists()
    {
        var (arm, apps) = SetupContainerApps();
        apps.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<CancellationToken>())).ReturnsAsync(Found(new Mock<ContainerAppResource>().Object));
        var output = new StringWriter();

        var sut = new AzureSdkExecutor(new SimpleLogger(false, output), arm.Object);
        var exists = await sut.WarnIfDbAppExistsAsync(SubscriptionId, RgName, "app-db", TestContext.Current.CancellationToken);

        exists.Should().BeTrue();
        output.ToString().Should().Contain("[WARN]").And.Contain("データ");
    }

    [Fact]
    public async Task WarnIfDbAppExists_Should_Not_Warn_When_Db_App_Not_Exists()
    {
        var (arm, apps) = SetupContainerApps();
        apps.Setup(c => c.GetIfExistsAsync("app-db", It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());
        var output = new StringWriter();

        var sut = new AzureSdkExecutor(new SimpleLogger(false, output), arm.Object);
        var exists = await sut.WarnIfDbAppExistsAsync(SubscriptionId, RgName, "app-db", TestContext.Current.CancellationToken);

        exists.Should().BeFalse();
        output.ToString().Should().NotContain("[WARN]");
    }

    // name・fqdn は読み取り専用のため、Azure の応答と同じ JSON から作る
    // （ArmAppContainersModelFactory.ContainerAppIngressConfiguration は stickySessionsAffinity 省略時に例外になる）
    private static ContainerAppResource CreatedApp(string name, string? fqdn = null, bool withConfiguration = true)
    {
        var ingress = fqdn == null ? "" : $$""", "ingress": { "fqdn": "{{fqdn}}" }""";
        var properties = withConfiguration ? $$"""{ "configuration": { "activeRevisionsMode": "Single"{{ingress}} } }""" : "{}";
        var json = $$"""{ "name": "{{name}}", "location": "japaneast", "properties": {{properties}} }""";
        var app = new Mock<ContainerAppResource>();
        app.SetupGet(a => a.HasData).Returns(true);
        app.SetupGet(a => a.Data).Returns(ModelReaderWriter.Read<ContainerAppData>(BinaryData.FromString(json))!);
        return app.Object;
    }

    private static Mock<ContainerAppCollection> SetupCreateOrUpdate(string? ttFqdn, out Mock<ArmClient> arm, bool ttWithConfiguration = true)
    {
        (arm, var apps) = SetupContainerApps();
        foreach (var name in new[] { "app-db", "app-redis", "app-tt" })
        {
            apps.Setup(c => c.GetIfExistsAsync(name, It.IsAny<CancellationToken>())).ReturnsAsync(NotFound<ContainerAppResource>());
        }
        foreach (var (name, fqdn) in new[] { ("app-db", (string?)null), ("app-redis", null), ("app-tt", ttFqdn) })
        {
            var op = new Mock<ArmOperation<ContainerAppResource>>();
            op.SetupGet(o => o.Value).Returns(CreatedApp(name, fqdn, name != "app-tt" || ttWithConfiguration));
            apps.Setup(c => c.CreateOrUpdateAsync(WaitUntil.Completed, name, It.IsAny<ContainerAppData>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(op.Object);
        }
        return apps;
    }

    private static ContainerAppManagedEnvironmentResource Environment()
    {
        var env = new Mock<ContainerAppManagedEnvironmentResource>();
        env.SetupGet(e => e.Id).Returns(new ResourceIdentifier(
            $"/subscriptions/{SubscriptionId}/resourceGroups/{RgName}/providers/Microsoft.App/managedEnvironments/app-env"));
        return env.Object;
    }

    private static DeployOptions Options() => new()
    {
        Subscription = SubscriptionId,
        ResourceGroup = RgName,
        AppName = "app",
        DbPassword = "DbPassw0rd!",
        TrackerPassword = "TrackerPass!",
    };

    [Fact]
    public async Task CreateOrUpdateContainerApp_Should_Return_App_Names_And_Endpoint()
    {
        var apps = SetupCreateOrUpdate("app-tt.example.japaneast.azurecontainerapps.io", out var arm);

        var sut = new AzureSdkExecutor(new SimpleLogger(false, new StringWriter()), arm.Object);
        var result = await sut.CreateOrUpdateContainerAppAsync(
            SubscriptionId, RgName, "app", "japaneast", Options(), Environment(), TestContext.Current.CancellationToken);

        result.Should().Be(new ContainerAppsDeployment(
            "app-tt", "app-db", "app-redis", "https://app-tt.example.japaneast.azurecontainerapps.io"));
        apps.Verify(c => c.CreateOrUpdateAsync(WaitUntil.Completed, It.IsAny<string>(), It.IsAny<ContainerAppData>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task CreateOrUpdateContainerApp_Should_Return_Null_Endpoint_When_Fqdn_Is_Not_Available()
    {
        SetupCreateOrUpdate(ttFqdn: null, out var arm);
        var output = new StringWriter();

        var sut = new AzureSdkExecutor(new SimpleLogger(false, output), arm.Object);
        var result = await sut.CreateOrUpdateContainerAppAsync(
            SubscriptionId, RgName, "app", "japaneast", Options(), Environment(), TestContext.Current.CancellationToken);

        result.TimetrackerUrl.Should().BeNull();
        output.ToString().Should().Contain("[WARN]").And.Contain("FQDN");
    }

    [Fact]
    public async Task CreateOrUpdateContainerApp_Should_Return_Null_Endpoint_When_Configuration_Is_Not_Returned()
    {
        SetupCreateOrUpdate(ttFqdn: null, out var arm, ttWithConfiguration: false);
        var output = new StringWriter();

        var sut = new AzureSdkExecutor(new SimpleLogger(false, output), arm.Object);
        var result = await sut.CreateOrUpdateContainerAppAsync(
            SubscriptionId, RgName, "app", "japaneast", Options(), Environment(), TestContext.Current.CancellationToken);

        result.TimetrackerUrl.Should().BeNull();
        output.ToString().Should().Contain("[WARN]").And.Contain("FQDN");
    }

    [Fact]
    public void Constructor_Should_Accept_Credential()
    {
        // ArmClient の作成だけで、Azure には接続しない
        var act = () => new AzureSdkExecutor(new SimpleLogger(false, new StringWriter()), new Mock<TokenCredential>().Object);

        act.Should().NotThrow();
    }
}
