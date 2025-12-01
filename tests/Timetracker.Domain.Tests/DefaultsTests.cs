using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Domain.Tests;

public class DefaultsTests
{
    [Fact]
    public void Defaults_Should_Have_Expected_AppName_And_Location()
    {
        Defaults.DefaultAppName.Should().Be("timetracker");
        Defaults.DefaultLocation.Should().Be("japaneast");
    }

    [Fact]
    public void Defaults_Should_Have_Fixed_DbUsers()
    {
        Defaults.DbUserFixedPostgres.Should().NotBeNullOrWhiteSpace();
        Defaults.DbUserFixedSqlServer.Should().NotBeNullOrWhiteSpace();
        // Typical values; adjust if DockerHub QuickStart specifies differently
        Defaults.DbUserFixedPostgres.Should().Be("postgres");
        Defaults.DbUserFixedSqlServer.Should().Be("sa");
    }
}

public class DeployOptionsTests
{
    [Fact]
    public void DeployOptions_AuthMode_Should_Default_To_Default()
    {
        // When creating DeployOptions, AuthMode should default to "default"
        var opts = new DeployOptions
        {
            Subscription = "test-sub",
            ResourceGroup = "test-rg",
            DbPassword = "testPassword123!",
            TrackerPassword = "testTracker123!"
        };

        opts.AuthMode.Should().Be("default");
    }

    [Theory]
    [InlineData("default")]
    [InlineData("azure-cli")]
    [InlineData("sp-env")]
    [InlineData("device-code")]
    [InlineData("managed-identity")]
    public void DeployOptions_AuthMode_Should_Accept_Valid_Values(string authMode)
    {
        var opts = new DeployOptions
        {
            Subscription = "test-sub",
            ResourceGroup = "test-rg",
            DbPassword = "testPassword123!",
            TrackerPassword = "testTracker123!",
            AuthMode = authMode
        };

        opts.AuthMode.Should().Be(authMode);
    }
}
