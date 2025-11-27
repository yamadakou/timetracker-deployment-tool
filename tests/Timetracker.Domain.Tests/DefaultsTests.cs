using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Domain.Tests;

public class DefaultsTests
{
    [Fact]
    public void Defaults_Should_Have_Expected_AppName_And_Location()
    {
        Defaults.DefaultAppName.Should().Be("TimeTracker");
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
