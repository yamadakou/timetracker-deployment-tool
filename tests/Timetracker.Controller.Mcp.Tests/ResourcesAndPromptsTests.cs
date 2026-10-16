using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Moq;
using Timetracker.Application.Jobs;
using Timetracker.Common;
using Timetracker.Controller.Mcp.Prompts;
using Timetracker.Controller.Mcp.Resources;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;
using Xunit;

namespace Timetracker.Controller.Mcp.Tests;

public sealed class ResourcesAndPromptsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // README の内容から外れていないか（ロール名・権限名・プロバイダー名・主な制約）
    [Fact]
    public void Documents_Should_Contain_The_Key_Facts_From_The_Readme()
    {
        TimetrackerResources.GetConstraints().Should().Contain("検証環境").And.Contain("DB のデータは失われます")
            .And.Contain("Developer").And.Contain("サンプル DB").And.Contain("リソースグループは削除しません");
        TimetrackerResources.GetRbac().Should().Contain("Contributor").And.Contain("Container Apps Contributor")
            .And.Contain(AzurePermissions.ContainerAppDelete).And.Contain(AzurePermissions.EnvironmentDelete)
            .And.Contain("Microsoft.App").And.Contain("Microsoft.OperationalInsights");
    }

    [Fact]
    public async Task Deployment_Resource_Should_Return_The_Same_Content_As_Get_Deployment_Info()
    {
        var gateway = new Mock<IAzureDeploymentGateway>();
        gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceGroupInfo?)null);
        var services = new ServiceCollection()
            .AddSingleton(new McpServerSettings())
            .AddSingleton<ICallerContext, LocalCallerContext>()
            .AddSingleton<IAzureAccessPolicy, UnrestrictedAccessPolicy>()
            .AddSingleton<IPlanStore>(new InMemoryPlanStore(TimeProvider.System))
            .AddSingleton(new JobManager(new InMemoryJobStore(), TimeProvider.System, NullLogger<JobManager>.Instance))
            .AddSingleton<Func<IDeploymentLogger, IAzureDeploymentGateway>>(_ => gateway.Object)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var resources = new TimetrackerResources(services);

        var json = JsonDocument.Parse(await resources.GetDeployment("sub", "rg", "demo", Ct)).RootElement;
        var invalid = () => resources.GetDeployment("sub", "rg", "Bad--Name", Ct);

        json.GetProperty("exists").GetBoolean().Should().BeFalse();
        json.GetProperty("app_name").GetString().Should().Be("demo");
        await invalid.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public void Prompts_Should_Embed_The_Arguments_Or_Ask_The_User()
    {
        var prompts = new TimetrackerPrompts(new McpServerSettings());
        prompts.DeployTimetracker().Should().Contain("利用者に確認する（既定は postgresql").And.Contain("acknowledge_data_loss=true");
        prompts.DeployTimetracker("sqlserver").Should().Contain("「sqlserver」");
        prompts.LocalDryRun().Should().Contain("利用者に絶対パスを確認する").And.Contain("generate_compose");
        prompts.LocalDryRun("/work/tt").Should().Contain("「/work/tt」");
        TimetrackerPrompts.DeleteTimetracker().Should().Contain("（利用者に確認する）").And.Contain("confirm_app_name");
        TimetrackerPrompts.DeleteTimetracker("demo").Should().Contain("「demo」");
    }
}
