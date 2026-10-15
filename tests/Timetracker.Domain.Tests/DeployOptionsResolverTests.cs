using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Domain.Tests;

public class DeployOptionsResolverTests
{
    private static DeployInput RequiredOnly() => new()
    {
        Subscription = "sub",
        ResourceGroup = "rg",
        DbPassword = "DbPassw0rd!",
        TrackerPassword = "TrackerPass!",
    };

    [Fact]
    public void Should_Apply_Defaults_When_Not_Specified()
    {
        var opts = DeployOptionsResolver.Resolve(RequiredOnly());

        opts.Subscription.Should().Be("sub");
        opts.ResourceGroup.Should().Be("rg");
        opts.DbPassword.Should().Be("DbPassw0rd!");
        opts.TrackerPassword.Should().Be("TrackerPass!");
        opts.Location.Should().Be(Defaults.DefaultLocation);
        opts.AppName.Should().Be(Defaults.DefaultAppName);
        opts.DbType.Should().Be("postgresql");
        opts.DbName.Should().Be(Defaults.DbName);
        opts.TimetrackerTag.Should().Be(Defaults.TimetrackerTagPostgres);
        opts.AuthMode.Should().Be("default");
        opts.TimetrackerCpu.Should().Be(Defaults.TimetrackerCpu);
        opts.TimetrackerMemoryGi.Should().Be(Defaults.TimetrackerMemoryGi);
        opts.DbCpu.Should().Be(Defaults.DbCpu);
        opts.DbMemoryGi.Should().Be(Defaults.DbMemoryGi);
        opts.RedisCpu.Should().Be(Defaults.RedisCpu);
        opts.RedisMemoryGi.Should().Be(Defaults.RedisMemoryGi);
        opts.DryRun.Should().BeFalse();
        opts.Verbose.Should().BeFalse();
    }

    [Fact]
    public void Should_Use_Specified_Values()
    {
        var opts = DeployOptionsResolver.Resolve(RequiredOnly() with
        {
            Location = "eastus",
            AppName = "demo",
            DbType = "sqlserver",
            DbName = "ttdb",
            TimetrackerTag = "7.0-linux-mssql",
            AuthMode = "azure-cli",
            TimetrackerCpu = 2.0,
            TimetrackerMemoryGi = 4.0,
            DbCpu = 0.5,
            DbMemoryGi = 1.0,
            RedisCpu = 0.25,
            RedisMemoryGi = 0.5,
            DryRun = true,
            Verbose = true,
        });

        opts.Location.Should().Be("eastus");
        opts.AppName.Should().Be("demo");
        opts.DbType.Should().Be("sqlserver");
        opts.DbName.Should().Be("ttdb");
        opts.TimetrackerTag.Should().Be("7.0-linux-mssql");
        opts.AuthMode.Should().Be("azure-cli");
        opts.TimetrackerCpu.Should().Be(2.0);
        opts.TimetrackerMemoryGi.Should().Be(4.0);
        opts.DbCpu.Should().Be(0.5);
        opts.DbMemoryGi.Should().Be(1.0);
        opts.RedisCpu.Should().Be(0.25);
        opts.RedisMemoryGi.Should().Be(0.5);
        opts.DryRun.Should().BeTrue();
        opts.Verbose.Should().BeTrue();
    }

    [Fact]
    public void Should_Use_Default_Tag_For_Specified_Db_Type()
    {
        var opts = DeployOptionsResolver.Resolve(RequiredOnly() with { DbType = "sqlserver" });

        opts.TimetrackerTag.Should().Be(Defaults.TimetrackerTagSqlServer);
    }

    [Fact]
    public void Should_Lowercase_App_Name()
    {
        DeployOptionsResolver.Resolve(RequiredOnly() with { AppName = "MyApp" }).AppName.Should().Be("myapp");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_Treat_Blank_Values_As_Not_Specified(string blank)
    {
        var opts = DeployOptionsResolver.Resolve(RequiredOnly() with
        {
            Location = blank,
            AppName = blank,
            DbType = blank,
            TimetrackerTag = blank,
            AuthMode = blank,
        });

        opts.Location.Should().Be(Defaults.DefaultLocation);
        opts.AppName.Should().Be(Defaults.DefaultAppName);
        opts.DbType.Should().Be("postgresql");
        opts.TimetrackerTag.Should().Be(Defaults.TimetrackerTagPostgres);
        opts.AuthMode.Should().Be("default");
    }

    [Fact]
    public void Should_Leave_Missing_Required_Values_Empty_For_Validation()
    {
        var opts = DeployOptionsResolver.Resolve(new DeployInput());

        opts.Subscription.Should().BeEmpty();
        opts.ResourceGroup.Should().BeEmpty();
        opts.DbPassword.Should().BeEmpty();
        opts.TrackerPassword.Should().BeEmpty();
        var validate = () => ComposeGenerator.Validate(opts);
        validate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Defaults_Should_Match_DeployOptions_Defaults()
    {
        // DeployOptions を直接作った場合と既定値がずれないこと
        var direct = new DeployOptions { Subscription = "sub", ResourceGroup = "rg", DbPassword = "DbPassw0rd!", TrackerPassword = "TrackerPass!" };
        var resolved = DeployOptionsResolver.Resolve(RequiredOnly());

        resolved.Should().BeEquivalentTo(direct);
    }
}
