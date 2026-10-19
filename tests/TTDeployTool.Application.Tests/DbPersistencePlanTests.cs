using FluentAssertions;
using Moq;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Application.Tests;

/// <summary>DB のデータの永続化（--persist-db）の計画・権限の事前確認（詳細設計 docs/design/db-persistence.md の 6・7）。</summary>
public class DbPersistencePlanTests
{
    private const string EnvId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/demo-env";
    private static readonly string Account = ResourceNaming.StorageAccount("sub", "rg", "demo");
    private static readonly IReadOnlyDictionary<string, string> NoTags = new Dictionary<string, string>();

    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DbPersistencePlanTests()
    {
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps);
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _gateway.Setup(g => g.IsStorageAccountNameAvailableAsync("sub", Account, It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private static DeployOptions Opts(bool? persistDb) => new()
    {
        Subscription = "sub", ResourceGroup = "rg", AppName = "demo", DbPassword = "x", TrackerPassword = "x", Location = "japaneast",
        PersistDb = persistDb,
    };

    private void ResourceGroup() =>
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));

    private void Environment() =>
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo(EnvId, "demo-env", "japaneast", NoTags));

    private void DbApp(bool persisted) =>
        _apps.Add(new ContainerAppInfo("demo-db", EnvId, NoTags, persisted ? [DbPersistence.StorageName] : []));

    // ストレージアカウント・ファイル共有・Environment のストレージが既にある
    private void ExistingStorage()
    {
        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageAccountInfo(Account, "japaneast", NoTags));
        _gateway.Setup(g => g.FileShareExistsAsync("sub", "rg", Account, "demo-db", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _gateway.Setup(g => g.EnvironmentStorageExistsAsync("sub", "rg", "demo-env", DbPersistence.StorageName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private Task<DeploymentPlan> Plan(bool? persistDb) => new DeploymentPlanner(_gateway.Object).PlanAsync(Opts(persistDb), Ct);

    private static PlannedOperation Op(PlannedResourceKind kind, string name, PlannedAction action, bool dataLoss = false) => new(kind, name, action, dataLoss);

    private static PlannedOperation DbOp(DeploymentPlan plan) => plan.Operations.Single(o => o.Kind == PlannedResourceKind.ContainerApp && o.Name == "demo-db");

    [Fact]
    public async Task Should_Create_The_Storage_For_A_New_Deployment()
    {
        var plan = await Plan(true);

        plan.Operations.Should().Equal(
            Op(PlannedResourceKind.ResourceGroup, "rg", PlannedAction.Create),
            Op(PlannedResourceKind.Environment, "demo-env", PlannedAction.Create),
            Op(PlannedResourceKind.StorageAccount, Account, PlannedAction.Create),
            Op(PlannedResourceKind.FileShare, "demo-db", PlannedAction.Create),
            Op(PlannedResourceKind.EnvironmentStorage, DbPersistence.StorageName, PlannedAction.Create),
            Op(PlannedResourceKind.ContainerApp, "demo-db", PlannedAction.Create),
            Op(PlannedResourceKind.ContainerApp, "demo-redis", PlannedAction.Create),
            Op(PlannedResourceKind.ContainerApp, "demo-tt", PlannedAction.Create));
        plan.PersistDb.Should().BeTrue();
        plan.RequiresDataLossAck.Should().BeFalse();
        plan.Warnings.Should().Contain(DbPersistence.StorageNotice(Account));
    }

    [Fact]
    public async Task Should_Not_Persist_A_New_Deployment_When_Not_Specified()
    {
        var plan = await Plan(null);

        plan.PersistDb.Should().BeFalse();
        plan.Operations.Should().NotContain(o => o.Kind == PlannedResourceKind.StorageAccount);
        _gateway.Verify(g => g.IsStorageAccountNameAvailableAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_Block_When_The_Storage_Account_Name_Is_Taken_Elsewhere()
    {
        _gateway.Setup(g => g.IsStorageAccountNameAvailableAsync("sub", Account, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var plan = await Plan(true);

        plan.CanRun.Should().BeFalse();
        plan.Blockers.Should().ContainSingle().Which.Should().Contain(Account).And.Contain("app_name");
    }

    [Fact]
    public async Task Should_Reuse_The_Existing_Storage_And_Register_The_Key_Again()
    {
        ResourceGroup();
        Environment();
        ExistingStorage();

        var plan = await Plan(true);

        plan.Operations.Where(o => o.Kind is PlannedResourceKind.StorageAccount or PlannedResourceKind.FileShare or PlannedResourceKind.EnvironmentStorage)
            .Should().Equal(
                Op(PlannedResourceKind.StorageAccount, Account, PlannedAction.UseExisting),
                Op(PlannedResourceKind.FileShare, "demo-db", PlannedAction.UseExisting),
                Op(PlannedResourceKind.EnvironmentStorage, DbPersistence.StorageName, PlannedAction.Update));
        _gateway.Verify(g => g.IsStorageAccountNameAvailableAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_Create_The_Share_And_The_Environment_Storage_When_Only_The_Account_Exists()
    {
        ResourceGroup();
        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageAccountInfo(Account, "japaneast", NoTags));

        var plan = await Plan(true);

        plan.Operations.Should().Contain(Op(PlannedResourceKind.FileShare, "demo-db", PlannedAction.Create));
        // Environment が無ければ、その中のストレージも無い
        plan.Operations.Should().Contain(Op(PlannedResourceKind.EnvironmentStorage, DbPersistence.StorageName, PlannedAction.Create));
        _gateway.Verify(g => g.EnvironmentStorageExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // 詳細設計 6 の表: (既存の DB アプリ, 今回の指定) → データ消失の同意が要るか、実際に永続化するか
    [Theory]
    [InlineData(false, false, true, false)]  // 永続化なし → なし（今と同じ）
    [InlineData(false, null, true, false)]   // 永続化なし → 指定なし（今の状態を引き継ぐ）
    [InlineData(false, true, true, true)]    // 永続化なし → あり（今のデータは引き継がれない）
    [InlineData(true, true, false, true)]    // 永続化あり → あり（停止 → 更新 → 開始で残る）
    [InlineData(true, null, false, true)]    // 永続化あり → 指定なし（引き継いで残る）
    [InlineData(true, false, true, false)]   // 永続化あり → なし（DB は空から始まる）
    public async Task Should_Decide_The_Data_Loss_Ack_From_The_Current_And_The_Requested_Persistence(
        bool currentlyPersisted, bool? persistDb, bool dataLoss, bool expectedPersist)
    {
        ResourceGroup();
        Environment();
        ExistingStorage();
        DbApp(currentlyPersisted);

        var plan = await Plan(persistDb);

        DbOp(plan).Should().Be(Op(PlannedResourceKind.ContainerApp, "demo-db", PlannedAction.Update, dataLoss));
        plan.RequiresDataLossAck.Should().Be(dataLoss);
        plan.PersistDb.Should().Be(expectedPersist);
        var expectedWarning = DbPersistence.DataLossWarning("demo-db", currentlyPersisted, expectedPersist) ?? DbPersistence.RestartNotice("demo-db");
        plan.Warnings.Should().Contain(expectedWarning);
    }

    [Fact]
    public async Task Should_Require_Storage_Permissions_At_The_Resource_Group_When_Creating()
    {
        ResourceGroup();
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["Microsoft.App/*", "Microsoft.Resources/*"], [])]);

        var plan = await Plan(true);

        plan.PermissionCheck.MissingActions.Should().Equal(
            AzurePermissions.StorageAccountRead, AzurePermissions.StorageAccountWrite, AzurePermissions.StorageAccountListKeys,
            AzurePermissions.FileShareRead, AzurePermissions.FileShareWrite);
        plan.Blockers.Should().ContainSingle().Which.Should().Contain(AzurePermissions.StorageAccountWrite);
    }

    [Fact]
    public async Task Should_Check_Existing_Storage_And_Stop_Start_Permissions_On_The_Resources()
    {
        // リソースグループでは Reader、各リソースに Contributor が割り当てられている
        ResourceGroup();
        Environment();
        ExistingStorage();
        DbApp(true);
        _apps.Add(new ContainerAppInfo("demo-redis", EnvId, NoTags));
        _apps.Add(new ContainerAppInfo("demo-tt", EnvId, NoTags));
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*/read"], [])]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["*"], [])]);

        var plan = await Plan(null);

        plan.PermissionCheck.Should().Be(PermissionCheckResult.Ok);
        _gateway.Verify(g => g.GetResourcePermissionsAsync("sub", "rg", "Microsoft.Storage", "storageAccounts", Account, It.IsAny<CancellationToken>()), Times.Once);
        _gateway.Verify(g => g.GetResourcePermissionsAsync("sub", "rg", "Microsoft.App", "containerApps", "demo-db", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_Require_Stop_And_Start_Only_When_Persistence_Continues()
    {
        ResourceGroup();
        Environment();
        ExistingStorage();
        DbApp(true);
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermissionSet(["*"], ["Microsoft.App/containerApps/stop/action", "Microsoft.App/containerApps/start/action"])]);

        (await Plan(true)).PermissionCheck.MissingActions.Should().Equal(AzurePermissions.ContainerAppStop, AzurePermissions.ContainerAppStart);
        (await Plan(false)).PermissionCheck.Should().Be(PermissionCheckResult.Ok);
    }

    [Fact]
    public async Task HasSameOperations_Should_Detect_A_Storage_Created_After_Planning()
    {
        ResourceGroup();
        Environment();
        var before = await Plan(true);
        ExistingStorage();

        var after = await Plan(true);

        before.HasSameOperations(after).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData(true, false, true)]
    public async Task CheckPermissions_Should_Include_The_Storage_When_Persisting(bool? persistDb, bool currentlyPersisted, bool expectStorage)
    {
        ResourceGroup();
        Environment();
        DbApp(currentlyPersisted);
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["Microsoft.App/*"], [])]);

        var result = await new DeploymentPlanner(_gateway.Object).CheckPermissionsAsync(Opts(persistDb), Ct);

        result.MissingActions.Contains(AzurePermissions.StorageAccountRead).Should().Be(expectStorage);
    }

    [Fact]
    public async Task UsesDbStorage_Should_Be_False_Without_A_Resource_Group_Or_A_Db_App()
    {
        (await DeploymentPlanner.UsesDbStorageAsync(_gateway.Object, Opts(null), Ct)).Should().BeFalse();
        ResourceGroup();
        (await DeploymentPlanner.UsesDbStorageAsync(_gateway.Object, Opts(null), Ct)).Should().BeFalse();
        DbApp(true);
        (await DeploymentPlanner.UsesDbStorageAsync(_gateway.Object, Opts(null), Ct)).Should().BeTrue();
    }
}
