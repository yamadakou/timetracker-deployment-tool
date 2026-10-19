using FluentAssertions;
using Moq;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Application.Tests;

public class DeploymentPlannerPlanTests
{
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];

    private static readonly DeployOptions Opts = new()
    {
        Subscription = "sub", ResourceGroup = "rg", AppName = "demo", DbPassword = "x", TrackerPassword = "x", Location = "japaneast",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyDictionary<string, string> NoTags = new Dictionary<string, string>();

    public DeploymentPlannerPlanTests()
    {
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps);
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private void ResourceGroup(string location = "japaneast") =>
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", location));

    private void Environment(string location = "japaneast") =>
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo("id", "demo-env", location, NoTags));

    private Task<DeploymentPlan> Plan() => new DeploymentPlanner(_gateway.Object).PlanAsync(Opts, Ct);

    private static PlannedOperation Op(PlannedResourceKind kind, string name, PlannedAction action, bool dataLoss = false) => new(kind, name, action, dataLoss);

    [Fact]
    public async Task Should_Create_Everything_When_The_Resource_Group_Does_Not_Exist()
    {
        var plan = await Plan();

        plan.Operations.Should().Equal(
            Op(PlannedResourceKind.ResourceGroup, "rg", PlannedAction.Create),
            Op(PlannedResourceKind.Environment, "demo-env", PlannedAction.Create),
            Op(PlannedResourceKind.ContainerApp, "demo-db", PlannedAction.Create),
            Op(PlannedResourceKind.ContainerApp, "demo-redis", PlannedAction.Create),
            Op(PlannedResourceKind.ContainerApp, "demo-tt", PlannedAction.Create));
        plan.CanRun.Should().BeTrue();
        plan.CreatesEnvironment.Should().BeTrue();
        plan.RequiresDataLossAck.Should().BeFalse();
        plan.PermissionCheck.Status.Should().Be(PermissionCheckStatus.NotChecked);
        plan.Warnings.Should().ContainSingle().Which.Should().Contain("リソースグループ 'rg' がまだ無い");
        _gateway.Verify(g => g.ListContainerAppsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_Update_Existing_Apps_And_Require_Data_Loss_Ack_For_The_Db()
    {
        ResourceGroup("Japan East");
        Environment();
        _apps.Add(new ContainerAppInfo("DEMO-DB", "id", NoTags));
        _apps.Add(new ContainerAppInfo("demo-tt", "id", NoTags));

        var plan = await Plan();

        plan.Operations.Should().Equal(
            Op(PlannedResourceKind.ResourceGroup, "rg", PlannedAction.UseExisting),
            Op(PlannedResourceKind.Environment, "demo-env", PlannedAction.UseExisting),
            Op(PlannedResourceKind.ContainerApp, "demo-db", PlannedAction.Update, dataLoss: true),
            Op(PlannedResourceKind.ContainerApp, "demo-redis", PlannedAction.Create),
            Op(PlannedResourceKind.ContainerApp, "demo-tt", PlannedAction.Update));
        plan.RequiresDataLossAck.Should().BeTrue();
        plan.CreatesEnvironment.Should().BeFalse();
        plan.Warnings.Should().ContainSingle().Which.Should().Contain("DB アプリ 'demo-db' は既に存在します。設定が変わると DB コンテナが再起動し");
        plan.PermissionCheck.Should().Be(PermissionCheckResult.Ok);
        // 指定が無く、既存の DB アプリが永続化していないため、永続化しない計画になる（元の設定は変えない）
        plan.Options.Should().NotBeSameAs(Opts).And.BeEquivalentTo(Opts, o => o.Excluding(x => x.PersistDb));
        plan.PersistDb.Should().BeFalse();
        Opts.PersistDb.Should().BeNull();
    }

    [Fact]
    public async Task Should_Warn_About_A_Different_Resource_Group_Region()
    {
        ResourceGroup("eastus");

        var plan = await Plan();

        plan.CanRun.Should().BeTrue();
        plan.Warnings.Should().ContainSingle().Which.Should().Contain("リージョン (eastus) は location (japaneast) と異なります");
    }

    [Fact]
    public async Task Should_Block_A_Different_Environment_Region()
    {
        ResourceGroup();
        Environment("eastus");

        var plan = await Plan();

        plan.CanRun.Should().BeFalse();
        plan.Blockers.Should().ContainSingle().Which.Should().Contain("'demo-env' のリージョン (eastus) が location (japaneast) と異なります");
    }

    [Fact]
    public async Task Should_Block_Missing_Permissions()
    {
        ResourceGroup();
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*/read"], [])]);

        var plan = await Plan();

        plan.Blockers.Should().ContainSingle().Which.Should().StartWith("権限が不足しています: Microsoft.App/containerApps/write");
        plan.PermissionCheck.Status.Should().Be(PermissionCheckStatus.Missing);
    }

    [Fact]
    public async Task Should_Warn_When_Permissions_Cannot_Be_Checked()
    {
        ResourceGroup();
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("forbidden"));

        var plan = await Plan();

        plan.CanRun.Should().BeTrue();
        plan.Warnings.Should().ContainSingle().Which.Should().Contain("権限を事前に確認できませんでした").And.Contain("forbidden");
    }

    [Fact]
    public async Task HasSameOperations_Should_Compare_The_Operations()
    {
        ResourceGroup();
        var before = await Plan();
        var same = await Plan();
        _apps.Add(new ContainerAppInfo("demo-db", "id", NoTags));
        var changed = await Plan();

        before.HasSameOperations(same).Should().BeTrue();
        before.HasSameOperations(changed).Should().BeFalse();
    }
}

