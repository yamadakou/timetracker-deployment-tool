using System.ComponentModel;
using Azure.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Timetracker.Application.Compose;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Mcp.Tools;

/// <summary>
/// 設定の確認・検証・サブスクリプションの一覧のツール（Phase 3a。generate_compose はトランスポートごとに ComposeFileTools / ComposeContentTools）。
/// 引数名は CLI のオプションと 1 対 1 の snake_case（SDK は C# の引数名をそのままツールの引数名にするため、引数名を snake_case にしている）。
/// 入力の誤りは、LLM が自分で直せるよう、プロトコルのエラーではなくツールの結果（valid: false や isError）で返す。
/// </summary>
[McpServerToolType]
public sealed class DeploymentTools
{
    public const string ComposeFileName = ComposeExporter.ComposeFileName;
    public const string EnvFileName = ComposeExporter.EnvFileName;
    public const string MaskedPassword = ToolSupport.MaskedPassword;

    /// <summary>azure_identity 引数の説明（HTTP のサーバーだけで使う）。</summary>
    internal const string AzureIdentityDescription =
        "HTTP のサーバーで Azure を操作する方式: server（サーバーの ID）または user（利用者本人の権限）。" +
        "省略時はサーバーの既定（get_deployment_options の server.auth_mode）。stdio のサーバーでは指定しない";

    internal static readonly IReadOnlyList<string> Constraints =
    [
        "検証環境向けのツールです。Azure 上の DB のデータは永続化されません（DB コンテナの再起動・再デプロイ・削除で消えます）。",
        "SQL Server は Developer エディションです（本番環境では使えません）。",
        "Azure 上の SQL Server 構成ではサンプル DB を作成できません（Timetracker のセットアップで空の DB を選択してください）。",
        "Timetracker のメモリが 2Gi 以下だと、サンプル DB の作成が OutOfMemory で失敗することがあります。",
    ];

    private readonly McpServerSettings _settings;
    private readonly IAzureAccessPolicy _policy;
    private readonly Func<TokenCredential, ISubscriptionDirectory> _subscriptionDirectory;
    private readonly ILogger<DeploymentTools> _logger;

    /// <param name="subscriptionDirectory">資格情報の作成に失敗した場合もツールの結果として返せるよう、方式の資格情報を作ってから作る</param>
    public DeploymentTools(
        McpServerSettings settings, IAzureAccessPolicy policy, Func<TokenCredential, ISubscriptionDirectory> subscriptionDirectory,
        ILogger<DeploymentTools> logger)
    {
        _settings = settings;
        _policy = policy;
        _subscriptionDirectory = subscriptionDirectory;
        _logger = logger;
    }

