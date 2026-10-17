using System.Security.Claims;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using Timetracker.Application.Jobs;
using Timetracker.Common;
using Timetracker.Controller.Mcp.Tools;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Mcp;

/// <summary>
/// だれの権限で、どの範囲の Azure を操作してよいか（全体設計 4.11）。ツールは Azure を読む前に <see cref="Resolve"/> で方式を決め、
/// 結果の <see cref="AzureAccess"/> で範囲を確かめ、資格情報を作る。
/// stdio では利用者の PC の資格情報で、範囲の制限はない。HTTP では方式（server / user）とアプリロールで決まる。
/// </summary>
public interface IAzureAccessPolicy
{
    /// <summary>azure_identity を省略したときに使う ID（get_deployment_options に表示する）。</summary>
    IdentityInfo DefaultIdentity { get; }

    /// <summary>サーバーで有効な方式（stdio では null）。</summary>
    IReadOnlyList<string>? IdentityModes { get; }

    /// <summary>呼び出し元が使える方式（stdio では null）。</summary>
    IReadOnlyList<string>? CallerIdentityModes { get; }

    /// <summary>呼び出し元が Azure を読む・操作するツールを一つも使えなければ、理由を添えたツールのエラーにする。</summary>
    /// <exception cref="McpException"></exception>
    void RequireAzureAccess();

    /// <summary>ツールの引数 azure_identity（省略可）から方式を決める。使えない方式なら、理由を添えたツールのエラーにする。</summary>
    /// <exception cref="McpException"></exception>
    AzureAccess Resolve(string? azureIdentity);

    /// <summary>計画に記録した方式で、もう一度方式を作る（実行の直前に、呼び出し元がまだその方式を使えるかも確かめる）。</summary>
    /// <exception cref="McpException"></exception>
    AzureAccess ForMode(string mode);
}

/// <summary>
/// 決まった方式で Azure を操作するための情報（表示、範囲の確認、資格情報の作成、認証に失敗したときの案内）。
/// user 方式では、作った時点の要求のアクセストークンを持ち、ジョブが終わるまで同じ利用者の権限で操作する。
/// </summary>
public sealed class AzureAccess
{
    private readonly Func<string, string, string?> _blocker;
    private readonly Func<IDeploymentLogger, TokenCredential> _credential;

    public AzureAccess(
        string mode, IdentityInfo identity, bool canCreateResourceGroup, Func<string, string, string?> blocker,
        Func<IDeploymentLogger, TokenCredential> credential, string credentialName, string authFailureHint)
    {
        Mode = mode;
        Identity = identity;
        CanCreateResourceGroup = canCreateResourceGroup;
        _blocker = blocker;
        _credential = credential;
        CredentialName = credentialName;
        AuthFailureHint = authFailureHint;
    }

    /// <summary>計画に記録する方式（stdio では認証モード、HTTP では server / user）。</summary>
    public string Mode { get; }

    /// <summary>Azure を操作する ID（計画などの結果に表示する）。</summary>
    public IdentityInfo Identity { get; }

    /// <summary>リソースグループが無い場合に作成してよいか。</summary>
    public bool CanCreateResourceGroup { get; }

    /// <summary>認証に失敗したときのメッセージに出す資格情報の説明（例: 認証モード: azure-cli）。</summary>
    public string CredentialName { get; }

    /// <summary>認証に失敗したときの対処。</summary>
    public string AuthFailureHint { get; }

    /// <summary>操作してよい範囲になければ、その理由（範囲内なら null）。</summary>
    public string? ResourceGroupBlocker(string subscription, string resourceGroup) => _blocker(subscription, resourceGroup);

    /// <summary>Azure を操作する資格情報を作る（資格情報を作れない場合は InvalidOperationException など）。</summary>
    public TokenCredential CreateCredential(IDeploymentLogger logger) => _credential(logger);
}

/// <summary>stdio: サーバーの認証モード（利用者の PC の資格情報）で、範囲を制限しない。</summary>
public sealed class UnrestrictedAccessPolicy : IAzureAccessPolicy
{
    // Azure を操作する ID の説明（認証モードごと）
    private static readonly IReadOnlyDictionary<string, string> Displays = new Dictionary<string, string>
    {
        ["azure-cli"] = "az login でサインインしているアカウント",
        ["default"] = "DefaultAzureCredential が見つけた資格情報",
        ["sp-env"] = "環境変数 AZURE_CLIENT_ID のサービスプリンシパル",
        ["device-code"] = "デバイスコード認証でサインインしたアカウント",
        ["managed-identity"] = "サーバーのマネージド ID",
    };

    private readonly AzureAccess _access;

