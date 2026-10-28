using Azure.ResourceManager.AppContainers;
using FluentAssertions;
using Moq;
using TTDeployTool.Common;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Cli.Tests;

public sealed class CliApplicationTests : IDisposable
{
    private static readonly string[] Required =
    {
        "--subscription", "sub", "--resource-group", "rg", "--db-password", "DbPassw0rd!", "--tracker-password", "TrackerPass!",
    };

    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("tt-cli-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Task<int> Run(string[] args, CliApplication.GatewayFactory? gatewayFactory = null) =>
        CliApplication.RunAsync(args, _out, _err, _dir, gatewayFactory ?? NotCalled);

    private static string[] With(params string[] extra) => Required.Concat(extra).ToArray();

    // Dry-run や引数エラーでは Azure を操作しないことを確かめるため、呼ばれたら失敗させる
    private static IAzureDeploymentGateway NotCalled(DeployOptions opts, IDeploymentLogger logger) =>
        throw new InvalidOperationException("Azure の操作は呼ばれないはず");

    [Fact]
    public async Task Should_Fail_On_Invalid_Arguments()
    {
        var code = await Run(new[] { "--unknown", "x" });

        code.Should().Be(1);
        _err.ToString().Should().Contain("[ERROR]").And.Contain("--unknown");
    }

    [Fact]
    public async Task Should_Print_Help()
    {
        var code = await Run(new[] { "--help" });

        code.Should().Be(0);
        _out.ToString().Should().Contain(CliArgs.HelpText);
        _err.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("--subscription")]
    [InlineData("--resource-group")]
    [InlineData("--db-password")]
    [InlineData("--tracker-password")]
    public async Task Should_Fail_When_Required_Option_Is_Missing(string missing)
    {
        var index = Array.IndexOf(Required, missing);
        var args = Required.Where((_, i) => i != index && i != index + 1).ToArray();

        var code = await Run(args);

        code.Should().Be(1);
        // 足りないオプションだけを表示する
        var message = _err.ToString();
        message.Should().Contain("必須パラメータ不足").And.Contain(missing);
        foreach (var other in Required.Where((_, i) => i % 2 == 0 && i != index))
        {
            message.Should().NotContain(other);
        }
    }

    [Fact]
    public async Task Should_List_All_Missing_Required_Options()
    {
        var code = await Run(Array.Empty<string>());

        code.Should().Be(1);
        _err.ToString().Should().Contain("--subscription --resource-group --db-password --tracker-password");
    }

    [Theory]
    [InlineData("--db-password")]
    [InlineData("--tracker-password")]
    public async Task DryRun_Should_Still_Require_Passwords(string missing)
    {
        var args = new[] { "--db-password", "DbPassw0rd!", "--tracker-password", "TrackerPass!", "--dry-run" };
        var index = Array.IndexOf(args, missing);

        var code = await Run(args.Where((_, i) => i != index && i != index + 1).ToArray());

        code.Should().Be(1);
        _err.ToString().Should().Contain("必須パラメータ不足").And.Contain(missing)
            .And.NotContain("--subscription").And.NotContain("--resource-group");
    }

    [Fact]
    public async Task DryRun_Should_Not_Require_Subscription_And_Resource_Group()
    {
        var code = await Run(new[] { "--db-password", "DbPassw0rd!", "--tracker-password", "TrackerPass!", "--dry-run" });

        code.Should().Be(0);
        var opts = DeployOptionsResolver.Resolve(new DeployInput { DbPassword = "DbPassw0rd!", TrackerPassword = "TrackerPass!" });
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().Be(ComposeGenerator.GenerateCompose(opts));
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(ComposeGenerator.GenerateEnv(opts));
    }

    [Fact]
    public async Task DryRun_Should_Fail_When_Validation_Fails()
    {
        var code = await Run(new[]
        {
            "--db-type", "sqlserver", "--db-password", "password", "--tracker-password", "TrackerPass!", "--dry-run",
        });

        code.Should().Be(1);
        _err.ToString().Should().Contain("SQL Server のパスワード要件");
        File.Exists(Path.Combine(_dir, "docker-compose.yml")).Should().BeFalse();
    }

    [Fact]
    public async Task Should_Fail_On_Invalid_Number()
    {
        var code = await Run(With("--tt-cpu", "abc"));

        code.Should().Be(1);
        _err.ToString().Should().Contain("--tt-cpu");
    }

    [Fact]
    public async Task Should_Fail_On_Invalid_App_Name_With_Original_Name()
    {
        var code = await Run(With("--app-name", "1Bad"));

        code.Should().Be(1);
        // 小文字化する前の、利用者が指定したとおりの名前で表示する
        _err.ToString().Should().Contain("アプリ名 '1Bad' は無効です");
    }

    [Fact]
    public async Task Should_Fail_When_Validation_Fails()
    {
        var code = await Run(With("--db-type", "sqlserver", "--tt-tag", "11.0.1-linux-postgres"));

        code.Should().Be(1);
        _err.ToString().Should().Contain("[ERROR]").And.Contain("不適切な組み合わせ");
    }

    [Fact]
    public async Task DryRun_Should_Write_Compose_And_Env_Without_Calling_Azure()
    {
        var code = await Run(With("--dry-run"));

        code.Should().Be(0);
        var opts = DeployOptionsResolver.Resolve(new DeployInput
        {
            Subscription = "sub", ResourceGroup = "rg", DbPassword = "DbPassw0rd!", TrackerPassword = "TrackerPass!",
        });
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().Be(ComposeGenerator.GenerateCompose(opts));
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(ComposeGenerator.GenerateEnv(opts));
        _out.ToString().Should().Contain("検証 OK").And.Contain("Dry-run のため");
    }

    [Fact]
    public async Task Deploy_Should_Run_Deployment_With_Resolved_Options()
    {
        var gateway = new Mock<IAzureDeploymentGateway>();
        var env = new Mock<ContainerAppManagedEnvironmentResource>().Object;
        gateway.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "myapp", "japaneast", It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(env);
        gateway.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "myapp", "japaneast", It.IsAny<DeployOptions>(), env, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerAppsDeployment("myapp-tt", "myapp-db", "myapp-redis", null));
        DeployOptions? received = null;

        var code = await Run(With("--app-name", "MyApp", "--tt-cpu", "2"), (opts, _) => { received = opts; return gateway.Object; });

        code.Should().Be(0);
        received!.AppName.Should().Be("myapp");
        received.TimetrackerCpu.Should().Be(2);
        gateway.Verify(g => g.EnsureResourceGroupAsync("sub", "rg", "japaneast", It.IsAny<CancellationToken>()));
        _out.ToString().Should().Contain("[SUCCESS] Container Apps デプロイ完了");
    }

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "--persist-db" }, true)]
    [InlineData(new[] { "--persist-db", "false" }, false)]
    public async Task Deploy_Should_Pass_Persist_Db_As_Specified(string[] extra, bool? expected)
    {
        var gateway = new Mock<IAzureDeploymentGateway>();
        DeployOptions? received = null;

        await Run(With(extra), (opts, _) => { received = opts; return gateway.Object; });

        received!.PersistDb.Should().Be(expected);
    }

    // 利用可能期限日の判定に使う時刻（UTC の時刻帯で 2026-10-15）
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private Task<int> RunAt(string[] args, CliApplication.GatewayFactory gatewayFactory) =>
        CliApplication.RunAsync(args, _out, _err, _dir, gatewayFactory, time: new FixedTime());

    private static (Mock<IAzureDeploymentGateway> Gateway, List<DeployOptions> Deployed) SucceedingGateway()
    {
        var gateway = new Mock<IAzureDeploymentGateway>();
        var env = new Mock<ContainerAppManagedEnvironmentResource>().Object;
        var deployed = new List<DeployOptions>();
        gateway.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "timetracker", "japaneast", It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(env);
        gateway.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "timetracker", "japaneast", Capture.In(deployed), env, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerAppsDeployment("timetracker-tt", "timetracker-db", "timetracker-redis", null));
        return (gateway, deployed);
    }

    [Fact]
    public async Task Deploy_Should_Record_The_Specified_Expiry_And_Show_It()
    {
        var (gateway, deployed) = SucceedingGateway();

        var code = await RunAt(With("--expires-on", "2026-12-01"), (_, _) => gateway.Object);

        code.Should().Be(0);
        deployed.Single().LifecycleTags.Should().Contain(DeploymentExpiry.ExpiresAtKey, "2026-12-01T23:59:59Z")
            .And.Contain(DeploymentExpiry.DeployedAtKey, "2026-10-15T12:00:00Z");
        _out.ToString().Should().Contain("利用可能期限: 2026-12-01。期限を過ぎると、TTDeployTool のデスクトップアプリが自動で削除します")
            .And.Contain("--expires-on <yyyy-MM-dd|none>");
    }

    [Fact]
    public async Task Deploy_Should_Record_The_Group_And_Show_It()
    {
        // docs/design/common/deployment-groups.md の 6
        var (gateway, deployed) = SucceedingGateway();

        var code = await RunAt(With("--group", " 研修 "), (_, _) => gateway.Object);

        code.Should().Be(0);
        deployed.Single().LifecycleTags.Should().Contain(DeploymentGroup.Key, "研修");
        _out.ToString().Should().Contain("グループ: 研修");
        TTDeployTool.Controller.Cli.CliArgs.HelpText.Should().Contain("--group <名前>");
    }

    [Fact]
    public async Task Deploy_Should_Not_Write_A_Group_When_Omitted()
    {
        var (gateway, deployed) = SucceedingGateway();

        await RunAt(Required, (_, _) => gateway.Object);

        deployed.Single().LifecycleTags.Should().NotContainKey(DeploymentGroup.Key);
        _out.ToString().Should().Contain("グループ: なし");
    }

    [Fact]
    public async Task Deploy_Should_Reject_A_Too_Long_Group_Without_Calling_Azure()
    {
        var code = await RunAt(With("--group", new string('x', 65)), NotCalled);

        code.Should().Be(1);
        (_out.ToString() + _err.ToString()).Should().Contain("group（グループ名）は 64 文字までにしてください（65 文字）。");
    }

    [Fact]
    public async Task Deploy_Should_Default_To_32_Days_Later()
    {
        var (gateway, deployed) = SucceedingGateway();

        await RunAt(Required, (_, _) => gateway.Object);

        deployed.Single().LifecycleTags.Should().Contain(DeploymentExpiry.ExpiresAtKey, "2026-11-16T23:59:59Z");
        _out.ToString().Should().Contain("利用可能期限: 2026-11-16（既定: 今日から 32 日後）。");
    }

    [Fact]
    public async Task Deploy_Should_Record_No_Expiry_Without_The_Auto_Delete_Notice()
    {
        var (gateway, deployed) = SucceedingGateway();

        await RunAt(With("--expires-on", "NONE"), (_, _) => gateway.Object);

        deployed.Single().LifecycleTags.Should().Contain(DeploymentExpiry.ExpiresAtKey, DeploymentExpiry.None);
        _out.ToString().Should().Contain("利用可能期限: 期限なし").And.NotContain("期限を過ぎると");
    }

    [Fact]
    public async Task Deploy_Should_Reject_A_Malformed_Expiry_Without_Calling_Azure()
    {
        var code = await RunAt(With("--expires-on", "12/01"), NotCalled);

        code.Should().Be(1);
        (_out.ToString() + _err.ToString()).Should().Contain("expires-on には yyyy-MM-dd の日付か none を指定してください: 12/01");
    }

    [Fact]
    public async Task Deploy_Should_Reject_A_Past_Expiry_Before_Changing_Azure()
    {
        var (gateway, deployed) = SucceedingGateway();

        var code = await RunAt(With("--expires-on", "2026-10-14"), (_, _) => gateway.Object);

        code.Should().Be(1);
        (_out.ToString() + _err.ToString()).Should().Contain("今日以降の日付を指定してください");
        gateway.Verify(g => g.EnsureResourceGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        deployed.Should().BeEmpty();
    }

    [Fact]
    public async Task Deploy_Should_Show_The_Storage_Notice_When_Persisting()
    {
        var gateway = new Mock<IAzureDeploymentGateway>();
        var env = new Mock<ContainerAppManagedEnvironmentResource>().Object;
        gateway.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "timetracker", "japaneast", It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(env);
        gateway.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "timetracker", "japaneast", It.IsAny<DeployOptions>(), env, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerAppsDeployment("timetracker-tt", "timetracker-db", "timetracker-redis", null));

        var code = await Run(With("--persist-db"), (_, _) => gateway.Object);

        code.Should().Be(0);
        gateway.Verify(g => g.EnsureDbStorageAsync("sub", "rg", "timetracker", "japaneast", env, It.IsAny<CancellationToken>()), Times.Once);
        _out.ToString().Should().Contain(DbPersistence.StorageNotice(ResourceNaming.StorageAccount("sub", "rg", "timetracker")));
    }

    [Fact]
    public async Task DryRun_Should_Warn_That_Persist_Db_Is_Ignored()
    {
        var code = await Run(With("--dry-run", "--persist-db"));

        code.Should().Be(0);
        _out.ToString().Should().Contain("[WARN] persist-db は Azure 向けの設定のため");
    }

    [Fact]
    public async Task Deploy_Should_Fail_When_Azure_Operation_Fails()
    {
        var gateway = new Mock<IAzureDeploymentGateway>();
        gateway.Setup(g => g.EnsureResourceGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("azure failed"));

        var code = await Run(Required, (_, _) => gateway.Object);

        code.Should().Be(1);
        _err.ToString().Should().Contain("[ERROR] エラー: azure failed");
        _out.ToString().Should().NotContain("[SUCCESS]");
    }

    [Fact]
    public async Task Deploy_Should_Use_Default_Gateway_Factory_When_Omitted()
    {
        // 既定のファクトリは --auth-mode から資格情報を作る。不明な認証モードなら Azure に接続する前に失敗する
        var code = await CliApplication.RunAsync(With("--auth-mode", "bogus"), _out, _err, _dir);

        code.Should().Be(1);
        _err.ToString().Should().Contain("不明な認証モード: bogus");
    }

    [Fact]
    public void DefaultGatewayFactory_Should_Create_AzureSdkExecutor()
    {
        var opts = new DeployOptions { Subscription = "sub", ResourceGroup = "rg", DbPassword = "x", TrackerPassword = "x", AuthMode = "azure-cli" };

        // 資格情報と ArmClient の作成だけで、Azure には接続しない
        CliApplication.DefaultGatewayFactory(opts, new SimpleLogger(false, new StringWriter(), new StringWriter()))
            .Should().BeOfType<AzureSdkExecutor>();
    }
}

/// <summary>出力先を省略した場合に Console に書くことの確認（Console を差し替えるため並列実行しない）。</summary>
[Collection(ConsoleRedirectCollection.Name)]
public class CliApplicationConsoleTests
{
    [Fact]
    public async Task Should_Write_To_Console_When_Writers_Are_Omitted()
    {
        using var console = new ConsoleRedirect();

        (await CliApplication.RunAsync(new[] { "--help" })).Should().Be(0);
        (await CliApplication.RunAsync(new[] { "--unknown" })).Should().Be(1);

        console.Output.Should().Contain(CliArgs.HelpText);
        console.Error.Should().Contain("--unknown");
    }
}
