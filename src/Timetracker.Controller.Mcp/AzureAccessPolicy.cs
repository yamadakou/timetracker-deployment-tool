using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using Timetracker.Application.Jobs;
using Timetracker.Controller.Mcp.Tools;

namespace Timetracker.Controller.Mcp;

/// <summary>
/// だれの権限で、どの範囲の Azure を操作してよいか（全体設計 4.11）。ツールは Azure を読む前に確かめる。
/// stdio では利用者の PC の資格情報で、範囲の制限はない。HTTP（server 方式）ではアプリロールと、許可したリソースグループで制限する。
/// </summary>
public interface IAzureAccessPolicy
{
    /// <summary>Azure を操作する ID（計画などの結果に表示する）。</summary>
    IdentityInfo Identity { get; }

    /// <summary>リソースグループが無い場合に作成してよいか。</summary>
    bool CanCreateResourceGroup { get; }

    /// <summary>呼び出し元が Azure を読む・操作するツールを使えなければ、理由を添えたツールのエラーにする。</summary>
    /// <exception cref="McpException"></exception>
    void RequireAzureAccess();

    /// <summary>操作してよい範囲になければ、その理由（範囲内なら null）。</summary>
    string? ResourceGroupBlocker(string subscription, string resourceGroup);
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

    public UnrestrictedAccessPolicy(McpServerSettings settings)
    {
        Identity = new IdentityInfo(settings.AuthMode, Displays[settings.AuthMode]);
    }

    public IdentityInfo Identity { get; }

    public bool CanCreateResourceGroup => true;

    public void RequireAzureAccess()
    {
    }

    public string? ResourceGroupBlocker(string subscription, string resourceGroup) => null;
}

/// <summary>
/// HTTP の server 方式: サーバーの ID（マネージド ID など）で操作する。アプリロール Timetracker.Deploy.Server を持つ呼び出し元だけが使え、
/// 操作できるのは TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS に列挙したリソースグループだけ。
/// </summary>
public sealed class ServerAccessPolicy : IAzureAccessPolicy
{
    public const string ServerRole = "Timetracker.Deploy.Server";

    private readonly McpHttpSettings _settings;
    private readonly IHttpContextAccessor _http;

    public ServerAccessPolicy(McpServerSettings settings, IHttpContextAccessor http)
    {
        _settings = settings.Http!;
        _http = http;
        Identity = new IdentityInfo("server", _settings.ServerIdentity == "managed-identity"
            ? "MCP サーバーのマネージド ID（server 方式）"
            : "MCP サーバーのサービスプリンシパル（server 方式）");
    }

    public IdentityInfo Identity { get; }

    public bool CanCreateResourceGroup => _settings.CanCreateResourceGroup;

    public void RequireAzureAccess()
    {
        if (_http.HttpContext?.User is not { } user || !user.IsInRole(ServerRole))
            throw new McpException($"このツールを使うには、MCP サーバーのアプリロール {ServerRole} が必要です。管理者に割り当てを依頼してください。");
    }

    public string? ResourceGroupBlocker(string subscription, string resourceGroup) =>
        _settings.AllowedResourceGroups.Any(g => g.Matches(subscription, resourceGroup))
            ? null
            : $"リソースグループ {resourceGroup}（サブスクリプション {subscription}）は、このサーバーで操作できる範囲にありません。" +
              $"操作できるのは {string.Join(", ", _settings.AllowedResourceGroups)} です（TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS）。";
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