    public UnrestrictedAccessPolicy(McpServerSettings settings)
    {
        var mode = settings.AuthMode;
        DefaultIdentity = new IdentityInfo(mode, Displays[mode]);
        _access = new AzureAccess(
            mode, DefaultIdentity, canCreateResourceGroup: true, (_, _) => null,
            logger => CredentialFactory.Create(mode, logger), $"認証モード: {mode}", ToolSupport.AuthHints[mode]);
    }

    public IdentityInfo DefaultIdentity { get; }

    public IReadOnlyList<string>? IdentityModes => null;

    public IReadOnlyList<string>? CallerIdentityModes => null;

    public void RequireAzureAccess()
    {
    }

    public AzureAccess Resolve(string? azureIdentity) => azureIdentity == null
        ? _access
        : throw new McpException(
            "azure_identity は HTTP のサーバーでだけ指定できます（stdio では、サーバーの認証モード TT_MCP_AUTH_MODE の資格情報で操作します）。指定せずに呼んでください。");

    public AzureAccess ForMode(string mode) => _access;
}

/// <summary>
/// HTTP: サーバーの設定で有効な方式（TT_MCP_HTTP__IDENTITY_MODES）のうち、呼び出し元のアプリロールで使えるものだけを使わせる。
/// server 方式はサーバーの ID（マネージド ID など）で、TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS のリソースグループだけを操作する。
/// user 方式は、要求のアクセストークンを OBO で交換し、利用者本人の権限で操作する（範囲は本人の Azure RBAC で決まる）。
/// </summary>
public sealed class HttpAccessPolicy : IAzureAccessPolicy
{
    public const string ServerRole = "Timetracker.Deploy.Server";
    public const string UserRole = "Timetracker.Deploy.User";

    public const string UserAuthHint =
        "サインインし直してから、もう一度試してください。続けて失敗する場合は、管理者に、MCP サーバーのアプリ登録の" +
        "委任アクセス許可（Azure Service Management の user_impersonation）と管理者の同意、条件付きアクセスの設定を確認してもらってください。";

    private readonly McpHttpSettings _settings;
    private readonly IHttpContextAccessor _http;

    // user 方式の資格情報（要求のアクセストークンを OBO で交換する）。テストでは、渡したトークンを確かめるために差し替える
    private readonly Func<string, IDeploymentLogger, TokenCredential> _userCredential;

    public HttpAccessPolicy(McpServerSettings settings, IHttpContextAccessor http)
        : this(settings, http, null)
    {
    }

    internal HttpAccessPolicy(McpServerSettings settings, IHttpContextAccessor http, Func<string, IDeploymentLogger, TokenCredential>? userCredential)
    {
        _settings = settings.Http!;
        _http = http;
        var secret = _settings.UserClientCredential == "client-secret" ? _settings.ClientSecret : null;
        _userCredential = userCredential
            ?? ((token, logger) => CredentialFactory.CreateOnBehalfOf(_settings.TenantId, _settings.ClientId, secret, token, logger));
    }

    /// <summary>azure_identity を省略したときの ID（既定の方式を呼び出し元が使えなければ、使えるほう）。</summary>
    public IdentityInfo DefaultIdentity => Identity(DefaultMode(CallerIdentityModes));

    public IReadOnlyList<string> IdentityModes => _settings.IdentityModes;

    public IReadOnlyList<string> CallerIdentityModes
    {
        get
        {
            var user = _http.HttpContext?.User;
            var server = user?.IsInRole(ServerRole) == true;
            var modes = new List<string>();
            if (_settings.ServerModeEnabled && server)
                modes.Add(McpHttpSettings.ServerMode);
            if (_settings.UserModeEnabled && (server || user?.IsInRole(UserRole) == true))
                modes.Add(McpHttpSettings.UserMode);
            return modes;
        }
    }

    IReadOnlyList<string>? IAzureAccessPolicy.IdentityModes => IdentityModes;

    IReadOnlyList<string>? IAzureAccessPolicy.CallerIdentityModes => CallerIdentityModes;

    public void RequireAzureAccess()
    {
        if (CallerIdentityModes.Count == 0)
            throw new McpException($"このツールを使うには、MCP サーバーのアプリロール {RequiredRoles()} が必要です。管理者に割り当てを依頼してください。");
    }

    public AzureAccess Resolve(string? azureIdentity)
    {
        RequireAzureAccess();
        var usable = CallerIdentityModes;
        if (azureIdentity == null)
            return Build(DefaultMode(usable));

        var mode = azureIdentity.ToLowerInvariant();
        if (!McpHttpSettings.IdentityModeNames.Contains(mode))
            throw new McpException($"azure_identity '{azureIdentity}' は無効です。server または user を指定してください（省略すると {_settings.DefaultIdentityMode}）。");
        if (!_settings.IdentityModes.Contains(mode))
            throw new McpException($"このサーバーでは {mode} 方式は使えません（使える方式: {string.Join(", ", _settings.IdentityModes)}）。");
        return ForMode(mode);
    }

