using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Timetracker.Controller.Mcp.Tests.Http;

/// <summary>HTTP（server 方式）のテストで使う設定と呼び出し元。</summary>
internal static class HttpTestSupport
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string ClientId = "22222222-2222-2222-2222-222222222222";
    public const string PublicUrl = "https://mcp.example.com/mcp";
    public const string AllowedGroup = "/subscriptions/sub/resourceGroups/rg";

    public static McpHttpSettings Http(
        bool canCreate = false, string groups = AllowedGroup, string identity = "managed-identity",
        string modes = "server", string? defaultMode = null, string? secret = null) => new()
    {
        TenantId = TenantId, ClientId = ClientId, PublicUrl = new Uri(PublicUrl), ServerIdentity = identity,
        AllowedResourceGroups = McpHttpSettings.FromEnvironment(name => name == "TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS" ? groups : Required(name))
            .AllowedResourceGroups,
        CanCreateResourceGroup = canCreate,
        IdentityModes = modes.Split(','),
        DefaultIdentityMode = defaultMode ?? modes.Split(',')[0],
        UserClientCredential = secret == null ? "managed-identity" : "client-secret",
        ClientSecret = secret,
    };

    /// <summary>HTTP のサーバーの設定（既定のサブスクリプションとリソースグループは sub / rg）。</summary>
    public static McpServerSettings Settings(bool canCreate = false, string groups = AllowedGroup, string modes = "server", string? defaultMode = null) => new()
    {
        Http = Http(canCreate, groups, modes: modes, defaultMode: defaultMode), DefaultSubscription = "sub", DefaultResourceGroup = "rg",
    };

    /// <summary>アクセストークン（Authorization ヘッダー）とロールを持つ呼び出し元のポリシー（user 方式の資格情報を作れる）。</summary>
    public static HttpAccessPolicy PolicyWithToken(McpServerSettings settings, string token = "user-token", params string[] roles) =>
        PolicyWithToken(settings, token, null, roles);

    /// <summary><paramref name="userCredential"/> で、user 方式の資格情報に渡したトークンを確かめられるポリシー。</summary>
    public static HttpAccessPolicy PolicyWithToken(
        McpServerSettings settings, string token, Func<string, Timetracker.Common.IDeploymentLogger, Azure.Core.TokenCredential>? userCredential,
        params string[] roles)
    {
        var context = new DefaultHttpContext { User = User(roles: roles) };
        context.Request.Headers.Authorization = $"Bearer {token}";
        return new HttpAccessPolicy(settings, new FixedAccessor { HttpContext = context }, userCredential);
    }

    public static ClaimsPrincipal User(string oid = "user-a", params string[] roles)
    {
        var claims = new List<Claim> { new("tid", TenantId), new("oid", oid), new("name", oid) };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "name", "roles"));
    }

    // HttpContextAccessor は値を static な AsyncLocal に置くため、テストでは呼び出し元ごとに独立した実装を使う
    public static IHttpContextAccessor Accessor(ClaimsPrincipal? user) =>
        new FixedAccessor { HttpContext = user == null ? null : new DefaultHttpContext { User = user } };

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>サーバーのロールを持つ（<paramref name="withRole"/> が false なら持たない）呼び出し元の server 方式のポリシー。</summary>
    public static HttpAccessPolicy Policy(McpServerSettings settings, bool withRole = true) =>
        new(settings, Accessor(withRole ? User(roles: HttpAccessPolicy.ServerRole) : User()));

    private static string? Required(string name) => name switch
    {
        "TT_MCP_HTTP__TENANT_ID" => TenantId,
        "TT_MCP_HTTP__CLIENT_ID" => ClientId,
        "TT_MCP_HTTP__PUBLIC_URL" => PublicUrl,
        _ => null,
    };
}
