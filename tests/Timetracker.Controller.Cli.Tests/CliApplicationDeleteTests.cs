using Azure.ResourceManager.AppContainers;
using FluentAssertions;
using Moq;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;
using Xunit;

namespace Timetracker.Controller.Cli.Tests;

public sealed class CliApplicationDeleteTests
{
    private const string EnvId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/demo-env";
    private static readonly string[] Base = { "delete", "--subscription", "sub", "--resource-group", "rg", "--app-name", "demo" };

    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<string> _deleted = [];
    private DeployOptions? _factoryOptions;

    public CliApplicationDeleteTests()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, name, _) => _deleted.Add(name)).ReturnsAsync(true);
        _gateway.Setup(g => g.DeleteEnvironmentAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, name, _) => _deleted.Add(name)).ReturnsAsync(true);
    }

    private void Resources(IReadOnlyDictionary<string, string> dbTags)
    {
        var managed = ManagedTags.For("demo");
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ContainerAppInfo("demo-tt", EnvId, managed),
            new ContainerAppInfo("demo-redis", EnvId, managed),
            new ContainerAppInfo("demo-db", EnvId, dbTags),
        ]);
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo(EnvId, "demo-env", "japaneast", managed));
    }

    private void AllManaged() => Resources(ManagedTags.For("demo"));

    private Task<int> Run(string[] extra, string? input = null) =>
        CliApplication.RunAsync(Base.Concat(extra).ToArray(), _out, _err, "", (opts, _) => { _factoryOptions = opts; return _gateway.Object; },
            new StringReader(input ?? ""));

    [Theory]
    [InlineData("--subscription")]
    [InlineData("--resource-group")]
    public async Task Should_Require_Subscription_And_Resource_Group(string missing)
    {
        var args = new[] { "delete", "--subscription", "sub", "--resource-group", "rg" };
        var index = Array.IndexOf(args, missing);

        var code = await CliApplication.RunAsync(args.Where((_, i) => i != index && i != index + 1).ToArray(), _out, _err, "", (_, _) => _gateway.Object);

        code.Should().Be(1);
        _err.ToString().Should().Contain("必須パラメータ不足").And.Contain(missing);
    }

    [Fact]
    public async Task Should_Reject_Invalid_App_Name()
    {
        var code = await CliApplication.RunAsync(new[] { "delete", "--subscription", "sub", "--resource-group", "rg", "--app-name", "Bad_Name" },
            _out, _err, "", (_, _) => _gateway.Object);

        code.Should().Be(1);
        _err.ToString().Should().Contain("アプリ名 'Bad_Name' は無効です");
    }

    [Fact]
    public async Task Should_Delete_After_Confirmation()
    {
        AllManaged();

        var code = await Run([], input: "demo\n");

        code.Should().Be(0);
        _deleted.Should().Equal("demo-tt", "demo-redis", "demo-db", "demo-env");
        var output = _out.ToString();
        output.Should().Contain("削除計画（リソースグループ rg は削除しません）")
            .And.Contain("削除する     demo-tt (Container App)")
            .And.Contain("削除する     demo-env (Environment)")
            .And.Contain("[WARN] DB アプリを削除すると DB のデータはすべて失われます。")
            .And.Contain("アプリ名 'demo' を入力してください")
            .And.Contain("削除中: demo-tt")
            .And.Contain("[SUCCESS] 削除が完了しました（リソースグループ rg は残しています）。");
    }

    [Theory]
    [InlineData("other\n")]
    [InlineData("")] // 入力なし（EOF）
    public async Task Should_Cancel_When_Confirmation_Does_Not_Match(string input)
    {
        AllManaged();

        var code = await Run([], input);

        code.Should().Be(1);
        _deleted.Should().BeEmpty();
        _err.ToString().Should().Contain("削除を中止しました");
    }

    [Fact]
    public async Task Yes_Should_Skip_Confirmation()
    {
        AllManaged();

        var code = await Run(["--yes"]);

        code.Should().Be(0);
        _deleted.Should().HaveCount(4);
        _out.ToString().Should().NotContain("入力してください");
    }

    [Fact]
    public async Task DryRun_Should_Only_Show_Plan()
    {
        AllManaged();

        var code = await Run(["--dry-run"]);

        code.Should().Be(0);
        _deleted.Should().BeEmpty();
        _out.ToString().Should().Contain("削除計画").And.Contain("Dry-run のため削除は行いません。");
    }

    [Fact]
    public async Task Should_Show_Hint_And_Keep_Untagged_Resources()
    {
        Resources(new Dictionary<string, string>());

        var code = await Run(["--yes"]);

        code.Should().Be(0);
        _deleted.Should().Equal("demo-tt", "demo-redis");
        var output = _out.ToString();
        output.Should().Contain("削除しない   demo-db (Container App): 管理タグがありません")
            .And.Contain("削除しない   demo-env (Environment)")
            .And.Contain("--include-untagged を指定してください")
            .And.NotContain("DB のデータはすべて失われます");
    }

    [Fact]
    public async Task IncludeUntagged_Should_Delete_Untagged_Resources()
    {
        Resources(new Dictionary<string, string>());

        var code = await Run(["--yes", "--include-untagged"]);

        code.Should().Be(0);
        _deleted.Should().Equal("demo-tt", "demo-redis", "demo-db", "demo-env");
        _out.ToString().Should().Contain("削除する     demo-db (Container App) ※管理タグなし");
    }

    [Fact]
    public async Task Should_Finish_When_Nothing_To_Delete()
    {
        var code = await Run([]);

        code.Should().Be(0);
        _out.ToString().Should().Contain("削除するリソースはありません。");
    }

    [Fact]
    public async Task Should_Stop_When_Permissions_Are_Missing()
    {
        AllManaged();
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*/read"], [])]);

        var code = await Run(["--yes"]);

        code.Should().Be(1);
        _deleted.Should().BeEmpty();
        _out.ToString().Should().Contain("[WARN] 権限が不足しています: Microsoft.App/containerApps/delete");
        _err.ToString().Should().Contain("[ERROR] 権限が不足しています").And.Contain("必要な RBAC ロールと権限");
    }

    [Fact]
    public async Task Should_Continue_When_Permissions_Cannot_Be_Checked()
    {
        AllManaged();
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("forbidden"));

        var code = await Run(["--yes", "--verbose"]);

        code.Should().Be(0);
        _deleted.Should().HaveCount(4);
        _out.ToString().Should().Contain("[WARN] 権限を事前に確認できませんでした").And.Contain("forbidden")
            .And.Contain("[VERBOSE] 権限の事前確認をスキップしました");
    }

    [Fact]
    public async Task Should_Report_Already_Deleted_Resources()
    {
        AllManaged();
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-redis", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var code = await Run(["--yes"]);

        code.Should().Be(0);
        _out.ToString().Should().Contain("demo-redis は既に削除されていました。");
    }

    [Fact]
    public async Task Should_Fail_When_Deletion_Fails()
    {
        AllManaged();
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("delete failed"));

        var code = await Run(["--yes"]);

        code.Should().Be(1);
        _err.ToString().Should().Contain("[ERROR] エラー: delete failed");
        _out.ToString().Should().NotContain("[SUCCESS]");
    }

    [Fact]
    public async Task Should_Pass_Auth_Mode_And_Lowercased_App_Name()
    {
        var code = await CliApplication.RunAsync(
            new[] { "delete", "--subscription", "sub", "--resource-group", "rg", "--app-name", "Demo", "--auth-mode", "azure-cli", "--dry-run" },
            _out, _err, "", (opts, _) => { _factoryOptions = opts; return _gateway.Object; });

        code.Should().Be(0);
        _factoryOptions!.AuthMode.Should().Be("azure-cli");
        _factoryOptions.AppName.Should().Be("demo");
    }
}

