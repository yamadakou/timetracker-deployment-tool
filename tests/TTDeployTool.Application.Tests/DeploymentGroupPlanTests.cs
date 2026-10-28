using Azure.ResourceManager.AppContainers;
using FluentAssertions;
using Moq;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Application.Tests;

/// <summary>計画・実行・状態の確認でのグループ（docs/design/common/deployment-groups.md の 4）。</summary>
public class DeploymentGroupPlanTests
{
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];
    private readonly ContainerAppManagedEnvironmentResource _env = new Mock<ContainerAppManagedEnvironmentResource>().Object;
    private DeployOptions? _sent;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeploymentGroupPlanTests()
    {
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps);
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _gateway.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "demo", "japaneast", It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(_env);
        _gateway.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(), _env, It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, DeployOptions, ContainerAppManagedEnvironmentResource, CancellationToken>((_, _, _, _, o, _, _) => _sent = o)
            .ReturnsAsync(new ContainerAppsDeployment("demo-tt", "demo-db", "demo-redis", null));
    }

    private static DeployOptions Opts(string? group = null) => new()
    {
        Subscription = "sub", ResourceGroup = "rg", AppName = "demo", DbPassword = "DbPassw0rd!", TrackerPassword = "TrackerPass!",
        Location = "japaneast", Group = group,
    };

    // デプロイ済み（DB アプリのタグにだけグループがある）
    private void Deployed(string? ttGroup, string? dbGroup)
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo("id", "demo-env", "japaneast", ManagedTags.For("demo")));
        _apps.Add(new ContainerAppInfo("DEMO-TT", "id", Tags(ttGroup)));
        _apps.Add(new ContainerAppInfo("demo-db", "id", Tags(dbGroup)));
    }

    private static Dictionary<string, string> Tags(string? group)
    {
        var tags = new Dictionary<string, string>(ManagedTags.For("demo"));
        if (group != null)
            tags[DeploymentGroup.Key] = group;
        return tags;
    }

    [Fact]
    public async Task A_New_Deployment_Should_Record_The_Given_Group_Only()
    {
        var plan = await new DeploymentPlanner(_gateway.Object).PlanAsync(Opts("研修"), Ct);
        plan.Group.Should().Be(new GroupDecision(null, "研修"));

        var result = await new DeploymentRunner(_gateway.Object).RunAsync(Opts("研修"), cancellationToken: Ct);
        result.Group.Should().Be(plan.Group);
        _sent!.LifecycleTags![DeploymentGroup.Key].Should().Be("研修");

        await new DeploymentRunner(_gateway.Object).RunAsync(Opts(), cancellationToken: Ct);
        _sent!.LifecycleTags!.Should().NotContainKey(DeploymentGroup.Key, "指定しなければ書かない");
    }

    [Fact]
    public async Task A_Redeployment_Without_A_Group_Should_Keep_The_Current_One()
    {
        Deployed(null, "営業");

        var plan = await new DeploymentPlanner(_gateway.Object).PlanAsync(Opts(), Ct);
        plan.Group.Should().Be(new GroupDecision("営業", "営業"), "Timetracker に無ければ DB のものを読む");
        var result = await new DeploymentRunner(_gateway.Object).RunAsync(Opts(), cancellationToken: Ct);
        result.Group!.Inherited.Should().BeTrue();
        _sent!.LifecycleTags!.Should().NotContainKey(DeploymentGroup.Key, "今のタグは、アプリの更新でそのまま残る");

        var changed = await new DeploymentPlanner(_gateway.Object).PlanAsync(Opts("研修"), Ct);
        changed.Group!.Describe().Should().Be("グループ: 研修（今の 営業 から変えます）");
    }

    [Fact]
    public void The_Current_Group_Should_Prefer_The_Timetracker_App()
    {
        DeploymentPlanner.CurrentGroup(
            [new ContainerAppInfo("demo-db", "id", Tags("営業")), new ContainerAppInfo("demo-tt", "id", Tags("研修"))], "demo").Should().Be("研修");
        DeploymentPlanner.CurrentGroup([new ContainerAppInfo("other-tt", "id", Tags("研修"))], "demo").Should().BeNull();
    }

    [Fact]
    public void Deployment_Info_Should_Show_The_Group()
    {
        static ContainerAppDetails App(string name, string? group) => new(name, null, null, null, null, null, null, null, Tags(group));

        new DeploymentInfo("sub", "rg", "demo", null, null, App("demo-tt", null), App("demo-db", null), App("demo-redis", "営業")).Group.Should().Be("営業");
        new DeploymentInfo("sub", "rg", "demo", null, null, null, null, null,
            new StorageAccountInfo("tt", "japaneast", Tags("研修"))).Group.Should().Be("研修");
        new DeploymentInfo("sub", "rg", "demo", null, null, null, null, null).Group.Should().BeNull();
    }
}

