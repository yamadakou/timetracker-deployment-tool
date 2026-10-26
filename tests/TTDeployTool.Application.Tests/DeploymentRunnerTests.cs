using Azure.ResourceManager.AppContainers;
using FluentAssertions;
using Moq;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Application.Tests;

public class DeploymentRunnerTests
{
    private static DeployOptions Options() => new()
    {
        Subscription = "sub",
        ResourceGroup = "rg",
        Location = "japaneast",
        AppName = "demo",
        DbPassword = "DbPassw0rd!",
        TrackerPassword = "TrackerPass!",
    };

    private static (Mock<IAzureDeploymentGateway> gateway, ContainerAppManagedEnvironmentResource env) SetupGateway(List<string> calls)
    {
        var gateway = new Mock<IAzureDeploymentGateway>();
        var env = new Mock<ContainerAppManagedEnvironmentResource>().Object;
        gateway.Setup(g => g.EnsureResourceGroupAsync("sub", "rg", "japaneast", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("rg")).Returns(Task.CompletedTask);
        gateway.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "demo", "japaneast", It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("env")).ReturnsAsync(env);
        gateway.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(), env, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("apps"))
            .ReturnsAsync(new ContainerAppsDeployment("demo-tt", "demo-db", "demo-redis", "https://demo-tt.example.io"));
        return (gateway, env);
    }

    [Fact]
    public async Task Should_Deploy_In_Order_And_Return_Result()
    {
        var calls = new List<string>();
        var (gateway, _) = SetupGateway(calls);

        var result = await new DeploymentRunner(gateway.Object).RunAsync(Options(), cancellationToken: TestContext.Current.CancellationToken);

        calls.Should().Equal("rg", "env", "apps");
        (result with { Expiry = null }).Should().Be(new DeploymentResult("rg", "demo-env", "demo-tt", "demo-db", "demo-redis", "https://demo-tt.example.io"));
        result.Expiry!.Source.Should().Be(ExpirySource.Default);
    }

    [Fact]
    public async Task Should_Report_Progress_For_Each_Step()
    {
        var (gateway, _) = SetupGateway(new List<string>());
        var reported = new List<DeploymentProgress>();

        await new DeploymentRunner(gateway.Object).RunAsync(Options(), new SyncProgress(reported.Add), TestContext.Current.CancellationToken);

        reported.Select(p => p.Step).Should().Equal(DeploymentStep.ResourceGroup, DeploymentStep.Environment, DeploymentStep.ContainerApps);
        reported.Select(p => p.Index).Should().Equal(1, 2, 3);
        reported.Should().OnlyContain(p => p.Total == 3);
    }

    [Fact]
    public async Task Should_Create_The_Db_Storage_Before_The_Apps_When_Persisting()
    {
        var calls = new List<string>();
        var (gateway, env) = SetupGateway(calls);
        gateway.Setup(g => g.EnsureDbStorageAsync("sub", "rg", "demo", "japaneast", env, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("storage")).Returns(Task.CompletedTask);
        var reported = new List<DeploymentProgress>();
        var opts = Options();
        opts.PersistDb = true;

        var result = await new DeploymentRunner(gateway.Object).RunAsync(opts, new SyncProgress(reported.Add), TestContext.Current.CancellationToken);

        calls.Should().Equal("rg", "env", "storage", "apps");
        reported.Select(p => p.Step).Should().Equal(
            DeploymentStep.ResourceGroup, DeploymentStep.Environment, DeploymentStep.DbStorage, DeploymentStep.ContainerApps);
        reported.Should().OnlyContain(p => p.Total == 4);
        result.StorageAccountName.Should().Be(ResourceNaming.StorageAccount("sub", "rg", "demo"));
        gateway.Verify(g => g.CreateOrUpdateContainerAppAsync(
            "sub", "rg", "demo", "japaneast", It.Is<DeployOptions>(o => o.PersistDb == true), env, It.IsAny<CancellationToken>()), Times.Once);
        // 指定があれば、既存の DB アプリの状態は読まない
        gateway.Verify(g => g.ListContainerAppsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Should_Keep_The_Current_Persistence_When_Not_Specified(bool currentlyPersisted, bool expected)
    {
        var (gateway, _) = SetupGateway(new List<string>());
        gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ContainerAppInfo("demo-db", "env", new Dictionary<string, string>(), currentlyPersisted ? [DbPersistence.StorageName] : [])]);

        var result = await new DeploymentRunner(gateway.Object).RunAsync(Options(), cancellationToken: TestContext.Current.CancellationToken);

        (result.StorageAccountName != null).Should().Be(expected);
        gateway.Verify(g => g.CreateOrUpdateContainerAppAsync(
            "sub", "rg", "demo", "japaneast", It.Is<DeployOptions>(o => o.PersistDb == expected), It.IsAny<ContainerAppManagedEnvironmentResource>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_Not_Persist_A_New_Deployment_When_Not_Specified()
    {
        // リソースグループが無ければ（新規）、DB アプリも無いので永続化しない
        var (gateway, _) = SetupGateway(new List<string>());

        var result = await new DeploymentRunner(gateway.Object).RunAsync(Options(), cancellationToken: TestContext.Current.CancellationToken);

        result.StorageAccountName.Should().BeNull();
        gateway.Verify(g => g.ListContainerAppsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gateway.Verify(g => g.EnsureDbStorageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<ContainerAppManagedEnvironmentResource>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        yield return new object[] { (Action<DeployOptions>)(o => o.DbPassword = "short") };
        yield return new object[] { (Action<DeployOptions>)(o => o.AppName = "bad_name") };
        yield return new object[] { (Action<DeployOptions>)(o => o.TimetrackerCpu = -1) };
        yield return new object[] { (Action<DeployOptions>)(o => o.RedisMemoryGi = double.NaN) };
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task Should_Not_Call_Azure_When_Options_Are_Invalid(Action<DeployOptions> mutate)
    {
        // Strict: どのメソッドが呼ばれても失敗する
        var gateway = new Mock<IAzureDeploymentGateway>(MockBehavior.Strict);
        var invalid = Options();
        mutate(invalid);

        var act = () => new DeploymentRunner(gateway.Object).RunAsync(invalid, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Should_Pass_Cancellation_Token_To_Azure_Calls()
    {
        var (gateway, _) = SetupGateway(new List<string>());
        using var cts = new CancellationTokenSource();

        await new DeploymentRunner(gateway.Object).RunAsync(Options(), cancellationToken: cts.Token);

        gateway.Verify(g => g.EnsureResourceGroupAsync("sub", "rg", "japaneast", cts.Token));
        gateway.Verify(g => g.EnsureContainerAppEnvAsync("sub", "rg", "demo", "japaneast", It.IsAny<IReadOnlyDictionary<string, string>?>(), cts.Token));
        gateway.Verify(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(),
            It.IsAny<ContainerAppManagedEnvironmentResource>(), cts.Token));
    }

    // Progress<T> は SynchronizationContext 経由で非同期に通知するため、テストでは同期的に記録する
    private sealed class SyncProgress(Action<DeploymentProgress> report) : IProgress<DeploymentProgress>
    {
        public void Report(DeploymentProgress value) => report(value);
    }
}