public sealed class CliApplicationDeployPermissionTests
{
    private static readonly string[] Args =
    {
        "--subscription", "sub", "--resource-group", "rg", "--db-password", "DbPassw0rd!", "--tracker-password", "TrackerPass!", "--verbose",
    };

    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();

    public CliApplicationDeployPermissionTests()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _gateway.Setup(g => g.CreateOrUpdateContainerAppAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DeployOptions>(), It.IsAny<ContainerAppManagedEnvironmentResource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerAppsDeployment("timetracker-tt", "timetracker-db", "timetracker-redis", null));
    }

    private Task<int> Run() => CliApplication.RunAsync(Args, _out, _err, "", (_, _) => _gateway.Object);

    [Fact]
    public async Task Should_Stop_Before_Deploying_When_Permissions_Are_Missing()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*/read"], [])]);

        var code = await Run();

        code.Should().Be(1);
        _err.ToString().Should().Contain("[ERROR] 権限が不足しています: Microsoft.App/containerApps/write");
        _gateway.Verify(g => g.EnsureResourceGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_Deploy_When_Permissions_Are_Sufficient()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);

        var code = await Run();

        code.Should().Be(0);
        _out.ToString().Should().Contain("[VERBOSE] 権限の事前確認: OK").And.Contain("[SUCCESS] Container Apps デプロイ完了");
    }

    [Fact]
    public async Task Should_Deploy_When_Resource_Group_Does_Not_Exist_Yet()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceGroupInfo?)null);

        var code = await Run();

        code.Should().Be(0);
        _out.ToString().Should().Contain("[VERBOSE] 権限の事前確認をスキップしました: リソースグループ 'rg' がまだ無いため");
    }
}