public class DeploymentInspectorTests
{
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ContainerAppDetails App(string name, string? fqdn = null) =>
        new(name, "Succeeded", "Running", "image", 1.0, "2Gi", "rev1", fqdn, new Dictionary<string, string>());

    [Fact]
    public async Task Should_Return_Nothing_When_The_Resource_Group_Does_Not_Exist()
    {
        var info = await new DeploymentInspector(_gateway.Object).InspectAsync("sub", "rg", "demo", Ct);

        info.Should().Be(new DeploymentInfo("sub", "rg", "demo", null, null, null, null, null));
        info.Exists.Should().BeFalse();
        info.Endpoint.Should().BeNull();
    }

    [Fact]
    public async Task Should_Return_The_Resources_And_The_Endpoint()
    {
        var rg = new ResourceGroupInfo("rg", "japaneast");
        var env = new ManagedEnvironmentInfo("id", "demo-env", "japaneast", new Dictionary<string, string>(), "Succeeded");
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(rg);
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>())).ReturnsAsync(env);
        _gateway.Setup(g => g.ListContainerAppDetailsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([App("DEMO-TT", "demo-tt.example.io"), App("demo-db"), App("other-redis")]);

        var info = await new DeploymentInspector(_gateway.Object).InspectAsync("sub", "rg", "demo", Ct);

        (info.ResourceGroupInfo, info.Environment).Should().Be((rg, env));
        info.Timetracker!.Name.Should().Be("DEMO-TT");
        info.Db!.Name.Should().Be("demo-db");
        info.Redis.Should().BeNull();
        info.Exists.Should().BeTrue();
        info.Endpoint.Should().Be("https://demo-tt.example.io");
    }

    [Fact]
    public async Task Should_Return_The_Storage_Account_And_Whether_The_Db_Is_Persisted()
    {
        var account = new StorageAccountInfo(ResourceNaming.StorageAccount("sub", "rg", "demo"), "japaneast", new Dictionary<string, string>());
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.ListContainerAppDetailsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([App("demo-db") with { StorageNames = [DbPersistence.StorageName] }]);
        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", account.Name, It.IsAny<CancellationToken>())).ReturnsAsync(account);

        var info = await new DeploymentInspector(_gateway.Object).InspectAsync("sub", "rg", "demo", Ct);

        info.StorageAccount.Should().Be(account);
        info.DbPersisted.Should().BeTrue();
    }

    [Fact]
    public void DbPersisted_Should_Be_False_Without_A_Db_App_Or_A_Mount()
    {
        new DeploymentInfo("sub", "rg", "demo", null, null, null, null, null).DbPersisted.Should().BeFalse();
        new DeploymentInfo("sub", "rg", "demo", null, null, null, App("demo-db"), null).DbPersisted.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Exists_Should_Be_True_When_Any_Resource_Exists(bool env, bool tt, bool db, bool redis)
    {
        var info = new DeploymentInfo("sub", "rg", "demo", new ResourceGroupInfo("rg", "japaneast"),
            env ? new ManagedEnvironmentInfo("id", "demo-env", "japaneast", new Dictionary<string, string>()) : null,
            tt ? App("demo-tt") : null, db ? App("demo-db") : null, redis ? App("demo-redis") : null);

        info.Exists.Should().BeTrue();
        info.Endpoint.Should().BeNull();
    }

    [Fact]
    public void Exists_Should_Be_True_When_Only_The_Storage_Account_Remains()
    {
        // delete --keep-db-data の後
        var info = new DeploymentInfo("sub", "rg", "demo", new ResourceGroupInfo("rg", "japaneast"), null, null, null, null,
            new StorageAccountInfo("ttdemo12345678", "japaneast", new Dictionary<string, string>()));

        info.Exists.Should().BeTrue();
    }
}
