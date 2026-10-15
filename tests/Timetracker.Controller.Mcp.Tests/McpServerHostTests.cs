using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Moq;
using Timetracker.Application.Jobs;
using Timetracker.Common;
using Timetracker.Infrastructure.Azure;
using Xunit;

namespace Timetracker.Controller.Mcp.Tests;

public class McpServerHostTests
{
    [Fact]
    public void LoadSettings_Should_Return_Settings_Or_Write_The_Reason()
    {
        var error = new StringWriter();

        McpServerHost.LoadSettings(_ => null, error).Should().Be(new McpServerSettings());
        McpServerHost.LoadSettings(name => name == "TT_MCP_AUTH_MODE" ? "bad" : null, error).Should().BeNull();
        error.ToString().Should().StartWith("[ERROR] TT_MCP_AUTH_MODE 'bad' は無効です。");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_Should_Register_The_Tools_With_Annotations(bool verbose)
    {
        using var host = McpServerHost.Build([], new McpServerSettings { Verbose = verbose });

        var tools = host.Services.GetServices<McpServerTool>().ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool);

        tools.Keys.Should().BeEquivalentTo(
            "get_deployment_options", "validate_deployment", "generate_compose", "list_subscriptions",
            "plan_deployment", "start_deployment", "get_job_status", "list_jobs", "get_deployment_info");
        var annotations = tools.ToDictionary(t => t.Key, t => (
            t.Value.Annotations!.ReadOnlyHint, t.Value.Annotations.DestructiveHint, t.Value.Annotations.IdempotentHint, t.Value.Annotations.OpenWorldHint));
        annotations["get_deployment_options"].Should().Be((true, false, true, false));
        annotations["validate_deployment"].Should().Be((true, false, true, false));
        annotations["generate_compose"].Should().Be((false, false, true, false));
        annotations["list_subscriptions"].Should().Be((true, false, true, true));
        annotations["plan_deployment"].Should().Be((true, false, true, true));
        annotations["start_deployment"].Should().Be((false, true, false, true));
        annotations["get_job_status"].Should().Be((true, false, true, false));
        annotations["list_jobs"].Should().Be((true, false, true, false));
        annotations["get_deployment_info"].Should().Be((true, false, true, true));
        tools.Values.Should().OnlyContain(t => t.OutputSchema != null);
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("x").IsEnabled(LogLevel.Debug).Should().Be(verbose);
    }

    [Fact]
    public void Build_Should_Create_The_Subscription_Directory_With_The_Configured_Credential()
    {
        using var host = McpServerHost.Build([], new McpServerSettings());

        // 作成するだけでは Azure に接続しない
        host.Services.GetRequiredService<Func<ISubscriptionDirectory>>()().Should().BeOfType<AzureSubscriptionDirectory>();
        host.Services.GetRequiredService<Func<IDeploymentLogger, IAzureDeploymentGateway>>()(Mock.Of<IDeploymentLogger>())
            .Should().BeOfType<AzureSdkExecutor>();
        host.Services.GetRequiredService<ICallerContext>().Should().BeOfType<LocalCallerContext>();
        host.Services.GetRequiredService<IPlanStore>().Should().BeOfType<InMemoryPlanStore>();
        McpServerHost.Version.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void McpDeploymentLogger_Should_Forward_Each_Level()
    {
        var logger = new Mock<ILogger>();
        var sut = new McpDeploymentLogger(logger.Object);

        sut.Info("info");
        sut.Verbose("verbose");
        sut.Warn("warn");
        sut.Success("success");
        sut.Error("error");
        sut.Exception(new InvalidOperationException("boom"));

        foreach (var (level, times) in new[] { (LogLevel.Information, 2), (LogLevel.Debug, 1), (LogLevel.Warning, 1), (LogLevel.Error, 2) })
        {
            logger.Verify(l => l.Log(level, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(times));
        }
        logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.Is<Exception?>(e => e != null && e.Message == "boom"),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}