/// <summary>デプロイの一覧のグループと、まとめての変更（docs/design/common/deployment-groups.md の 3・5）。</summary>
public class DeploymentGroupInventoryTests
{
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];
    private readonly List<string> _calls = [];
    private readonly string _account = ResourceNaming.StorageAccount("sub", "rg", "demo");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeploymentGroupInventoryTests()
    {
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps);
        _gateway.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, n, t, _) => _calls.Add($"merge {n} {Show(t)}"))
            .Returns(Task.CompletedTask);
        _gateway.Setup(g => g.DeleteContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, n, t, _) => _calls.Add($"delete {n} {Show(t)}"))
            .Returns(Task.CompletedTask);
        _gateway.Setup(g => g.UpdateStorageAccountTagsAsync("sub", "rg", _account, It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, n, t, _) => _calls.Add($"merge storage {Show(t)}"))
            .Returns(Task.CompletedTask);
        _gateway.Setup(g => g.DeleteStorageAccountTagsAsync("sub", "rg", _account, It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, n, t, _) => _calls.Add($"delete storage {Show(t)}"))
            .Returns(Task.CompletedTask);
    }

    private static string Show(IReadOnlyDictionary<string, string> tags) => string.Join(",", tags.Select(t => $"{t.Key}={t.Value}"));

    private static Dictionary<string, string> Tags(string appName, string? group, string key = DeploymentGroup.Key)
    {
        var tags = new Dictionary<string, string>(ManagedTags.For(appName));
        if (group != null)
            tags[key] = group;
        return tags;
    }

    private void Storage(string? group, string appName = "demo") =>
        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", _account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageAccountInfo(_account, "japaneast", Tags(appName, group)));

    private DeploymentMaintenance Sut() => new(_gateway.Object);

    [Fact]
    public async Task Changing_The_Group_Should_Merge_The_Tag_Into_The_Managed_Resources_Only()
    {
        _apps.Add(new ContainerAppInfo("DEMO-TT", "env", Tags("demo", "営業")));
        _apps.Add(new ContainerAppInfo("demo-db", "env", Tags("demo", null)));
        _apps.Add(new ContainerAppInfo("demo-redis", "env", new Dictionary<string, string>()));
        _apps.Add(new ContainerAppInfo("other-tt", "env", Tags("other", null)));
        Storage(null);

        (await Sut().ChangeGroupAsync("sub", "rg", "demo", " 研修 ", Ct)).Should().Be("研修");

        _calls.Should().Equal("merge demo-tt tt-group=研修", "merge demo-db tt-group=研修", "merge storage tt-group=研修");
        _gateway.Verify(g => g.GetEnvironmentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "Environment は書き換えない");
    }

    [Fact]
    public async Task Removing_The_Group_Should_Delete_Only_The_Existing_Tags()
    {
        _apps.Add(new ContainerAppInfo("demo-tt", "env", Tags("demo", "営業", key: "TT-Group")));
        _apps.Add(new ContainerAppInfo("demo-db", "env", Tags("demo", null)));
        Storage("営業");

        (await Sut().ChangeGroupAsync("sub", "rg", "demo", null, Ct)).Should().BeNull();

        _calls.Should().Equal("delete demo-tt TT-Group=営業", "delete storage tt-group=営業");

        // ストレージアカウントにタグが無い・管理タグが無い
        _calls.Clear();
        Storage(null);
        await Sut().ChangeGroupAsync("sub", "rg", "demo", " ", Ct);
        _calls.Should().Equal("delete demo-tt TT-Group=営業");
        _calls.Clear();
        Storage("営業", appName: "other");
        await Sut().ChangeGroupAsync("sub", "rg", "demo", null, Ct);
        _calls.Should().Equal("delete demo-tt TT-Group=営業");
    }

    [Fact]
    public async Task Changing_The_Group_Should_Work_For_Leftovers_And_Fail_When_Nothing_Is_Left()
    {
        Storage(null);
        await Sut().ChangeGroupAsync("sub", "rg", "demo", "研修", Ct);
        _calls.Should().Equal("merge storage tt-group=研修");

        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", _account, It.IsAny<CancellationToken>())).ReturnsAsync((StorageAccountInfo?)null);
        var act = () => Sut().ChangeGroupAsync("sub", "rg", "demo", "研修", Ct);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("本ツールで作った Container App とストレージアカウントがありません");
    }

    [Fact]
    public async Task A_Wrong_Group_Name_Should_Be_Rejected_Without_Reading_Azure()
    {
        var act = () => Sut().ChangeGroupAsync("sub", "rg", "demo", new string('x', 65), Ct);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().StartWith("group（グループ名）は 64 文字まで");
        _gateway.Verify(g => g.ListContainerAppsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ManagedResourceRecord Record(string name, string type, IReadOnlyDictionary<string, string> tags) =>
        new($"/subscriptions/s1/resourceGroups/rg/providers/x/{name}", name, type, "s1", "rg", "japaneast", tags,
            type == ManagedResourceRecord.ContainerAppType ? new ContainerAppSnapshot("Succeeded", "Running", null, 0.5, "1Gi", null, []) : null);

    [Fact]
    public void The_List_Should_Show_The_Group_And_Whether_It_Differs()
    {
        var same = DeploymentCatalog.Build(
        [
            Record("demo-tt", ManagedResourceRecord.ContainerAppType, Tags("demo", "営業")),
            Record("demo-db", ManagedResourceRecord.ContainerAppType, Tags("demo", "営業")),
            Record("demo-env", ManagedResourceRecord.EnvironmentType, Tags("demo", "古い")),
        ]).Single();
        (same.Group, same.GroupDiffers).Should().Be(("営業", false), "Environment は、ほかがあれば見ない");

        var differs = DeploymentCatalog.Build(
        [
            Record("demo-tt", ManagedResourceRecord.ContainerAppType, Tags("demo", null)),
            Record("demo-db", ManagedResourceRecord.ContainerAppType, Tags("demo", "営業")),
        ]).Single();
        (differs.Group, differs.GroupDiffers).Should().Be(("営業", true));

        var envOnly = DeploymentCatalog.Build([Record("demo-env", ManagedResourceRecord.EnvironmentType, Tags("demo", "研修"))]).Single();
        envOnly.Group.Should().Be("研修", "作りかけで Environment だけ");

        var none = DeploymentCatalog.Build([Record("demo-tt", ManagedResourceRecord.ContainerAppType, Tags("demo", null))]).Single();
        (none.Group, none.GroupDiffers).Should().Be((null, false));
    }
}
