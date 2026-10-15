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

    [Fact]
    public async Task Should_Be_Ok_When_All_Actions_Are_Allowed()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["*"], [])]);

        var result = await new PermissionChecker(_gateway.Object).CheckAsync("sub", "rg", [AzurePermissions.ContainerAppDelete], Ct);

        result.Should().Be(PermissionCheckResult.Ok);
    }

    [Fact]
    public async Task Should_List_Missing_Actions()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["*/read"], [])]);

        var result = await new PermissionChecker(_gateway.Object).CheckAsync(
            "sub", "rg", [AzurePermissions.ContainerAppRead, AzurePermissions.ContainerAppDelete], Ct);

        result.Status.Should().Be(PermissionCheckStatus.Missing);
        result.MissingActions.Should().Equal(AzurePermissions.ContainerAppDelete);
    }

    [Fact]
    public async Task Should_Be_NotChecked_When_Permissions_Cannot_Be_Read()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("forbidden"));

        var result = await new PermissionChecker(_gateway.Object).CheckAsync("sub", "rg", [AzurePermissions.ContainerAppDelete], Ct);

        result.Status.Should().Be(PermissionCheckStatus.NotChecked);
        result.Note.Should().Contain("forbidden");
    }

    [Fact]
    public async Task Should_Not_Swallow_Cancellation()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => new PermissionChecker(_gateway.Object).CheckAsync("sub", "rg", [AzurePermissions.ContainerAppDelete], Ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public class DeploymentPlannerTests
{
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<IReadOnlyCollection<string>> _checkedActions = [];

    private static readonly DeployOptions Opts = new()
    {
        Subscription = "sub", ResourceGroup = "rg", AppName = "demo", DbPassword = "x", TrackerPassword = "x",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeploymentPlannerTests()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["*/read"], [])]);
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
    }
}