    public AzureAccess ForMode(string mode)
    {
        var usable = CallerIdentityModes;
        if (!usable.Contains(mode))
        {
            var roles = mode == McpHttpSettings.ServerMode ? ServerRole : $"{UserRole} または {ServerRole}";
            var available = usable.Count > 0 ? $"（使える方式: {string.Join(", ", usable)}）" : "";
            throw new McpException($"{mode} 方式を使うには、MCP サーバーのアプリロール {roles} が必要です{available}。");
        }
        return Build(mode);
    }

    private AzureAccess Build(string mode)
    {
        if (mode == McpHttpSettings.ServerMode)
        {
            return new AzureAccess(
                mode, Identity(mode), _settings.CanCreateResourceGroup, ServerBlocker,
                logger => CredentialFactory.Create(_settings.ServerIdentity, logger),
                $"認証モード: {_settings.ServerIdentity}", ToolSupport.AuthHints[_settings.ServerIdentity]);
        }

        // 要求のアクセストークン（このサーバー向け）。/mcp は認証が必須のため、通常は必ずある
        var token = BearerToken(_http.HttpContext)
                    ?? throw new McpException("要求にアクセストークンがないため、user 方式で操作できません。サインインし直してください。");
        return new AzureAccess(
            mode, Identity(mode), canCreateResourceGroup: true, (_, _) => null,
            logger => _userCredential(token, logger),
            "user 方式（利用者本人の権限）", UserAuthHint);
    }

    // 既定の方式。呼び出し元が使えなければ、使えるほう（Timetracker.Deploy.User だけを持つ利用者が毎回指定しなくて済むように）
    private string DefaultMode(IReadOnlyList<string> usable) =>
        usable.Contains(_settings.DefaultIdentityMode) || usable.Count == 0 ? _settings.DefaultIdentityMode : usable[0];

    private string? ServerBlocker(string subscription, string resourceGroup) =>
        _settings.AllowedResourceGroups.Any(g => g.Matches(subscription, resourceGroup))
            ? null
            : $"リソースグループ {resourceGroup}（サブスクリプション {subscription}）は、このサーバーで server 方式で操作できる範囲にありません。" +
              $"操作できるのは {string.Join(", ", _settings.AllowedResourceGroups)} です（TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS）。" +
              (_settings.UserModeEnabled ? "ご自身の Azure の権限で操作する場合は azure_identity=user を指定してください。" : "");

    private IdentityInfo Identity(string mode) => mode == McpHttpSettings.ServerMode
        ? new IdentityInfo(mode, _settings.ServerIdentity == "managed-identity"
            ? "MCP サーバーのマネージド ID（server 方式）"
            : "MCP サーバーのサービスプリンシパル（server 方式）")
        : new IdentityInfo(mode, "サインインした利用者本人の権限（user 方式）");

    // 使える方式に必要なロール（server 方式は Server、user 方式は User または Server）
    private string RequiredRoles() => _settings.UserModeEnabled ? $"{UserRole} または {ServerRole}" : ServerRole;

    /// <summary>
    /// Authorization ヘッダーの Bearer トークン（無ければ null）。ヘッダーが複数ある場合は、どれを使うかが曖昧なため null にする
    /// （JWT の検証も、その要求を受け付けない）。
    /// </summary>
    internal static string? BearerToken(HttpContext? context) =>
        context?.Request.Headers.Authorization is { Count: 1 } values
        && values[0] is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && header["Bearer ".Length..].Trim() is { Length: > 0 } token
            ? token
            : null;
}

/// <summary>HTTP の呼び出し元: Entra ID のトークンのテナント ID と オブジェクト ID（計画・ジョブを利用者ごとに分ける）。</summary>
public sealed class HttpCallerContext : ICallerContext
{
    private readonly IHttpContextAccessor _http;

    public HttpCallerContext(IHttpContextAccessor http)
    {
        _http = http;
    }

    public string CallerId => Id(_http.HttpContext?.User)
        ?? throw new InvalidOperationException("呼び出し元のトークンに tid / oid がありません。");

    /// <summary>トークンの tid と oid から呼び出し元の ID を作る（どちらかが無ければ null）。</summary>
    public static string? Id(ClaimsPrincipal? user) =>
        user?.FindFirst("tid")?.Value is { } tid && user.FindFirst("oid")?.Value is { } oid ? $"{tid}/{oid}" : null;
}
