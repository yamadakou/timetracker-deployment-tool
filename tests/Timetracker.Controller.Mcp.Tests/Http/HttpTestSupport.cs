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

    public static McpHttpSettings Http(bool canCreate = false, string groups = AllowedGroup, string identity = "managed-identity") => new()
    {
        TenantId = TenantId, ClientId = ClientId, PublicUrl = new Uri(PublicUrl), ServerIdentity = identity,
        AllowedResourceGroups = McpHttpSettings.FromEnvironment(name => name == "TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS" ? groups : Required(name))
            .AllowedResourceGroups,
        CanCreateResourceGroup = canCreate,
    };

    /// <summary>HTTP のサーバーの設定（既定のサブスクリプションとリソースグループは sub / rg）。</summary>
    public static McpServerSettings Settings(bool canCreate = false, string groups = AllowedGroup) => new()
    {
        Http = Http(canCreate, groups), DefaultSubscription = "sub", DefaultResourceGroup = "rg",
    };

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
    public static ServerAccessPolicy Policy(McpServerSettings settings, bool withRole = true) =>
        new(settings, Accessor(withRole ? User(roles: ServerAccessPolicy.ServerRole) : User()));

    private static string? Required(string name) => name switch
    {
        "TT_MCP_HTTP__TENANT_ID" => TenantId,
        "TT_MCP_HTTP__CLIENT_ID" => ClientId,
        "TT_MCP_HTTP__PUBLIC_URL" => PublicUrl,
        _ => null,
    };
}