    [McpServerTool(Name = "get_deployment_options", Title = "デプロイ設定の既定値と制約",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("デプロイ設定の既定値、選べる値（DB 種別と既定のイメージタグ）、アプリ名の規則、作成するリソース名の接尾辞、注意事項、" +
                 "サーバーの設定（認証モード、パスワードの環境変数が設定済みか）を返す。Azure には接続しない。最初に呼んで前提を確認する。")]
    public DeploymentOptionsResult GetDeploymentOptions() => new(
        DbTypes: ["postgresql", "sqlserver"],
        DefaultTtTags: new Dictionary<string, string>
        {
            ["postgresql"] = Defaults.GetDefaultTimetrackerTag("postgresql"),
            ["sqlserver"] = Defaults.GetDefaultTimetrackerTag("sqlserver"),
        },
        Defaults: new DefaultValues(
            Defaults.DefaultLocation, Defaults.DefaultAppName, DeployOptionsResolver.DefaultDbType, Defaults.DbName,
            Defaults.TimetrackerCpu, Defaults.TimetrackerMemoryGi, Defaults.DbCpu, Defaults.DbMemoryGi, Defaults.RedisCpu, Defaults.RedisMemoryGi),
        AppNameRule: new AppNameRule(AppNameValidator.MinLength, AppNameValidator.MaxLength, AppNameValidator.Pattern, NoDoubleHyphen: true),
        ResourceSuffixes: ResourceNaming.Suffixes,
        Server: new ServerStatus(
            Transport: _settings.Http == null ? "stdio" : "http",
            AuthMode: _policy.DefaultIdentity.AuthMode,
            PasswordEnv: new PasswordEnvStatus(_settings.DbPassword != null, _settings.TrackerPassword != null),
            DefaultSubscriptionSet: _settings.DefaultSubscription != null,
            DefaultResourceGroupSet: _settings.DefaultResourceGroup != null,
            OutputRoot: _settings.OutputRoot,
            AllowedResourceGroups: _settings.Http is { ServerModeEnabled: true } http ? http.AllowedResourceGroups.Select(g => g.ToString()).ToList() : null,
            CanCreateResourceGroup: _settings.Http is { ServerModeEnabled: true } ? _settings.Http.CanCreateResourceGroup : null,
            IdentityModes: _policy.IdentityModes,
            CallerIdentityModes: _policy.CallerIdentityModes),
        Constraints: Constraints,
        TagListUrl: "https://hub.docker.com/r/densocreate/timetracker/tags");

    [McpServerTool(Name = "validate_deployment", Title = "デプロイ設定の検証",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("デプロイ設定を検証し、すべてのエラーと警告、既定値を適用した設定（パスワードはマスク）、作成するリソース名を返す。" +
                 "Azure には接続しない。target=azure（既定）は Azure へのデプロイ用（subscription と resource_group が必須）、" +
                 "target=compose はローカルの Docker 用（generate_compose）。省略した項目には既定値を使う。")]
    public ValidationResult ValidateDeployment(
        [Description("検証の対象。azure（既定）または compose")] string? target = null,
        [Description("サブスクリプション ID（省略時はサーバーの既定値 TT_MCP_DEFAULT_SUBSCRIPTION）")] string? subscription = null,
        [Description("リソースグループ名（省略時はサーバーの既定値 TT_MCP_DEFAULT_RESOURCE_GROUP）")] string? resource_group = null,
        [Description("リージョン（既定 japaneast）")] string? location = null,
        [Description("アプリ名（既定 timetracker）。リソース名の接頭辞になる")] string? app_name = null,
        [Description("DB 種別: postgresql（既定）または sqlserver")] string? db_type = null,
        [Description("DB 名（既定 timetracker）")] string? db_name = null,
        [Description("timetracker イメージのタグ（既定は DB 種別に応じたタグ）")] string? tt_tag = null,
        [Description("DB のパスワード。会話に残るため、サーバーの環境変数 TT_MCP_DB_PASSWORD での設定を推奨")] string? db_password = null,
        [Description("Timetracker のパスワード。会話に残るため、サーバーの環境変数 TT_MCP_TRACKER_PASSWORD での設定を推奨")] string? tracker_password = null,
        [Description("Timetracker の CPU（vCPU、既定 1.5）")] double? tt_cpu = null,
        [Description("Timetracker のメモリ（Gi、既定 3.0）")] double? tt_memory_gi = null,
        [Description("DB の CPU（vCPU、既定 1.0）")] double? db_cpu = null,
        [Description("DB のメモリ（Gi、既定 2.0）")] double? db_memory_gi = null,
        [Description("Redis の CPU（vCPU、既定 0.5）")] double? redis_cpu = null,
        [Description("Redis のメモリ（Gi、既定 1.0）")] double? redis_memory_gi = null)
    {
        var parameters = new DeploymentParameters
        {
            Subscription = subscription, ResourceGroup = resource_group, Location = location, AppName = app_name, DbType = db_type,
            DbName = db_name, TtTag = tt_tag, DbPassword = db_password, TrackerPassword = tracker_password,
            TtCpu = tt_cpu, TtMemoryGi = tt_memory_gi, DbCpu = db_cpu, DbMemoryGi = db_memory_gi, RedisCpu = redis_cpu, RedisMemoryGi = redis_memory_gi,
        };
        var resolved = parameters.Resolve(_settings);
        var (validationTarget, targetError) = ParseTarget(target);
        var errors = new List<string>();
        if (targetError != null)
            errors.Add(targetError);
        errors.AddRange(DeploymentValidator.GetErrors(resolved.Options, validationTarget));
        errors.AddRange(ToolSupport.PasswordHints(resolved, _settings));
        var o = resolved.Options;
        return new ValidationResult(
            Valid: errors.Count == 0,
            Errors: errors,
            Warnings: DeploymentAdvisor.GetWarnings(o, validationTarget),
            Resolved: ToolSupport.ToResolvedSettings(resolved),
            ResourceNames: ToolSupport.ToResourceNames(o.AppName));
    }

    [McpServerTool(Name = "list_subscriptions", Title = "Azure サブスクリプションの一覧",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Azure を操作する ID（stdio ではサーバーの認証モード TT_MCP_AUTH_MODE の資格情報）で使える Azure サブスクリプションの一覧を返す。" +
                 "利用者にデプロイ先のサブスクリプションを選んでもらうときに使う。")]
    public async Task<SubscriptionsResult> ListSubscriptions(
        [Description(AzureIdentityDescription)] string? azure_identity = null,
        CancellationToken cancellationToken = default)
    {
        var access = _policy.Resolve(azure_identity);
        var subscriptions = await ToolSupport.CallAzureAsync(access, _logger, () =>
            _subscriptionDirectory(access.CreateCredential(new McpDeploymentLogger(_logger))).ListAsync(cancellationToken));

        return new SubscriptionsResult(
            access.Identity.AuthMode,
            subscriptions.Select(s => new SubscriptionItem(s.SubscriptionId, s.DisplayName, s.State, s.TenantId)).ToList());
    }

    private static (ValidationTarget Target, string? Error) ParseTarget(string? target) =>
        (target ?? "azure").ToLowerInvariant() switch
        {
            "azure" => (ValidationTarget.Azure, null),
            "compose" => (ValidationTarget.Compose, null),
            _ => (ValidationTarget.Azure, $"target '{target}' は無効です。azure または compose を指定してください。"),
        };
}
