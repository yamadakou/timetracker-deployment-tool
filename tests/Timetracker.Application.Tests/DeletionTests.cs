using FluentAssertions;
using Moq;
using Timetracker.Application.Deletion;
using Timetracker.Application.Permissions;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;
using Xunit;

namespace Timetracker.Application.Tests;

public class DeletionPlannerTests
{
    private const string EnvId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/demo-env";
    private const string OtherEnvId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/other-env";

    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyDictionary<string, string> Managed => ManagedTags.For("demo");
    private static IReadOnlyDictionary<string, string> NoTags => new Dictionary<string, string>();

    public DeletionPlannerTests()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps);
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
    }

    private void App(string name, IReadOnlyDictionary<string, string> tags, string? envId = EnvId) => _apps.Add(new ContainerAppInfo(name, envId, tags));

    private void Environment(IReadOnlyDictionary<string, string> tags) =>
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo(EnvId, "demo-env", "japaneast", tags));

    private void AllManaged()
    {
        App("demo-tt", Managed);
        App("demo-redis", Managed);
        App("demo-db", Managed);
        Environment(Managed);
    }

    private Task<DeletionPlan> Plan(bool includeUntagged = false) =>
        new DeletionPlanner(_gateway.Object).PlanAsync("sub", "rg", "demo", includeUntagged, Ct);

    private static DeletionTarget Target(DeletionPlan plan, string name) => plan.Targets.Single(t => t.Name == name);

    [Fact]
    public async Task Should_Delete_All_Managed_Resources_In_Order()
    {
        AllManaged();

        var plan = await Plan();

        plan.Targets.Select(t => (t.Name, t.Kind, t.WillDelete)).Should().Equal(
            ("demo-tt", DeletionResourceKind.ContainerApp, true),
            ("demo-redis", DeletionResourceKind.ContainerApp, true),
            ("demo-db", DeletionResourceKind.ContainerApp, true),
            ("demo-env", DeletionResourceKind.Environment, true));
        plan.HasDeletions.Should().BeTrue();
        plan.DeletesDb.Should().BeTrue();
        plan.HasUnmanaged.Should().BeFalse();
        plan.PermissionCheck.Should().Be(PermissionCheckResult.Ok);
    }

    [Fact]
    public async Task Should_Report_Nothing_When_Resource_Group_Does_Not_Exist()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceGroupInfo?)null);

        var plan = await Plan();

        plan.Targets.Should().HaveCount(4).And.OnlyContain(t => !t.Exists && !t.WillDelete && t.Reason == DeletionPlanner.NotFoundReason);
        plan.HasDeletions.Should().BeFalse();
        _gateway.Verify(g => g.ListContainerAppsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_Skip_Missing_Resources_Without_Checking_Permissions()
    {
        var plan = await Plan();

        plan.Targets.Should().OnlyContain(t => !t.Exists && t.Reason == DeletionPlanner.NotFoundReason);
        plan.PermissionCheck.Should().Be(PermissionCheckResult.Ok);
        _gateway.Verify(g => g.GetPermissionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_Keep_Untagged_App_And_Its_Environment()
    {
        // 同じ名前の別リソース（管理タグなし）は削除しない。それが残るため Environment も削除しない
        App("demo-tt", Managed);
        App("demo-redis", Managed);
        App("demo-db", NoTags);
        Environment(Managed);

        var plan = await Plan();

        Target(plan, "demo-db").Should().Be(new DeletionTarget("demo-db", DeletionResourceKind.ContainerApp, true, false, false, DeletionPlanner.UnmanagedReason));
        Target(plan, "demo-env").WillDelete.Should().BeFalse();
        Target(plan, "demo-env").Reason.Should().Contain("demo-db");
        plan.DeletesDb.Should().BeFalse();
        plan.HasUnmanaged.Should().BeTrue();
        // Environment を削除しないため、Environment の削除権限は確認しない
        _gateway.Verify(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_Include_Untagged_Resources_When_Requested()
    {
        App("demo-tt", NoTags);
        App("demo-db", ManagedTags.For("other")); // 別のアプリ名のタグ
        Environment(NoTags);

        var plan = await Plan(includeUntagged: true);

        Target(plan, "demo-tt").Should().Be(new DeletionTarget("demo-tt", DeletionResourceKind.ContainerApp, true, false, true, null));
        Target(plan, "demo-db").WillDelete.Should().BeTrue();
        Target(plan, "demo-redis").Exists.Should().BeFalse();
        Target(plan, "demo-env").WillDelete.Should().BeTrue();
        plan.HasUnmanaged.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Keep_Untagged_Environment()
    {
        App("demo-tt", Managed);
        Environment(NoTags);

        var plan = await Plan();

        Target(plan, "demo-tt").WillDelete.Should().BeTrue();
        Target(plan, "demo-env").Reason.Should().Be(DeletionPlanner.UnmanagedReason);
    }

    [Fact]
    public async Task Should_Keep_Environment_When_Other_Apps_Remain_In_It()
    {
        AllManaged();
        App("someone-else", NoTags);              // 同じ Environment の別アプリ
        App("unrelated", NoTags, OtherEnvId);     // 別の Environment のアプリは影響しない
        App("no-environment", NoTags, null);

        var plan = await Plan();

        Target(plan, "demo-env").WillDelete.Should().BeFalse();
        Target(plan, "demo-env").Reason.Should().Contain("someone-else").And.NotContain("unrelated").And.NotContain("no-environment");
        plan.Targets.Where(t => t.Kind == DeletionResourceKind.ContainerApp).Should().OnlyContain(t => t.WillDelete);
    }

    [Fact]
    public async Task Should_Match_Names_And_Environment_Ids_Case_Insensitively()
    {
        _apps.Add(new ContainerAppInfo("DEMO-TT", EnvId.ToUpperInvariant(), Managed));
        Environment(Managed);

        var plan = await Plan();

        Target(plan, "demo-tt").WillDelete.Should().BeTrue();
        Target(plan, "demo-env").WillDelete.Should().BeTrue();
    }

    [Fact]
    public async Task Should_Check_Delete_Permissions_For_Planned_Kinds()
    {
        AllManaged();
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["Microsoft.App/containerApps/*"], [])]);

        var plan = await Plan();

        plan.PermissionCheck.Status.Should().Be(PermissionCheckStatus.Missing);
        plan.PermissionCheck.MissingActions.Should().Equal(AzurePermissions.EnvironmentDelete);
    }

    [Fact]
    public async Task Should_Check_Only_Environment_Delete_When_Only_Environment_Remains()
    {
        Environment(Managed);
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*/read"], [])]);

        var plan = await Plan();

        plan.PermissionCheck.MissingActions.Should().Equal(AzurePermissions.EnvironmentDelete);
    }
}

public class DeletionRunnerTests
{
    private readonly Mock<IAzureDeploymentGateway> _gateway = new(MockBehavior.Strict);

    private static DeletionPlan Plan(params DeletionTarget[] targets) => new("sub", "rg", "demo", targets, PermissionCheckResult.Ok);

    private static DeletionTarget Delete(string name, DeletionResourceKind kind) => new(name, kind, true, true, true, null);

    [Fact]
    public async Task Should_Delete_Planned_Targets_In_Order_And_Report_Already_Deleted()
    {
        var calls = new List<string>();
        using var cts = new CancellationTokenSource();
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", It.IsAny<string>(), cts.Token))
            .Callback<string, string, string, CancellationToken>((_, _, name, _) => calls.Add(name))
            .ReturnsAsync((string _, string _, string name, CancellationToken _) => name != "demo-redis");
        _gateway.Setup(g => g.DeleteEnvironmentAsync("sub", "rg", "demo-env", cts.Token))
            .Callback(() => calls.Add("demo-env")).ReturnsAsync(true);
        var reported = new List<string>();

        var results = await new DeletionRunner(_gateway.Object).RunAsync(
            Plan(
                Delete("demo-tt", DeletionResourceKind.ContainerApp),
                Delete("demo-redis", DeletionResourceKind.ContainerApp),
                new DeletionTarget("demo-db", DeletionResourceKind.ContainerApp, true, false, false, DeletionPlanner.UnmanagedReason),
                Delete("demo-env", DeletionResourceKind.Environment)),
            new SyncProgress(t => reported.Add(t.Name)),
            cts.Token);

        calls.Should().Equal("demo-tt", "demo-redis", "demo-env");
        reported.Should().Equal("demo-tt", "demo-redis", "demo-env");
        results.Should().Equal(
            new DeletionResult("demo-tt", DeletionResourceKind.ContainerApp, true),
            new DeletionResult("demo-redis", DeletionResourceKind.ContainerApp, false), // 計画の作成後に既に削除されていた
            new DeletionResult("demo-env", DeletionResourceKind.Environment, true));
    }

    [Fact]
    public async Task Should_Delete_Without_Progress()
    {
        _gateway.Setup(g => g.DeleteEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var results = await new DeletionRunner(_gateway.Object).RunAsync(
            Plan(Delete("demo-env", DeletionResourceKind.Environment)), cancellationToken: TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task Should_Do_Nothing_When_Nothing_Is_Planned()
    {
        var results = await new DeletionRunner(_gateway.Object).RunAsync(
            Plan(new DeletionTarget("demo-tt", DeletionResourceKind.ContainerApp, false, false, false, DeletionPlanner.NotFoundReason)),
            cancellationToken: TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
    }

    private sealed class SyncProgress(Action<DeletionTarget> report) : IProgress<DeletionTarget>
    {
        public void Report(DeletionTarget value) => report(value);
    }
}
