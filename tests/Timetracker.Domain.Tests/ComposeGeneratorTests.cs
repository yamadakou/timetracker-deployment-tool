using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Domain.Tests;

public class ComposeGeneratorTests
{
    private DeployOptions BaseOptions() => new DeployOptions
    {
        Subscription = "00000000-0000-0000-0000-000000000000",
        ResourceGroup = "rg-test",
        TrackerPassword = "AppLoginP@ss!",
        DbPassword = "Str0ngP@ssw0rd!",
        // defaults: location=japaneast, app=TimeTracker, dbType=postgres, dbName=timetracker, ttTag=7.0-linux-postgres
    };

    [Fact]
    public void GenerateCompose_Postgres_Should_Contain_Expected_Services_And_Tag()
    {
        var opts = BaseOptions();
        opts.DbType = "postgres";
        opts.TimetrackerTag = "1.2.3";

        var compose = ComposeGenerator.GenerateCompose(opts);

        compose.Should().Contain("version: '3.9'");
        compose.Should().Contain("services:");
        compose.Should().Contain("timetracker:");
        compose.Should().Contain("image: densocreate/timetracker:1.2.3");
        compose.Should().Contain("db:");
        compose.Should().Contain("image: postgres:16-alpine");
        compose.Should().Contain("redis:");
        compose.Should().Contain("image: redis:7-alpine");

        compose.Should().Contain("DB_HOST=db");
        compose.Should().Contain("DB_PORT=${DB_PORT}");
        compose.Should().Contain("DB_USER=${DB_USER}");
        compose.Should().Contain("DB_PASSWORD=${DB_PASSWORD}");
        compose.Should().Contain("DB_NAME=${DB_NAME}");
        compose.Should().Contain("APP_PASSWORD=${TIMETRACKER_PASSWORD}");
    }

    [Fact]
    public void GenerateCompose_SqlServer_Should_Use_Mssql_Image_And_SA_PasswordEnv()
    {
        var opts = BaseOptions();
        opts.DbType = "sqlserver";
        var compose = ComposeGenerator.GenerateCompose(opts);

        compose.Should().Contain("image: mcr.microsoft.com/mssql/server:2022-latest");
        compose.Should().Contain("SA_PASSWORD=${DB_PASSWORD}");
        compose.Should().Contain("'1433:1433'");
    }

    [Fact]
    public void GenerateEnv_Postgres_Should_Set_Fixed_User_And_Port()
    {
        var opts = BaseOptions();
        opts.DbType = "postgres";

        var env = ComposeGenerator.GenerateEnv(opts);

        env.Should().Contain("DB_PORT=5432");
        env.Should().Contain($"DB_USER={Defaults.DbUserFixedPostgres}");
        env.Should().Contain($"DB_PASSWORD={opts.DbPassword}");
        env.Should().Contain($"DB_NAME={opts.DbName}");
        env.Should().Contain($"TIMETRACKER_PASSWORD={opts.TrackerPassword}");
    }

    [Fact]
    public void GenerateEnv_SqlServer_Should_Set_Fixed_User_And_Port()
    {
        var opts = BaseOptions();
        opts.DbType = "sqlserver";

        var env = ComposeGenerator.GenerateEnv(opts);

        env.Should().Contain("DB_PORT=1433");
        env.Should().Contain($"DB_USER={Defaults.DbUserFixedSqlServer}");
        env.Should().Contain($"DB_PASSWORD={opts.DbPassword}");
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void Validate_Valid_Options_Should_Not_Throw(string dbType)
    {
        var opts = BaseOptions();
        opts.DbType = dbType;
        opts.TimetrackerTag = "7.0-linux-postgres";

        var act = () => ComposeGenerator.Validate(opts);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Should_Throw_When_DbType_Invalid()
    {
        var opts = BaseOptions();
        opts.DbType = "mysql"; // unsupported
        var act = () => ComposeGenerator.Validate(opts);
        act.Should().Throw<ArgumentException>().WithMessage("*postgres または sqlserver*");
    }

    [Fact]
    public void Validate_Should_Throw_When_TT_Tag_Empty()
    {
        var opts = BaseOptions();
        opts.TimetrackerTag = "";
        var act = () => ComposeGenerator.Validate(opts);
        act.Should().Throw<ArgumentException>().WithMessage("*tt-tag*");
    }

    [Fact]
    public void Validate_Should_Throw_When_Passwords_Too_Short()
    {
        var opts = BaseOptions();
        opts.TrackerPassword = "short";
        var act1 = () => ComposeGenerator.Validate(opts);
        act1.Should().Throw<ArgumentException>().WithMessage("*tracker-password*");

        opts = BaseOptions();
        opts.DbPassword = "short";
        var act2 = () => ComposeGenerator.Validate(opts);
        act2.Should().Throw<ArgumentException>().WithMessage("*db-password*");
    }
}
