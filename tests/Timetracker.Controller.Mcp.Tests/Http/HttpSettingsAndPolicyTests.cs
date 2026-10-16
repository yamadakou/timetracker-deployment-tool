using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Timetracker.Application.Jobs;
using Timetracker.Controller.Mcp.Prompts;
using Timetracker.Controller.Mcp.Tools;
using Xunit;

namespace Timetracker.Controller.Mcp.Tests.Http;

public class HttpSettingsAndPolicyTests
{
    private static readonly Dictionary<string, string?> Required = new()
    {
        ["TT_MCP_HTTP__TENANT_ID"] = HttpTestSupport.TenantId,
        ["TT_MCP_HTTP__CLIENT_ID"] = HttpTestSupport.ClientId,
        ["TT_MCP_HTTP__PUBLIC_URL"] = HttpTestSupport.PublicUrl,
        ["TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS"] = HttpTestSupport.AllowedGroup,
    };

    private static Func<string, string?> Env(params (string Name, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>(Required);
        foreach (var (name, value) in overrides)
            values[name] = value;
        return name => values.GetValueOrDefault(name);
    }

    // ---- McpHttpSettings

    [Fact]
    public void FromEnvironment_Should_Read_The_Http_Settings_With_Defaults()
    {
        var http = McpHttpSettings.FromEnvironment(Env());

        http.TenantId.Should().Be(HttpTestSupport.TenantId);
        http.ClientId.Should().Be(HttpTestSupport.ClientId);
        http.PublicUrl.Should().Be(new Uri(HttpTestSupport.PublicUrl));
        http.ServerIdentity.Should().Be("managed-identity");
        http.AllowedResourceGroups.Should().Equal(new AllowedResourceGroup("sub", "rg"));
        http.CanCreateResourceGroup.Should().BeFalse();
        http.Issuer.Should().Be($"https://login.microsoftonline.com/{HttpTestSupport.TenantId}/v2.0");
        http.Scope.Should().Be($"api://{HttpTestSupport.ClientId}/Mcp.Access");
    }

    [Fact]
    public void FromEnvironment_Should_Read_The_Optional_Settings()
    {
        var http = McpHttpSettings.FromEnvironment(Env(
            ("TT_MCP_HTTP__SERVER_IDENTITY", "SP-ENV"),
            ("TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS", " /subscriptions/s1/resourceGroups/a/ , /SUBSCRIPTIONS/s2/RESOURCEGROUPS/B ,"),
            ("TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP", "true")));

        http.ServerIdentity.Should().Be("sp-env");
        http.AllowedResourceGroups.Should().Equal(new AllowedResourceGroup("s1", "a"), new AllowedResourceGroup("s2", "B"));
        http.CanCreateResourceGroup.Should().BeTrue();
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    public void FromEnvironment_Should_Read_Whether_Resource_Groups_Can_Be_Created(string value, bool expected) =>
        McpHttpSettings.FromEnvironment(Env(("TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP", value))).CanCreateResourceGroup.Should().Be(expected);

    [Theory]
    [InlineData("TT_MCP_HTTP__TENANT_ID", null, "HTTP で起動するには TT_MCP_HTTP__TENANT_ID が必要です。")]
    [InlineData("TT_MCP_HTTP__CLIENT_ID", null, "HTTP で起動するには TT_MCP_HTTP__CLIENT_ID が必要です。")]
    [InlineData("TT_MCP_HTTP__PUBLIC_URL", null, "HTTP で起動するには TT_MCP_HTTP__PUBLIC_URL が必要です。")]
    [InlineData("TT_MCP_HTTP__PUBLIC_URL", "/mcp", "TT_MCP_HTTP__PUBLIC_URL には")]
    [InlineData("TT_MCP_HTTP__PUBLIC_URL", "ftp://mcp.example.com/mcp", "TT_MCP_HTTP__PUBLIC_URL には")]
    [InlineData("TT_MCP_HTTP__SERVER_IDENTITY", "azure-cli", "TT_MCP_HTTP__SERVER_IDENTITY 'azure-cli' は無効です。")]
    [InlineData("TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS", null, "HTTP で起動するには TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS が必要です。")]
    [InlineData("TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS", " , ", "TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS に、操作してよいリソースグループを 1 つ以上")]
    [InlineData("TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS", "/subscriptions/sub", "TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS の '/subscriptions/sub' は無効です。")]
    [InlineData("TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP", "yes", "TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP 'yes' は無効です。")]
    public void FromEnvironment_Should_Reject_Missing_Or_Invalid_Values(string name, string? value, string message)
    {
        var act = () => McpHttpSettings.FromEnvironment(Env((name, value)));

        act.Should().Throw<ArgumentException>().Which.Message.Should().StartWith(message);
    }

    [Fact]
    public void HasScope_Should_Find_Mcp_Access_In_The_Scp_Claim()
    {
        System.Security.Claims.ClaimsPrincipal User(params string[] scp) =>
            new(new System.Security.Claims.ClaimsIdentity(scp.Select(s => new System.Security.Claims.Claim("scp", s))));

        McpHttpSettings.HasScope(User("Mcp.Access")).Should().BeTrue();
        McpHttpSettings.HasScope(User("openid  Mcp.Access")).Should().BeTrue();
        McpHttpSettings.HasScope(User("openid", "Mcp.Access")).Should().BeTrue();
        McpHttpSettings.HasScope(User()).Should().BeFalse();
        McpHttpSettings.HasScope(User("mcp.access Mcp.AccessX")).Should().BeFalse();
    }

    [Fact]
    public void AllowedResourceGroup_Should_Match_Ignoring_Case()
    {
        var group = new AllowedResourceGroup("Sub", "RG");

        group.Matches("sub", "rg").Should().BeTrue();
        group.Matches("sub", "other").Should().BeFalse();
        group.Matches("other", "rg").Should().BeFalse();
        group.ToString().Should().Be("/subscriptions/Sub/resourceGroups/RG");
    }

    // ---- McpServerSettings（トランスポート）

    [Fact]
    public void ServerSettings_Should_Select_Http_By_Argument_Or_Environment()
    {
        var env = Env(("TT_MCP_DB_PASSWORD", "x"), ("TT_MCP_TRACKER_PASSWORD", "y"), ("TT_MCP_OUTPUT_ROOT", "/out"),
            ("TT_MCP_AUTH_MODE", "bad"), ("TT_MCP_DEFAULT_SUBSCRIPTION", "sub"), ("TT_MCP_DEFAULT_RESOURCE_GROUP", "rg"), ("TT_MCP_VERBOSE", "1"));

        var byArgument = McpServerSettings.FromEnvironment(env, ["--transport", "http"]);
        var byEnvironment = McpServerSettings.FromEnvironment(name => name == "TT_MCP_TRANSPORT" ? "HTTP" : env(name));

        foreach (var settings in new[] { byArgument, byEnvironment })
        {
            settings.Http.Should().NotBeNull();
            // パスワードと出力先の環境変数は読まない（全利用者で共有になるため）。TT_MCP_AUTH_MODE も使わない
            settings.DbPassword.Should().BeNull();
            settings.TrackerPassword.Should().BeNull();
            settings.OutputRoot.Should().BeNull();
            settings.AzureAuthMode.Should().Be("managed-identity");
            settings.DefaultSubscription.Should().Be("sub");
            settings.DefaultResourceGroup.Should().Be("rg");
            settings.Verbose.Should().BeTrue();
        }
    }

    [Fact]
    public void ServerSettings_Should_Use_Stdio_By_Default_Or_Reject_An_Unknown_Transport()
    {
        McpServerSettings.FromEnvironment(_ => null, ["--transport", "stdio"]).Should().Be(new McpServerSettings());
        new McpServerSettings().AzureAuthMode.Should().Be("azure-cli");

        var unknown = () => McpServerSettings.FromEnvironment(_ => null, ["--transport", "sse"]);
        var missing = () => McpServerSettings.FromEnvironment(_ => null, ["--transport"]);

        unknown.Should().Throw<ArgumentException>().WithMessage("トランスポート 'sse' は無効です。*");
        missing.Should().Throw<ArgumentException>().WithMessage("--transport の値*");
    }

    [Fact]
    public void LoadSettings_Should_Pass_The_Arguments()
    {
        var error = new StringWriter();

        McpServerHost.LoadSettings(Env(), error, ["--transport", "http"])!.Http.Should().NotBeNull();
        McpServerHost.LoadSettings(_ => null, error, ["--transport", "http"]).Should().BeNull();
        error.ToString().Should().StartWith("[ERROR] HTTP で起動するには TT_MCP_HTTP__TENANT_ID が必要です。");
    }

    // ---- IAzureAccessPolicy

    [Fact]
    public void UnrestrictedAccessPolicy_Should_Allow_Everything_With_The_Auth_Mode_Identity()
    {
        var policy = new UnrestrictedAccessPolicy(new McpServerSettings { AuthMode = "sp-env" });

        policy.Identity.Should().Be(new IdentityInfo("sp-env", "環境変数 AZURE_CLIENT_ID のサービスプリンシパル"));
        policy.CanCreateResourceGroup.Should().BeTrue();
        policy.ResourceGroupBlocker("any", "any").Should().BeNull();
        policy.Invoking(p => p.RequireAzureAccess()).Should().NotThrow();
    }

    [Theory]
    [InlineData("managed-identity", "MCP サーバーのマネージド ID（server 方式）")]
    [InlineData("sp-env", "MCP サーバーのサービスプリンシパル（server 方式）")]
    public void ServerAccessPolicy_Should_Show_The_Server_Identity(string identity, string display)
    {
        var settings = new McpServerSettings { Http = HttpTestSupport.Http(identity: identity) };

        HttpTestSupport.Policy(settings).Identity.Should().Be(new IdentityInfo("server", display));
    }

    [Fact]
    public void ServerAccessPolicy_Should_Require_The_Server_Role()
    {
        var settings = HttpTestSupport.Settings();

        HttpTestSupport.Policy(settings).Invoking(p => p.RequireAzureAccess()).Should().NotThrow();
        foreach (var policy in new[] { HttpTestSupport.Policy(settings, withRole: false), new ServerAccessPolicy(settings, HttpTestSupport.Accessor(null)) })
            policy.Invoking(p => p.RequireAzureAccess()).Should().Throw<McpException>()
                .WithMessage("このツールを使うには、MCP サーバーのアプリロール Timetracker.Deploy.Server が必要です。*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServerAccessPolicy_Should_Allow_Only_The_Configured_Resource_Groups(bool canCreate)
    {
        var policy = HttpTestSupport.Policy(HttpTestSupport.Settings(canCreate, "/subscriptions/sub/resourceGroups/rg,/subscriptions/sub/resourceGroups/rg2"));

        policy.CanCreateResourceGroup.Should().Be(canCreate);
        policy.ResourceGroupBlocker("SUB", "Rg2").Should().BeNull();
        policy.ResourceGroupBlocker("sub", "other").Should().Be(
            "リソースグループ other（サブスクリプション sub）は、このサーバーで操作できる範囲にありません。" +
            "操作できるのは /subscriptions/sub/resourceGroups/rg, /subscriptions/sub/resourceGroups/rg2 です（TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS）。");
    }

    [Fact]
    public void HttpCallerContext_Should_Identify_The_Caller_By_Tenant_And_Object_Id()
    {
        new HttpCallerContext(HttpTestSupport.Accessor(HttpTestSupport.User("user-a"))).CallerId.Should().Be($"{HttpTestSupport.TenantId}/user-a");
        HttpCallerContext.Id(null).Should().BeNull();
        HttpCallerContext.Id(new(new System.Security.Claims.ClaimsIdentity([new("tid", "t")]))).Should().BeNull();
        HttpCallerContext.Id(new(new System.Security.Claims.ClaimsIdentity([new("oid", "o")]))).Should().BeNull();

        var anonymous = () => new HttpCallerContext(HttpTestSupport.Accessor(null)).CallerId;
        anonymous.Should().Throw<InvalidOperationException>();
    }

    // ---- Prompts と instructions

    [Fact]
    public void Prompts_Should_Describe_The_Http_Server()
    {
        var prompts = new TimetrackerPrompts(HttpTestSupport.Settings());

        prompts.DeployTimetracker().Should().Contain("パスワードは引数で渡す必要がある").And.Contain("server.allowed_resource_groups");
        prompts.LocalDryRun("/ignored").Should().Contain("このサーバーはファイルを書き込まない").And.NotContain("/ignored")
            .And.NotContain("validate_deployment に target=compose");
        ServerInstructions.HttpText.Should().Contain("server 方式").And.Contain(ServerAccessPolicy.ServerRole);
    }

    // ---- McpServerHost

    [Fact]
    public async Task Create_Should_Build_The_Server_For_The_Transport()
    {
        using var stdio = McpServerHost.Create([], new McpServerSettings());
        await using var http = (WebApplication)McpServerHost.Create([], HttpTestSupport.Settings());

        stdio.Should().NotBeOfType<WebApplication>();
        stdio.Services.GetRequiredService<IAzureAccessPolicy>().Should().BeOfType<UnrestrictedAccessPolicy>();
        http.Services.GetRequiredService<IAzureAccessPolicy>().Should().BeOfType<ServerAccessPolicy>();
        http.Services.GetRequiredService<ICallerContext>().Should().BeOfType<HttpCallerContext>();
        var tools = http.Services.GetServices<McpServerTool>().ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool);
        tools.Should().HaveCount(11);
        tools["generate_compose"].InputSchema.GetProperty("properties").TryGetProperty("output_dir", out _).Should().BeFalse();
        tools["generate_compose"].Annotations!.ReadOnlyHint.Should().BeTrue();
    }

    [Theory]
    [InlineData("--urls", "http://localhost:5099")]
    [InlineData("--http_ports", "8081")]
    public async Task BuildHttp_Should_Keep_The_Configured_Address_And_Log_Level(string option, string value)
    {
        await using var app = McpServerHost.BuildHttp([option, value], HttpTestSupport.Settings() with { Verbose = true });

        app.Configuration[option.TrimStart('-')].Should().Be(value);
        app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("x")
            .IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug).Should().BeTrue();
    }

    [Fact]
    public void BuildHttp_Should_Require_The_Http_Settings()
    {
        var act = () => McpServerHost.BuildHttp([], new McpServerSettings());

        act.Should().Throw<ArgumentException>();
    }
}
