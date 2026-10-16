using System.Text;
using System.Text.RegularExpressions;

namespace Timetracker.Controller.Mcp;

/// <summary>操作してよいリソースグループ（サブスクリプション ID とリソースグループ名。比較は大文字小文字を区別しない）。</summary>
public sealed record AllowedResourceGroup(string Subscription, string ResourceGroup)
{
    public bool Matches(string subscription, string resourceGroup) =>
        string.Equals(Subscription, subscription, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ResourceGroup, resourceGroup, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}";
}

/// <summary>
/// HTTP で公開する場合の設定（環境変数 TT_MCP_HTTP__*。全体設計 4.11、詳細設計 Phase 4a・4b）。
/// 呼び出し元は Entra ID のアクセストークンで認証する。Azure は、サーバーの ID（server 方式）で許可したリソースグループだけを、
/// または利用者本人の権限（user 方式）で操作する。
/// </summary>
public sealed partial record McpHttpSettings
{
    public const string ServerMode = "server";
    public const string UserMode = "user";

    public static readonly IReadOnlyList<string> IdentityModeNames = [ServerMode, UserMode];
    public static readonly IReadOnlyList<string> ServerIdentities = ["managed-identity", "sp-env"];
    public static readonly IReadOnlyList<string> UserClientCredentials = ["managed-identity", "client-secret"];

    public required string TenantId { get; init; }
    public required string ClientId { get; init; }

    /// <summary>公開する MCP のエンドポイントの URL（保護リソースのメタデータの resource）。</summary>
    public required Uri PublicUrl { get; init; }

    /// <summary>使える方式（server / user）。</summary>
    public IReadOnlyList<string> IdentityModes { get; init; } = [ServerMode];

    /// <summary>azure_identity を省略したときの方式。</summary>
    public string DefaultIdentityMode { get; init; } = ServerMode;

    /// <summary>server 方式で使う資格情報（CredentialFactory の認証モード）。</summary>
    public string ServerIdentity { get; init; } = "managed-identity";

    /// <summary>server 方式で操作してよいリソースグループ（server 方式が無効なら空）。</summary>
    public IReadOnlyList<AllowedResourceGroup> AllowedResourceGroups { get; init; } = [];
    public bool CanCreateResourceGroup { get; init; }

    /// <summary>user 方式のトークン交換（OBO）で、サーバーのアプリ登録が使う資格情報（managed-identity / client-secret）。</summary>
    public string UserClientCredential { get; init; } = "managed-identity";

    /// <summary>アプリ登録のクライアントシークレット（UserClientCredential が client-secret の場合だけ）。</summary>
    public string? ClientSecret { get; init; }

    public bool ServerModeEnabled => IdentityModes.Contains(ServerMode);
    public bool UserModeEnabled => IdentityModes.Contains(UserMode);

    /// <summary>トークンの発行者（v2）。認可サーバーとして保護リソースのメタデータにも載せる。</summary>
    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>クライアントが要求するスコープ。</summary>
    public string Scope => $"api://{ClientId}/{ScopeName}";

    public const string ScopeName = "Mcp.Access";

