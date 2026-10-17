using Azure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using ModelContextProtocol;
using Timetracker.Common;
using Timetracker.Controller.Mcp.Prompts;
using Timetracker.Controller.Mcp.Tools;
using Xunit;

namespace Timetracker.Controller.Mcp.Tests.Http;

/// <summary>user 方式（利用者本人の権限。詳細設計 Phase 4b）の設定・方式の選び方・資格情報。</summary>
public class UserModeSettingsAndPolicyTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] values)
    {
        var all = new Dictionary<string, string?>
        {
            ["TT_MCP_HTTP__TENANT_ID"] = HttpTestSupport.TenantId,
            ["TT_MCP_HTTP__CLIENT_ID"] = HttpTestSupport.ClientId,
            ["TT_MCP_HTTP__PUBLIC_URL"] = HttpTestSupport.PublicUrl,
        };
        foreach (var (name, value) in values)
            all[name] = value;
        return name => all.GetValueOrDefault(name);
    }

    private static readonly (string, string?) AllowedGroup = ("TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS", HttpTestSupport.AllowedGroup);

    // ---- McpHttpSettings

    [Fact]
    public void FromEnvironment_Should_Use_Only_The_Server_Mode_By_Default()
    {
        var http = McpHttpSettings.FromEnvironment(Env(AllowedGroup));

        http.IdentityModes.Should().Equal("server");
        http.DefaultIdentityMode.Should().Be("server");
        http.ServerModeEnabled.Should().BeTrue();
        http.UserModeEnabled.Should().BeFalse();
        http.UserClientCredential.Should().Be("managed-identity");
        http.ClientSecret.Should().BeNull();
    }

    [Fact]
    public void FromEnvironment_Should_Not_Require_Allowed_Resource_Groups_For_The_User_Mode_Only()
    {
        var http = McpHttpSettings.FromEnvironment(Env(("TT_MCP_HTTP__IDENTITY_MODES", "user"), AllowedGroup));

        http.IdentityModes.Should().Equal("user");
        http.DefaultIdentityMode.Should().Be("user");
        http.ServerModeEnabled.Should().BeFalse();
        // user 方式は利用者本人の権限で操作するため、指定しても使わない
        http.AllowedResourceGroups.Should().BeEmpty();
        McpHttpSettings.FromEnvironment(Env(("TT_MCP_HTTP__IDENTITY_MODES", "user"))).AllowedResourceGroups.Should().BeEmpty();
    }

    [Fact]
    public void FromEnvironment_Should_Read_Both_Modes_And_The_Default()
    {
        var http = McpHttpSettings.FromEnvironment(Env(
            ("TT_MCP_HTTP__IDENTITY_MODES", " Server , USER ,server"), ("TT_MCP_HTTP__DEFAULT_IDENTITY_MODE", "User"),
            ("TT_MCP_HTTP__USER_CLIENT_CREDENTIAL", "Client-Secret"), ("TT_MCP_HTTP__CLIENT_SECRET", "s3cret"), AllowedGroup));

        http.IdentityModes.Should().Equal("server", "user");
        http.DefaultIdentityMode.Should().Be("user");
        http.UserClientCredential.Should().Be("client-secret");
        http.ClientSecret.Should().Be("s3cret");
    }

    [Theory]
    [InlineData("admin", "TT_MCP_HTTP__IDENTITY_MODES 'admin' は無効です。*")]
    [InlineData(",", "TT_MCP_HTTP__IDENTITY_MODES ',' は無効です。*")]
    public void FromEnvironment_Should_Reject_Invalid_Modes(string modes, string message)
    {
        var act = () => McpHttpSettings.FromEnvironment(Env(("TT_MCP_HTTP__IDENTITY_MODES", modes), AllowedGroup));

        act.Should().Throw<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public void FromEnvironment_Should_Reject_A_Default_Mode_That_Is_Not_Enabled()
    {
        var act = () => McpHttpSettings.FromEnvironment(Env(("TT_MCP_HTTP__DEFAULT_IDENTITY_MODE", "user"), AllowedGroup));

        act.Should().Throw<ArgumentException>().WithMessage("TT_MCP_HTTP__DEFAULT_IDENTITY_MODE 'user' は、TT_MCP_HTTP__IDENTITY_MODES（server）のどれかにしてください。");
    }

    [Fact]
    public void FromEnvironment_Should_Require_The_Allowed_Resource_Groups_When_The_Server_Mode_Is_Enabled()
    {
        var act = () => McpHttpSettings.FromEnvironment(Env(("TT_MCP_HTTP__IDENTITY_MODES", "user,server")));

        act.Should().Throw<ArgumentException>().WithMessage("HTTP で起動するには TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS が必要です。");
    }

    [Fact]
    public void FromEnvironment_Should_Require_The_Client_Secret_Only_For_The_User_Mode()
    {
        var missing = () => McpHttpSettings.FromEnvironment(Env(("TT_MCP_HTTP__IDENTITY_MODES", "user"), ("TT_MCP_HTTP__USER_CLIENT_CREDENTIAL", "client-secret")));
        var invalid = () => McpHttpSettings.FromEnvironment(Env(("TT_MCP_HTTP__IDENTITY_MODES", "user"), ("TT_MCP_HTTP__USER_CLIENT_CREDENTIAL", "certificate")));

        missing.Should().Throw<ArgumentException>().WithMessage("HTTP で起動するには TT_MCP_HTTP__CLIENT_SECRET が必要です。");
        invalid.Should().Throw<ArgumentException>().WithMessage("TT_MCP_HTTP__USER_CLIENT_CREDENTIAL 'certificate' は無効です。*");
        // server 方式だけなら使わないため、読まない
        McpHttpSettings.FromEnvironment(Env(("TT_MCP_HTTP__USER_CLIENT_CREDENTIAL", "client-secret"), ("TT_MCP_HTTP__CLIENT_SECRET", "s3cret"), AllowedGroup))
            .ClientSecret.Should().BeNull();
    }

    [Fact]
    public void ToString_Should_Not_Show_The_Client_Secret()
    {
        var withSecret = HttpTestSupport.Http(modes: "user", secret: "s3cret").ToString();
        var withoutSecret = HttpTestSupport.Http().ToString();

        withSecret.Should().NotContain("s3cret").And.Contain("ClientSecret = ********").And.Contain("IdentityModes = [user]");
        withoutSecret.Should().Contain("ClientSecret = null").And.Contain(HttpTestSupport.AllowedGroup);
    }

    // ---- HttpAccessPolicy: 方式の選び方

    [Theory]
    [InlineData("server", new[] { HttpAccessPolicy.ServerRole }, new[] { "server" })]
    [InlineData("server", new[] { HttpAccessPolicy.UserRole }, new string[0])]
    [InlineData("user", new[] { HttpAccessPolicy.UserRole }, new[] { "user" })]
    [InlineData("user", new[] { HttpAccessPolicy.ServerRole }, new[] { "user" })]
    [InlineData("server,user", new[] { HttpAccessPolicy.ServerRole }, new[] { "server", "user" })]
    [InlineData("server,user", new[] { HttpAccessPolicy.UserRole }, new[] { "user" })]
    [InlineData("server,user", new string[0], new string[0])]
    public void CallerIdentityModes_Should_Follow_The_Enabled_Modes_And_The_Roles(string modes, string[] roles, string[] expected)
    {
        var policy = HttpTestSupport.PolicyWithToken(HttpTestSupport.Settings(modes: modes), roles: roles);

        policy.CallerIdentityModes.Should().Equal(expected);
        policy.IdentityModes.Should().Equal(modes.Split(','));
    }

    [Fact]
    public void CallerIdentityModes_Should_Be_Empty_Without_A_Request()
    {
        new HttpAccessPolicy(HttpTestSupport.Settings(modes: "server,user"), HttpTestSupport.Accessor(null)).CallerIdentityModes.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_Should_Use_The_Default_Mode_Or_The_Mode_The_Caller_Can_Use()
    {
        var settings = HttpTestSupport.Settings(modes: "server,user");

        HttpTestSupport.PolicyWithToken(settings, roles: HttpAccessPolicy.ServerRole).Resolve(null).Mode.Should().Be("server");
        HttpTestSupport.PolicyWithToken(settings, roles: HttpAccessPolicy.ServerRole).DefaultIdentity.AuthMode.Should().Be("server");
        // Timetracker.Deploy.User だけを持つ利用者は、指定しなくても user 方式になる
        HttpTestSupport.PolicyWithToken(settings, roles: HttpAccessPolicy.UserRole).Resolve(null).Mode.Should().Be("user");
        HttpTestSupport.PolicyWithToken(settings, roles: HttpAccessPolicy.UserRole).DefaultIdentity
            .Should().Be(new IdentityInfo("user", "サインインした利用者本人の権限（user 方式）"));
        // ロールが無ければ、サーバーの既定を表示する
        HttpTestSupport.PolicyWithToken(settings).DefaultIdentity.AuthMode.Should().Be("server");
        HttpTestSupport.PolicyWithToken(HttpTestSupport.Settings(modes: "server,user", defaultMode: "user"), roles: HttpAccessPolicy.ServerRole)
            .Resolve(null).Mode.Should().Be("user");
    }

    [Fact]
    public void Resolve_Should_Use_The_Requested_Mode()
    {
        var policy = HttpTestSupport.PolicyWithToken(HttpTestSupport.Settings(modes: "server,user"), roles: HttpAccessPolicy.ServerRole);

        policy.Resolve("USER").Mode.Should().Be("user");
        policy.Resolve("server").Mode.Should().Be("server");
    }

    [Fact]
    public void Resolve_Should_Reject_An_Unknown_Or_Disabled_Mode()
    {
        var policy = HttpTestSupport.PolicyWithToken(HttpTestSupport.Settings(), roles: HttpAccessPolicy.ServerRole);

        policy.Invoking(p => p.Resolve("admin")).Should().Throw<McpException>()
            .WithMessage("azure_identity 'admin' は無効です。server または user を指定してください（省略すると server）。");
        policy.Invoking(p => p.Resolve("user")).Should().Throw<McpException>()
            .WithMessage("このサーバーでは user 方式は使えません（使える方式: server）。");
    }

    [Fact]
    public void Resolve_Should_Require_The_Role_For_The_Mode()
    {
        var userOnly = HttpTestSupport.PolicyWithToken(HttpTestSupport.Settings(modes: "server,user"), roles: HttpAccessPolicy.UserRole);
        var noRole = HttpTestSupport.PolicyWithToken(HttpTestSupport.Settings(modes: "server,user"));

        userOnly.Invoking(p => p.Resolve("server")).Should().Throw<McpException>()
            .WithMessage("server 方式を使うには、MCP サーバーのアプリロール Timetracker.Deploy.Server が必要です（使える方式: user）。");
        userOnly.Invoking(p => p.ForMode("server")).Should().Throw<McpException>();
        noRole.Invoking(p => p.ForMode("user")).Should().Throw<McpException>()
            .WithMessage("user 方式を使うには、MCP サーバーのアプリロール Timetracker.Deploy.User または Timetracker.Deploy.Server が必要です。");
        noRole.Invoking(p => p.Resolve(null)).Should().Throw<McpException>()
            .WithMessage("このツールを使うには、MCP サーバーのアプリロール Timetracker.Deploy.User または Timetracker.Deploy.Server が必要です。*");
    }

    [Fact]
    public void UnrestrictedAccessPolicy_Should_Reject_The_Azure_Identity_Argument()
    {
        var policy = new UnrestrictedAccessPolicy(new McpServerSettings());

        policy.Invoking(p => p.Resolve("server")).Should().Throw<McpException>().WithMessage("azure_identity は HTTP のサーバーでだけ指定できます*");
    }

    // ---- HttpAccessPolicy: 方式ごとの操作

    [Fact]
    public void User_Mode_Should_Not_Limit_The_Resource_Groups_And_Should_Exchange_The_Request_Token()
    {
        var access = HttpTestSupport.PolicyWithToken(HttpTestSupport.Settings(modes: "user"), roles: HttpAccessPolicy.UserRole).Resolve(null);

        access.Identity.Should().Be(new IdentityInfo("user", "サインインした利用者本人の権限（user 方式）"));
        access.CanCreateResourceGroup.Should().BeTrue();
        access.ResourceGroupBlocker("any", "other").Should().BeNull();
        access.CredentialName.Should().Be("user 方式（利用者本人の権限）");
        access.AuthFailureHint.Should().Be(HttpAccessPolicy.UserAuthHint).And.Contain("user_impersonation");
        // 作るだけでは Entra ID に接続しない
        access.CreateCredential(Mock.Of<IDeploymentLogger>()).Should().BeOfType<OnBehalfOfCredential>();
    }

    [Fact]
    public void User_Mode_Should_Use_The_Client_Secret_When_Configured()
    {
        var settings = new McpServerSettings { Http = HttpTestSupport.Http(modes: "user", secret: "s3cret") };
        var logger = new Mock<IDeploymentLogger>();

        var credential = HttpTestSupport.PolicyWithToken(settings, roles: HttpAccessPolicy.UserRole).Resolve(null).CreateCredential(logger.Object);

        credential.Should().BeOfType<OnBehalfOfCredential>();
        logger.Verify(l => l.Verbose("OnBehalfOfCredential を使用します（クライアントシークレット）"));
    }

    [Fact]
    public void User_Mode_Should_Require_The_Request_Token()
    {
        var context = new DefaultHttpContext { User = HttpTestSupport.User(roles: HttpAccessPolicy.UserRole) };
        var policy = new HttpAccessPolicy(HttpTestSupport.Settings(modes: "user"), new Accessor(context));

        policy.Invoking(p => p.Resolve(null)).Should().Throw<McpException>().WithMessage("要求にアクセストークンがないため、user 方式で操作できません。*");
    }

    [Fact]
    public void Server_Mode_Blocker_Should_Suggest_The_User_Mode_When_Enabled()
    {
        var both = HttpTestSupport.PolicyWithToken(HttpTestSupport.Settings(modes: "server,user"), roles: HttpAccessPolicy.ServerRole).Resolve("server");
        var serverOnly = HttpTestSupport.PolicyWithToken(HttpTestSupport.Settings(), roles: HttpAccessPolicy.ServerRole).Resolve(null);

        both.ResourceGroupBlocker("sub", "other").Should().EndWith("ご自身の Azure の権限で操作する場合は azure_identity=user を指定してください。");
        serverOnly.ResourceGroupBlocker("sub", "other").Should().EndWith("（TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS）。");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Basic abc", null)]
    [InlineData("Bearer ", null)]
    [InlineData("Bearer", null)]
    [InlineData("Bearerabc", null)]
    [InlineData("Bearer  abc", "abc")]
    [InlineData("bearer abc ", "abc")]
    public void BearerToken_Should_Read_The_Authorization_Header(string? header, string? expected)
    {
        var context = new DefaultHttpContext();
        if (header != null)
            context.Request.Headers.Authorization = header;

        HttpAccessPolicy.BearerToken(context).Should().Be(expected);
        HttpAccessPolicy.BearerToken(null).Should().BeNull();
    }

    [Fact]
    public void BearerToken_Should_Ignore_Multiple_Authorization_Headers()
    {
        // どのトークンを使うかが曖昧なため使わない（JWT の検証もこの要求を受け付けない）
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues(["Bearer a", "Bearer b"]);

        HttpAccessPolicy.BearerToken(context).Should().BeNull();
    }

    // ---- instructions と Prompts

    [Fact]
    public void Instructions_And_Prompts_Should_Describe_The_Enabled_Modes()
    {
        var userOnly = HttpTestSupport.Settings(modes: "user");
        var both = HttpTestSupport.Settings(modes: "server,user");

        ServerInstructions.Http(userOnly.Http!).Should().Contain("user 方式").And.Contain(HttpAccessPolicy.UserRole).And.NotContain("server.allowed_resource_groups");
        ServerInstructions.Http(both.Http!).Should().Contain("azure_identity").And.Contain("server.caller_identity_modes").And.Contain("identity");
        new TimetrackerPrompts(userOnly).DeployTimetracker().Should().Contain("利用者本人の権限（user 方式）");
        new TimetrackerPrompts(both).DeployTimetracker().Should().Contain("azure_identity").And.Contain("server.allowed_resource_groups");
    }

    private sealed class Accessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
}
