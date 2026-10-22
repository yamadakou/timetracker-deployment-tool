using Azure.ResourceManager.AppContainers;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Application.Tests;

/// <summary>計画と実行での利用可能期限（docs/design/desktop-jobs.md の 6.1）。</summary>
public class DeploymentExpiryPlanTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero));
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];
    private readonly ContainerAppManagedEnvironmentResource _env = new Mock<ContainerAppManagedEnvironmentResource>().Object;
    private DeployOptions? _sent;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeploymentExpiryPlanTests()
    {
        _time.SetLocalTimeZone(Tokyo);
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps);
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _gateway.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "demo", "japaneast", It.IsAny<CancellationToken>())).ReturnsAsync(_env);
        _gateway.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(), _env, It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, DeployOptions, ContainerAppManagedEnvironmentResource, CancellationToken>((_, _, _, _, o, _, _) => _sent = o)
            .ReturnsAsync(new ContainerAppsDeployment("demo-tt", "demo-db", "demo-redis", null));
    }

    private static DeployOptions Opts(string? expiresOn = null) => new()
    {
        Subscription = "sub", ResourceGroup = "rg", AppName = "demo", DbPassword = "DbPassw0rd!", TrackerPassword = "TrackerPass!",
        Location = "japaneast", ExpiresOn = expiresOn,
    };

    private void Deployed(string? ttExpiry, string? dbExpiry = null)
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo("id", "demo-env", "japaneast", ManagedTags.For("demo")));
        _apps.Add(new ContainerAppInfo("demo-tt", "id", Tags(ttExpiry)));
        _apps.Add(new ContainerAppInfo("demo-db", "id", Tags(dbExpiry)));
    }

    private static Dictionary<string, string> Tags(string? expiry)
    {
        var tags = new Dictionary<string, string>(ManagedTags.For("demo"));
        if (expiry != null)
            tags[DeploymentExpiry.ExpiresAtKey] = expiry;
        return tags;
    }

    private Task<DeploymentPlan> Plan(string? expiresOn = null) => new DeploymentPlanner(_gateway.Object, _time).PlanAsync(Opts(expiresOn), Ct);

    private Task<DeploymentResult> Run(string? expiresOn = null) => new DeploymentRunner(_gateway.Object, _time).RunAsync(Opts(expiresOn), cancellationToken: Ct);

    [Fact]
    public async Task A_New_Deployment_Should_Expire_In_32_Days_By_Default()
    {
        var plan = await Plan();
        var result = await Run();

        var expected = new DateTimeOffset(2026, 11, 23, 23, 59, 59, TimeSpan.FromHours(9));
        plan.Expiry.Should().Be(new ExpiryDecision(expected, ExpirySource.Default));
        result.Expiry.Should().Be(plan.Expiry);
        _sent!.LifecycleTags.Should().Equal(new Dictionary<string, string>
        {
            ["tt-expires-at"] = "2026-11-23T14:59:59Z", ["tt-deployed-at"] = "2026-10-21T20:00:00Z",
        });
        _gateway.Verify(g => g.ListContainerAppsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "リソースグループが無ければ、Container App も無い");
    }

    [Fact]
    public async Task A_Redeployment_Should_Inherit_The_Expiry_From_The_Apps()
    {
        // Timetracker に無ければ、DB アプリの期限を使う
        Deployed(ttExpiry: null, dbExpiry: "none");

        var plan = await Plan();
        await Run();

        plan.Expiry.Should().Be(new ExpiryDecision(null, ExpirySource.Inherited));
        plan.Warnings.Should().NotContain(w => w.Contains("利用可能期限"));
        _sent!.LifecycleTags![DeploymentExpiry.ExpiresAtKey].Should().Be("none");
    }

    [Fact]
    public async Task A_Specified_Date_Should_Replace_The_Current_Expiry()
    {
        Deployed(ttExpiry: "2026-11-01T14:59:59Z");

        var plan = await Plan("2026-12-31");
        await Run("2026-12-31");

        plan.Expiry.Should().Be(new ExpiryDecision(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(9)), ExpirySource.Specified));
        _sent!.LifecycleTags![DeploymentExpiry.ExpiresAtKey].Should().Be("2026-12-31T14:59:59Z");
    }

    [Fact]
    public async Task A_Past_Date_Should_Block_The_Plan_And_Stop_The_Run_Before_Changing_Azure()
    {
        var plan = await Plan("2026-10-21");
        var run = () => Run("2026-10-21");

        plan.Blockers.Should().Contain("利用可能期限日（2026-10-21）は、今日以降の日付を指定してください。");
        plan.CanRun.Should().BeFalse();
        await run.Should().ThrowAsync<ArgumentException>().WithMessage("利用可能期限日（2026-10-21）は、今日以降の日付を指定してください。");
        _gateway.Verify(g => g.EnsureResourceGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_Inherited_Expiry_That_Has_Passed_Should_Be_Warned()
    {
        Deployed(ttExpiry: "2026-10-20T14:59:59Z");

        var plan = await Plan();

        plan.Warnings.Should().Contain("今の利用可能期限（2026-10-20（今の期限を引き継ぎます））を過ぎています。期限日を指定し直してください。");
        plan.CanRun.Should().BeTrue();
    }

    [Fact]
    public void Lifecycle_Tags_Should_Be_Found_In_Order()
    {
        var apps = new List<ContainerAppInfo>
        {
            new("demo-redis", "id", Tags("redis")), new("demo-db", "id", Tags(null)), new("other-tt", "id", Tags("other")),
        };

        DeploymentPlanner.CurrentLifecycleTags(apps, "demo")![DeploymentExpiry.ExpiresAtKey].Should().Be("redis");
        DeploymentPlanner.CurrentLifecycleTags([], "demo").Should().BeNull();
    }
}
