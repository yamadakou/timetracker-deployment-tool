using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Mocking;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Resources;
using FluentAssertions;
using Moq;
using Timetracker.Common;
using Xunit;

namespace Timetracker.Controller.Cli.Tests;

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
        await sut.EnsureResourceGroupAsync(SubscriptionId, RgName, "japaneast");

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
        await sut.EnsureResourceGroupAsync(SubscriptionId, RgName, "japaneast");

        rgs.Verify(c => c.CreateOrUpdateAsync(WaitUntil.Completed, RgName,
            It.Is<ResourceGroupData>(d => d.Location == new AzureLocation("japaneast")), It.IsAny<CancellationToken>()), Times.Once);
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
        var act = () => sut.EnsureContainerAppEnvAsync(SubscriptionId, RgName, "app-env", "japaneast");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*app-env*eastus*japaneast*");
    }

    [Fact]
    public async Task EnsureContainerAppEnv_Should_Reuse_Existing_Environment()
    {
        var (arm, envs) = SetupEnvironments();
        var existing = ExistingEnvironment("japaneast");
        envs.Setup(c => c.GetIfExistsAsync("app-env", It.IsAny<CancellationToken>())).ReturnsAsync(Found(existing.Object));

        var sut = new AzureSdkExecutor(new SimpleLogger(false), arm.Object);
        var result = await sut.EnsureContainerAppEnvAsync(SubscriptionId, RgName, "app-env", "JapanEast");

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
        var result = await sut.EnsureContainerAppEnvAsync(SubscriptionId, RgName, "app-env", "japaneast");

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
        var exists = await sut.WarnIfDbAppExistsAsync(SubscriptionId, RgName, "app-db");

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
        var exists = await sut.WarnIfDbAppExistsAsync(SubscriptionId, RgName, "app-db");

        exists.Should().BeFalse();
        output.ToString().Should().NotContain("[WARN]");
    }
}
