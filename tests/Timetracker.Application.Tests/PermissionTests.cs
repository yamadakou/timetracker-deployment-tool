using FluentAssertions;
using Moq;
using Timetracker.Application.Deployment;
using Timetracker.Application.Permissions;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;
using Xunit;

namespace Timetracker.Application.Tests;

public class PermissionCheckerTests
{
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TargetResource DbApp = TargetResource.ContainerApp("demo-db");

    private void RgPermissions(params PermissionSet[] permissions) =>
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(permissions);

    private void ResourcePermissions(TargetResource resource, params PermissionSet[] permissions) =>
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", resource.Type, resource.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(permissions);

    private Task<PermissionCheckResult> Check(params PermissionRequirement[] requirements) =>
        new PermissionChecker(_gateway.Object).CheckAsync("sub", "rg", requirements, Ct);

    [Fact]
    public async Task Should_Be_Ok_When_All_Actions_Are_Allowed()
    {
        RgPermissions(new PermissionSet(["*"], []));

        var result = await Check(new PermissionRequirement(AzurePermissions.ContainerAppDelete, DbApp));

        result.Should().Be(PermissionCheckResult.Ok);
        // リソースグループで許可されていれば、リソースでの権限は取得しない
        _gateway.Verify(g => g.GetResourcePermissionsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_List_Missing_Actions_Once_In_Order()
    {
        RgPermissions(new PermissionSet(["*/read"], []));

        var result = await Check(
            new PermissionRequirement(AzurePermissions.ContainerAppRead),
            new PermissionRequirement(AzurePermissions.ContainerAppDelete),
            new PermissionRequirement(AzurePermissions.EnvironmentDelete),
            new PermissionRequirement(AzurePermissions.ContainerAppDelete));

        result.Status.Should().Be(PermissionCheckStatus.Missing);
        result.MissingActions.Should().Equal(AzurePermissions.ContainerAppDelete, AzurePermissions.EnvironmentDelete);
    }

    [Fact]
    public async Task Should_Allow_Actions_Granted_On_The_Existing_Resource()
    {
        // リソースグループでは Reader、既存の DB アプリには Contributor が割り当てられている
        RgPermissions(new PermissionSet(["*/read"], []));
        ResourcePermissions(DbApp, new PermissionSet(["*"], ["Microsoft.Authorization/*/Write"]));
        var redisApp = TargetResource.ContainerApp("demo-redis");
        ResourcePermissions(redisApp, new PermissionSet(["*/read"], []));

        var result = await Check(
            new PermissionRequirement(AzurePermissions.ContainerAppRead, DbApp),
            new PermissionRequirement(AzurePermissions.ContainerAppWrite, DbApp),
            new PermissionRequirement(AzurePermissions.ContainerAppDelete, DbApp),
            new PermissionRequirement(AzurePermissions.ContainerAppDelete, redisApp));

        result.MissingActions.Should().Equal(AzurePermissions.ContainerAppDelete);
        // 同じリソースの権限は 1 回だけ取得する
        _gateway.Verify(g => g.GetResourcePermissionsAsync("sub", "rg", "containerApps", "demo-db", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_Be_NotChecked_When_Permissions_Cannot_Be_Read()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("forbidden"));

        var result = await Check(new PermissionRequirement(AzurePermissions.ContainerAppDelete));

        result.Status.Should().Be(PermissionCheckStatus.NotChecked);
        result.Note.Should().Contain("forbidden");
    }

    [Fact]
    public async Task Should_Be_NotChecked_When_Resource_Permissions_Cannot_Be_Read()
    {
        RgPermissions(new PermissionSet(["*/read"], []));
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", "containerApps", "demo-db", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("not found"));

        var result = await Check(new PermissionRequirement(AzurePermissions.ContainerAppDelete, DbApp));

        result.Status.Should().Be(PermissionCheckStatus.NotChecked);
        result.Note.Should().Contain("not found");
    }

    [Fact]
    public async Task Should_Not_Swallow_Cancellation()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Check(new PermissionRequirement(AzurePermissions.ContainerAppDelete));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Should_Not_Swallow_Cancellation_While_Reading_Resource_Permissions()
    {
        RgPermissions(new PermissionSet(["*/read"], []));
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", "containerApps", "demo-db", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Check(new PermissionRequirement(AzurePermissions.ContainerAppDelete, DbApp));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public class DeploymentPlannerTests
{
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();

    private static readonly DeployOptions Opts = new()
    {
        Subscription = "sub", ResourceGroup = "rg", AppName = "demo", DbPassword = "x", TrackerPassword = "x",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeploymentPlannerTests()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["*/read"], [])]);
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private void ResourceGroupExists() =>
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));

    [Fact]
    public async Task Should_Not_Check_When_Resource_Group_Does_Not_Exist()
    {
        var result = await new DeploymentPlanner(_gateway.Object).CheckPermissionsAsync(Opts, Ct);

        result.Status.Should().Be(PermissionCheckStatus.NotChecked);
        result.Note.Should().Contain("rg");
        _gateway.Verify(g => g.GetPermissionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_Require_Environment_Write_When_Environment_Does_Not_Exist()
    {
        ResourceGroupExists();

        var result = await new DeploymentPlanner(_gateway.Object).CheckPermissionsAsync(Opts, Ct);

        result.MissingActions.Should().Equal(
            AzurePermissions.ContainerAppWrite, AzurePermissions.EnvironmentJoin, AzurePermissions.EnvironmentWrite);
    }

    [Fact]
    public async Task Should_Not_Require_Environment_Write_When_Environment_Exists()
    {
        ResourceGroupExists();
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo("id", "demo-env", "japaneast", new Dictionary<string, string>()));

        var result = await new DeploymentPlanner(_gateway.Object).CheckPermissionsAsync(Opts, Ct);

        result.MissingActions.Should().Equal(AzurePermissions.ContainerAppWrite, AzurePermissions.EnvironmentJoin);
        _gateway.Verify(g => g.GetResourcePermissionsAsync("sub", "rg", "managedEnvironments", "demo-env", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_Allow_Updates_Granted_On_Existing_Resources()
    {
        // リソースグループでは Reader、既存の Container App と Environment には Contributor が割り当てられている
        ResourceGroupExists();
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo("id", "demo-env", "japaneast", new Dictionary<string, string>()));
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ContainerAppInfo("demo-db", "id", new Dictionary<string, string>()),
            new ContainerAppInfo("DEMO-REDIS", "id", new Dictionary<string, string>()),
            new ContainerAppInfo("demo-tt", "id", new Dictionary<string, string>()),
        ]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["*"], [])]);

        var result = await new DeploymentPlanner(_gateway.Object).CheckPermissionsAsync(Opts, Ct);

        result.Should().Be(PermissionCheckResult.Ok);
    }

    [Fact]
    public async Task Should_Require_Resource_Group_Permissions_For_Apps_To_Be_Created()
    {
        // DB アプリだけが既存。作成する Redis / Timetracker は、リソースでの権限では判定しない
        ResourceGroupExists();
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo("id", "demo-env", "japaneast", new Dictionary<string, string>()));
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ContainerAppInfo("demo-db", "id", new Dictionary<string, string>())]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["*"], [])]);

        var result = await new DeploymentPlanner(_gateway.Object).CheckPermissionsAsync(Opts, Ct);

        result.MissingActions.Should().Equal(AzurePermissions.ContainerAppWrite);
        _gateway.Verify(g => g.GetResourcePermissionsAsync("sub", "rg", "containerApps", "demo-db", It.IsAny<CancellationToken>()), Times.Once);
        _gateway.Verify(g => g.GetResourcePermissionsAsync("sub", "rg", "containerApps", It.IsIn("demo-redis", "demo-tt"), It.IsAny<CancellationToken>()), Times.Never);
    }
}
