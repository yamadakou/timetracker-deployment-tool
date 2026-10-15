using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Controller.Mcp.Tests;

public class McpServerSettingsTests
{
    private static McpServerSettings Load(Dictionary<string, string> env) =>
        McpServerSettings.FromEnvironment(name => env.GetValueOrDefault(name));

    [Fact]
    public void Should_Use_Defaults_When_Nothing_Is_Set()
    {
        Load([]).Should().Be(new McpServerSettings());
        new McpServerSettings().AuthMode.Should().Be("azure-cli");
    }

    [Fact]
    public void Should_Read_All_Settings_And_Treat_Empty_Values_As_Unset()
    {
        var root = Path.Combine(Path.GetTempPath(), "ttmcp", "out") + Path.DirectorySeparatorChar;
        var settings = Load(new()
        {
            ["TT_MCP_AUTH_MODE"] = "SP-ENV",
            ["TT_MCP_DEFAULT_SUBSCRIPTION"] = "sub",
            ["TT_MCP_DEFAULT_RESOURCE_GROUP"] = "",
            ["TT_MCP_DB_PASSWORD"] = "DbPassw0rd!",
            ["TT_MCP_TRACKER_PASSWORD"] = "TrackerPass!",
            ["TT_MCP_OUTPUT_ROOT"] = root,
            ["TT_MCP_VERBOSE"] = "true",
        });

        settings.Should().Be(new McpServerSettings
        {
            AuthMode = "sp-env",
            DefaultSubscription = "sub",
            DefaultResourceGroup = null,
            DbPassword = "DbPassw0rd!",
            TrackerPassword = "TrackerPass!",
            // 末尾の区切り文字を除いて正規化する
            OutputRoot = Path.TrimEndingDirectorySeparator(root),
            Verbose = true,
        });
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("yes", false)]
    [InlineData("TRUE", false)]
    public void Should_Enable_Verbose_Only_For_True_Or_1(string value, bool expected)
    {
        Load(new() { ["TT_MCP_VERBOSE"] = value }).Verbose.Should().Be(expected);
    }

    [Fact]
    public void Should_Reject_An_Unknown_Auth_Mode()
    {
        var act = () => Load(new() { ["TT_MCP_AUTH_MODE"] = "password" });

        act.Should().Throw<ArgumentException>().WithMessage("TT_MCP_AUTH_MODE 'password' は無効です。*azure-cli*");
    }

    [Fact]
    public void Should_Reject_A_Relative_Output_Root()
    {
        var act = () => Load(new() { ["TT_MCP_OUTPUT_ROOT"] = "out" });

        act.Should().Throw<ArgumentException>().WithMessage("TT_MCP_OUTPUT_ROOT には絶対パスを指定してください: out");
    }
}

public class DeploymentParametersTests
{
    private static readonly McpServerSettings Settings = new()
    {
        DefaultSubscription = "default-sub", DefaultResourceGroup = "default-rg", DbPassword = "EnvDbPass1!", TrackerPassword = "EnvTracker1!",
    };

    [Fact]
    public void Should_Apply_Defaults_Like_The_Cli()
    {
        var resolved = new DeploymentParameters().Resolve(new McpServerSettings());

        var o = resolved.Options;
        (o.Subscription, o.ResourceGroup, o.Location, o.AppName, o.DbType, o.DbName, o.TimetrackerTag).Should().Be(
            ("", "", Defaults.DefaultLocation, Defaults.DefaultAppName, "postgresql", Defaults.DbName, Defaults.TimetrackerTagPostgres));
        (o.TimetrackerCpu, o.TimetrackerMemoryGi, o.DbCpu, o.DbMemoryGi, o.RedisCpu, o.RedisMemoryGi).Should().Be(
            (Defaults.TimetrackerCpu, Defaults.TimetrackerMemoryGi, Defaults.DbCpu, Defaults.DbMemoryGi, Defaults.RedisCpu, Defaults.RedisMemoryGi));
        o.AuthMode.Should().Be("azure-cli");
        (resolved.DbPasswordSource, resolved.TrackerPasswordSource).Should().Be((PasswordSource.None, PasswordSource.None));
    }

    [Fact]
    public void Should_Use_Arguments_Before_Server_Defaults()
    {
        var resolved = new DeploymentParameters
        {
            Subscription = "sub", ResourceGroup = "rg", Location = "eastus", AppName = "Demo", DbType = "sqlserver", DbName = "db",
            TtTag = "tag", DbPassword = "ArgDbPass1!", TrackerPassword = "ArgTracker1!",
            TtCpu = 2, TtMemoryGi = 4, DbCpu = 1.5, DbMemoryGi = 3, RedisCpu = 0.25, RedisMemoryGi = 0.5,
        }.Resolve(Settings);

        var o = resolved.Options;
        (o.Subscription, o.ResourceGroup, o.Location, o.AppName, o.DbType, o.DbName, o.TimetrackerTag).Should().Be(
            ("sub", "rg", "eastus", "demo", "sqlserver", "db", "tag"));
        (o.TimetrackerCpu, o.TimetrackerMemoryGi, o.DbCpu, o.DbMemoryGi, o.RedisCpu, o.RedisMemoryGi).Should().Be((2, 4, 1.5, 3, 0.25, 0.5));
        (o.DbPassword, o.TrackerPassword).Should().Be(("ArgDbPass1!", "ArgTracker1!"));
        (resolved.DbPasswordSource, resolved.TrackerPasswordSource).Should().Be((PasswordSource.Argument, PasswordSource.Argument));
    }

    [Fact]
    public void Should_Fall_Back_To_Server_Defaults_And_Environment_Passwords()
    {
        // 空白の引数は未指定として扱う
        var resolved = new DeploymentParameters { Subscription = " ", DbPassword = "" }.Resolve(Settings);

        (resolved.Options.Subscription, resolved.Options.ResourceGroup).Should().Be(("default-sub", "default-rg"));
        (resolved.Options.DbPassword, resolved.Options.TrackerPassword).Should().Be(("EnvDbPass1!", "EnvTracker1!"));
        (resolved.DbPasswordSource, resolved.TrackerPasswordSource).Should().Be((PasswordSource.Environment, PasswordSource.Environment));
    }
}
