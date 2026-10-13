using Azure.Core;
using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Timetracker.Controller.Cli.Tests;

/// <summary>
/// Dry-run の compose と Azure Container Apps の構成が食い違わないことを検証する。
/// </summary>
public class ComposeAzureConsistencyTests
{
    private static readonly ResourceIdentifier EnvId =
        new("/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/app-env");

    private static DeployOptions CreateOptions(string dbType) => new()
    {
        Subscription = "sub",
        ResourceGroup = "rg",
        DbType = dbType,
        DbPassword = "Passw0rd!x",
        TrackerPassword = "Passw0rd!y",
        TimetrackerTag = Defaults.GetDefaultTimetrackerTag(dbType),
    };

    private static YamlMappingNode Service(string compose, string name)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(compose));
        var services = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["services"];
        return (YamlMappingNode)services[name];
    }

    private static string Image(YamlMappingNode svc) => ((YamlScalarNode)svc["image"]).Value!;

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Timetracker_Environment_Variable_Names_Should_Match(string dbType)
    {
        var o = CreateOptions(dbType);
        var compose = ComposeGenerator.GenerateCompose(o);
        var composeKeys = ((YamlMappingNode)Service(compose, "timetracker")["environment"]).Children.Keys
            .Select(k => ((YamlScalarNode)k).Value);

        var azureKeys = ContainerAppSpecBuilder.BuildTimetrackerApp(o, "japaneast", EnvId, "app-db", "app-redis")
            .Template.Containers[0].Env.Select(e => e.Name);

        composeKeys.Should().BeEquivalentTo(azureKeys);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Images_Should_Match(string dbType)
    {
        var o = CreateOptions(dbType);
        var compose = ComposeGenerator.GenerateCompose(o);

        Image(Service(compose, "timetracker")).Should().Be(
            ContainerAppSpecBuilder.BuildTimetrackerApp(o, "japaneast", EnvId, "app-db", "app-redis").Template.Containers[0].Image);
        Image(Service(compose, "db")).Should().Be(
            ContainerAppSpecBuilder.BuildDbApp(o, "japaneast", EnvId).Template.Containers[0].Image);
        Image(Service(compose, "redis")).Should().Be(
            ContainerAppSpecBuilder.BuildRedisApp(o, "japaneast", EnvId).Template.Containers[0].Image);
    }

    [Fact]
    public void Images_Should_Not_Use_Latest_Tag_For_Redis()
    {
        ContainerImages.Redis.Should().NotEndWith(":latest");
    }
}
