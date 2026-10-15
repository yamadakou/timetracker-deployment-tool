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
        _gateway.Setup(g => g.GetResourcePermissionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
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
        App("demo-db", new Dictionary<string, string> { ["managed-by"] = "timetracker-deployment-tool" }); // tt-app-name だけが外された
        Environment(NoTags);

        var plan = await Plan(includeUntagged: true);

        Target(plan, "demo-tt").Should().Be(new DeletionTarget("demo-tt", DeletionResourceKind.ContainerApp, true, false, true, null));
        Target(plan, "demo-db").WillDelete.Should().BeTrue();
        Target(plan, "demo-redis").Exists.Should().BeFalse();
        Target(plan, "demo-env").WillDelete.Should().BeTrue();
        plan.HasUnmanaged.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Not_Include_Resources_Managed_By_Others_Even_When_Untagged_Are_Included()
    {
        App("demo-tt", Managed);
        App("demo-db", ManagedTags.For("other"));                                       // 別のアプリ名のタグ
        App("demo-redis", new Dictionary<string, string> { ["managed-by"] = "terraform" }); // 別のツールの管理タグ
        Environment(Managed);

        var plan = await Plan(includeUntagged: true);

        Target(plan, "demo-tt").WillDelete.Should().BeTrue();
        Target(plan, "demo-db").Should().Be(
            new DeletionTarget("demo-db", DeletionResourceKind.ContainerApp, true, false, false, DeletionPlanner.OtherOwnerReason));
        Target(plan, "demo-redis").Reason.Should().Be(DeletionPlanner.OtherOwnerReason);
        Target(plan, "demo-env").WillDelete.Should().BeFalse(); // 削除しないアプリが残る
        // --include-untagged で対象にできるものは無い
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

    [Fact]
    public async Task Should_Allow_Deletes_Granted_On_The_Resources()
    {
        // リソースグループでは Reader、削除する各リソースには Contributor が割り当てられている
        AllManaged();
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*/read"], [])]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["*"], [])]);

        var plan = await Plan();

        plan.PermissionCheck.Should().Be(PermissionCheckResult.Ok);
        _gateway.Verify(g => g.GetResourcePermissionsAsync("sub", "rg", "containerApps", "demo-db", It.IsAny<CancellationToken>()), Times.Once);
        _gateway.Verify(g => g.GetResourcePermissionsAsync("sub", "rg", "managedEnvironments", "demo-env", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_Keep_Include_Untagged_In_The_Plan()
    {
        (await Plan()).IncludeUntagged.Should().BeFalse();
        (await Plan(includeUntagged: true)).IncludeUntagged.Should().BeTrue();
    }
}

public class DeletionRunnerTests
{
    private const string EnvId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/demo-env";

    private readonly Mock<IAzureDeploymentGateway> _gateway = new(MockBehavior.Strict);
    // Azure の現在の状態。削除するとここから消える
    private readonly List<ContainerAppInfo> _apps = [];
    private ManagedEnvironmentInfo? _env;
    private readonly List<string> _deleted = [];

    private static IReadOnlyDictionary<string, string> Managed => ManagedTags.For("demo");
    private static IReadOnlyDictionary<string, string> NoTags => new Dictionary<string, string>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeletionRunnerTests()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps.ToList());
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>())).ReturnsAsync(() => _env);
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string name, CancellationToken _) =>
            {
                _deleted.Add(name);
                return _apps.RemoveAll(a => a.Name == name) > 0;
            });
        _gateway.Setup(g => g.DeleteEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                _deleted.Add("demo-env");
                _env = null;
                return true;
            });
    }

    private void App(string name, IReadOnlyDictionary<string, string> tags, string? envId = EnvId) => _apps.Add(new ContainerAppInfo(name, envId, tags));

    private void AllManaged()
    {
        App("demo-tt", Managed);
        App("demo-redis", Managed);
        App("demo-db", Managed);
        _env = new ManagedEnvironmentInfo(EnvId, "demo-env", "japaneast", Managed);
    }

    private static DeletionTarget Delete(string name, DeletionResourceKind kind, bool managed = true) => new(name, kind, true, managed, true, null);

    private static DeletionPlan Plan(bool includeUntagged, params DeletionTarget[] targets) =>
        new("sub", "rg", "demo", targets, PermissionCheckResult.Ok, includeUntagged);

    private static DeletionPlan AllPlan(bool includeUntagged = false) => Plan(includeUntagged,
        Delete("demo-tt", DeletionResourceKind.ContainerApp),
        Delete("demo-redis", DeletionResourceKind.ContainerApp),
        Delete("demo-db", DeletionResourceKind.ContainerApp),
        Delete("demo-env", DeletionResourceKind.Environment));

    [Fact]
    public async Task Should_Delete_Planned_Targets_In_Order_And_Report_Already_Deleted()
    {
        AllManaged();
        _apps.RemoveAll(a => a.Name == "demo-redis"); // 計画の作成後に削除された
        var reported = new List<string>();

        var results = await new DeletionRunner(_gateway.Object).RunAsync(AllPlan(), new SyncProgress(t => reported.Add(t.Name)), Ct);

        _deleted.Should().Equal("demo-tt", "demo-db", "demo-env");
        reported.Should().Equal("demo-tt", "demo-db", "demo-env");
        results.Should().Equal(
            new DeletionResult("demo-tt", DeletionResourceKind.ContainerApp, true),
            new DeletionResult("demo-redis", DeletionResourceKind.ContainerApp, false),
            new DeletionResult("demo-db", DeletionResourceKind.ContainerApp, true),
            new DeletionResult("demo-env", DeletionResourceKind.Environment, true));
    }

    [Fact]
    public async Task Should_Report_Already_Deleted_When_It_Disappears_Just_Before_Deletion()
    {
        AllManaged();
        _gateway.Setup(g => g.DeleteEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var results = await new DeletionRunner(_gateway.Object).RunAsync(AllPlan(), cancellationToken: Ct);

        results.Last().Should().Be(new DeletionResult("demo-env", DeletionResourceKind.Environment, false));
    }

    [Fact]
    public async Task Should_Skip_Targets_Not_Planned_For_Deletion()
    {
        App("demo-db", NoTags);

        var results = await new DeletionRunner(_gateway.Object).RunAsync(
            Plan(false, new DeletionTarget("demo-db", DeletionResourceKind.ContainerApp, true, false, false, DeletionPlanner.UnmanagedReason)),
            cancellationToken: Ct);

        results.Should().BeEmpty();
        _deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_Stop_When_Tags_Were_Removed_After_Planning()
    {
        AllManaged();
        _apps[2] = _apps[2] with { Tags = NoTags }; // 確認の入力を待つ間に DB アプリの管理タグが外された

        var act = () => new DeletionRunner(_gateway.Object).RunAsync(AllPlan(), cancellationToken: Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*demo-db*{DeletionPlanner.UnmanagedReason}*");
        _deleted.Should().Equal("demo-tt", "demo-redis");
    }

    [Fact]
    public async Task Should_Stop_When_Replaced_By_An_Untagged_Resource_With_The_Same_Name()
    {
        // 計画の作成後に、Timetracker アプリが管理タグの無い同じ名前のアプリに置き換えられた
        App("demo-tt", NoTags);

        var act = () => new DeletionRunner(_gateway.Object).RunAsync(
            Plan(false, Delete("demo-tt", DeletionResourceKind.ContainerApp)), cancellationToken: Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*demo-tt*");
        _deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_Delete_Untagged_Resources_When_The_Plan_Includes_Them()
    {
        App("demo-tt", NoTags);

        var results = await new DeletionRunner(_gateway.Object).RunAsync(
            Plan(true, Delete("demo-tt", DeletionResourceKind.ContainerApp, managed: false)), cancellationToken: Ct);

        results.Should().ContainSingle().Which.Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task Should_Keep_Environment_When_An_App_Was_Added_To_It_During_Deletion()
    {
        AllManaged();
        // DB アプリの削除中に、別のアプリが同じ Environment に作られた
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-db", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                _deleted.Add("demo-db");
                _apps.RemoveAll(a => a.Name == "demo-db");
                App("someone-else", NoTags);
                return true;
            });

        var act = () => new DeletionRunner(_gateway.Object).RunAsync(AllPlan(), cancellationToken: Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*demo-env*someone-else*");
        _deleted.Should().Equal("demo-tt", "demo-redis", "demo-db");
    }

    private sealed class SyncProgress(Action<DeletionTarget> report) : IProgress<DeletionTarget>
    {
        public void Report(DeletionTarget value) => report(value);
    }
}
