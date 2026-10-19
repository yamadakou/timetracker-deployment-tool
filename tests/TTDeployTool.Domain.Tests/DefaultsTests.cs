using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Domain.Tests;

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

public class DefaultTimetrackerTagTests
{
    [Theory]
    [InlineData("postgresql", "11.0.1-linux-postgres")]
    [InlineData("sqlserver", "11.0.1-linux-mssql")]
    public void GetDefaultTimetrackerTag_Should_Match_DbType(string dbType, string expected)
    {
        Defaults.GetDefaultTimetrackerTag(dbType).Should().Be(expected);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Default_Tag_Should_Pass_Validation_For_Each_DbType(string dbType)
    {
        var opts = new DeployOptions
        {
            Subscription = "sub",
            ResourceGroup = "rg",
            DbType = dbType,
            DbPassword = "Passw0rd!x",
            TrackerPassword = "Passw0rd!y",
            TimetrackerTag = Defaults.GetDefaultTimetrackerTag(dbType),
        };
        var act = () => ComposeGenerator.Validate(opts);
        act.Should().NotThrow();
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

    [Fact]
    public void DeployOptions_TimetrackerSpec_Should_Default_To_1_5_vCPU_And_3_0Gi()
    {
        // 2Gi 以下ではサンプル DB の復元が OutOfMemoryException で失敗するため
        var opts = new DeployOptions
        {
            Subscription = "test-sub",
            ResourceGroup = "test-rg",
            DbPassword = "testPassword123!",
            TrackerPassword = "testTracker123!"
        };

        opts.TimetrackerCpu.Should().Be(1.5);
        opts.TimetrackerMemoryGi.Should().Be(3.0);
        // CLI（Program.cs）も同じ Defaults を既定値に使う
        Defaults.TimetrackerCpu.Should().Be(1.5);
        Defaults.TimetrackerMemoryGi.Should().Be(3.0);
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
