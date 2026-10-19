using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Domain.Tests;

public class ResourceNamingTests
{
    [Fact]
    public void Should_Build_Resource_Names_From_App_Name()
    {
        ResourceNaming.Environment("demo").Should().Be("demo-env");
        ResourceNaming.DbApp("demo").Should().Be("demo-db");
        ResourceNaming.RedisApp("demo").Should().Be("demo-redis");
        ResourceNaming.TimetrackerApp("demo").Should().Be("demo-tt");
    }

    [Fact]
    public void AppNameValidator_Should_Use_The_Same_Suffixes()
    {
        // アプリ名の上限（32 - 最長の接尾辞）は、実際に付ける接尾辞から算出されている必要がある
        AppNameValidator.ResourceSuffixes.Should().BeEquivalentTo(ResourceNaming.Suffixes);
        AppNameValidator.MaxLength.Should().Be(AppNameValidator.ContainerAppNameMaxLength - "-redis".Length);
    }
}
