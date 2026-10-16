namespace Timetracker.Controller.Mcp.Tools;

// ツールの結果（structuredContent と outputSchema の元）。プロパティ名は snake_case で返す（McpServerHost の JSON 設定）

public sealed record DeploymentOptionsResult(
    IReadOnlyList<string> DbTypes,
    IReadOnlyDictionary<string, string> DefaultTtTags,
    DefaultValues Defaults,
    AppNameRule AppNameRule,
    IReadOnlyList<string> ResourceSuffixes,
    ServerStatus Server,
    IReadOnlyList<string> Constraints,
    string TagListUrl);

public sealed record DefaultValues(
    string Location, string AppName, string DbType, string DbName,
    double TtCpu, double TtMemoryGi, double DbCpu, double DbMemoryGi, double RedisCpu, double RedisMemoryGi);

public sealed record AppNameRule(int MinLength, int MaxLength, string Pattern, bool NoDoubleHyphen);

/// <summary>サーバーの設定の状態。パスワードは値を返さず、環境変数が設定済みかだけを返す。</summary>
/// <param name="AllowedResourceGroups">HTTP（server 方式）で操作できるリソースグループ（stdio では null）</param>
/// <param name="CanCreateResourceGroup">HTTP（server 方式）でリソースグループを作成できるか（stdio では null）</param>
public sealed record ServerStatus(
    string Transport, string AuthMode, PasswordEnvStatus PasswordEnv,
    bool DefaultSubscriptionSet, bool DefaultResourceGroupSet, string? OutputRoot,
    IReadOnlyList<string>? AllowedResourceGroups = null, bool? CanCreateResourceGroup = null);

public sealed record PasswordEnvStatus(bool DbPassword, bool TrackerPassword);

public sealed record ValidationResult(
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    ResolvedSettings Resolved,
    ResourceNames ResourceNames);

/// <summary>既定値を適用した設定。パスワードはマスクし、取得元だけを示す。</summary>
public sealed record ResolvedSettings(
    string Subscription, string ResourceGroup, string Location, string AppName, string DbType, string DbName, string TtTag,
    string DbPassword, string TrackerPassword,
    double TtCpu, double TtMemoryGi, double DbCpu, double DbMemoryGi, double RedisCpu, double RedisMemoryGi);

public sealed record ResourceNames(string Environment, string Tt, string Db, string Redis);

/// <summary>generate_compose の結果。.env はパスワードを含むため内容を返さない。</summary>
public sealed record ComposeResult(
    string ComposePath,
    string EnvPath,
    string ComposeContent,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> NextSteps);

public sealed record SubscriptionsResult(string AuthMode, IReadOnlyList<SubscriptionItem> Subscriptions);

public sealed record SubscriptionItem(string SubscriptionId, string DisplayName, string? State, string? TenantId);

/// <summary>HTTP の generate_compose の結果（.env のパスワードはプレースホルダー）。</summary>
public sealed record ComposeContentResult(
    string ComposeFileName,
    string ComposeContent,
    string EnvFileName,
    string EnvContent,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> NextSteps);