    /// <summary>トークンの scp（委任のスコープ。スペース区切り）に <see cref="ScopeName"/> があるか。ID トークンやアプリのみのトークンには scp が無い。</summary>
    public static bool HasScope(System.Security.Claims.ClaimsPrincipal user) =>
        user.FindAll("scp").Any(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ScopeName));

    /// <exception cref="ArgumentException">必須の不足や不正な値</exception>
    public static McpHttpSettings FromEnvironment(Func<string, string?> get)
    {
        string Require(string name) => get(name) ?? throw new ArgumentException($"HTTP で起動するには {name} が必要です。");

        var tenantId = Require("TT_MCP_HTTP__TENANT_ID");
        var clientId = Require("TT_MCP_HTTP__CLIENT_ID");
        var publicUrl = Require("TT_MCP_HTTP__PUBLIC_URL");
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            throw new ArgumentException($"TT_MCP_HTTP__PUBLIC_URL には MCP のエンドポイントの絶対 URL を指定してください: {publicUrl}");

        var modes = (get("TT_MCP_HTTP__IDENTITY_MODES") ?? ServerMode)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (modes.Count == 0 || modes.Any(m => !IdentityModeNames.Contains(m)))
            throw new ArgumentException(
                $"TT_MCP_HTTP__IDENTITY_MODES '{get("TT_MCP_HTTP__IDENTITY_MODES")}' は無効です。server、user、または server,user を指定してください。");
        var defaultMode = (get("TT_MCP_HTTP__DEFAULT_IDENTITY_MODE") ?? modes[0]).ToLowerInvariant();
        if (!modes.Contains(defaultMode))
            throw new ArgumentException(
                $"TT_MCP_HTTP__DEFAULT_IDENTITY_MODE '{defaultMode}' は、TT_MCP_HTTP__IDENTITY_MODES（{string.Join(",", modes)}）のどれかにしてください。");

        var identity = (get("TT_MCP_HTTP__SERVER_IDENTITY") ?? "managed-identity").ToLowerInvariant();
        if (!ServerIdentities.Contains(identity))
            throw new ArgumentException($"TT_MCP_HTTP__SERVER_IDENTITY '{identity}' は無効です。有効な値は {string.Join(", ", ServerIdentities)} です。");

        // server 方式で全サブスクリプションを操作できる状態で公開しないよう、許可するリソースグループの指定を必須にする
        // （user 方式は利用者本人の権限で操作するため、範囲をサーバーでは制限しない）
        List<AllowedResourceGroup> groups = [];
        if (modes.Contains(ServerMode))
        {
            groups = Require("TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseResourceGroup)
                .ToList();
            if (groups.Count == 0)
                throw new ArgumentException("TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS に、操作してよいリソースグループを 1 つ以上指定してください。");
        }

        var canCreate = get("TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP") switch
        {
            null or "false" or "0" => false,
            "true" or "1" => true,
            var other => throw new ArgumentException($"TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP '{other}' は無効です。true または false を指定してください。"),
        };

        var userCredential = (get("TT_MCP_HTTP__USER_CLIENT_CREDENTIAL") ?? "managed-identity").ToLowerInvariant();
        if (!UserClientCredentials.Contains(userCredential))
            throw new ArgumentException(
                $"TT_MCP_HTTP__USER_CLIENT_CREDENTIAL '{userCredential}' は無効です。有効な値は {string.Join(", ", UserClientCredentials)} です。");
        var secret = modes.Contains(UserMode) && userCredential == "client-secret" ? Require("TT_MCP_HTTP__CLIENT_SECRET") : null;

        return new McpHttpSettings
        {
            TenantId = tenantId, ClientId = clientId, PublicUrl = url, IdentityModes = modes, DefaultIdentityMode = defaultMode,
            ServerIdentity = identity, AllowedResourceGroups = groups, CanCreateResourceGroup = canCreate,
            UserClientCredential = userCredential, ClientSecret = secret,
        };
    }

    // ログなどに出ないよう、クライアントシークレットは値を出さない
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"TenantId = {TenantId}, ClientId = {ClientId}, PublicUrl = {PublicUrl}, ");
        builder.Append($"IdentityModes = [{string.Join(", ", IdentityModes)}], DefaultIdentityMode = {DefaultIdentityMode}, ");
        builder.Append($"ServerIdentity = {ServerIdentity}, AllowedResourceGroups = [{string.Join(", ", AllowedResourceGroups)}], ");
        builder.Append($"CanCreateResourceGroup = {CanCreateResourceGroup}, UserClientCredential = {UserClientCredential}, ");
        builder.Append($"ClientSecret = {(ClientSecret == null ? "null" : "********")}");
        return true;
    }

    private static AllowedResourceGroup ParseResourceGroup(string id) =>
        ResourceGroupId().Match(id) is { Success: true } m
            ? new AllowedResourceGroup(m.Groups["sub"].Value, m.Groups["rg"].Value)
            : throw new ArgumentException(
                $"TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS の '{id}' は無効です。/subscriptions/<サブスクリプション ID>/resourceGroups/<名前> の形式で指定してください。");

    [GeneratedRegex(@"^/subscriptions/(?<sub>[^/]+)/resourceGroups/(?<rg>[^/]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceGroupId();
}
