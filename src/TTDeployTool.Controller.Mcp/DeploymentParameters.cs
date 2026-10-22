using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Mcp;

/// <summary>パスワードをどこから取ったか（値は結果・ログに出さず、取得元だけを示す）。</summary>
public enum PasswordSource
{
    None,
    Argument,
    Environment,
}

/// <summary>
/// ツールの共通パラメータ（全体設計 4.2。CLI のオプションと 1 対 1）。null は未指定。
/// </summary>
public sealed record DeploymentParameters
{
    public string? Subscription { get; init; }
    public string? ResourceGroup { get; init; }
    public string? Location { get; init; }
    public string? AppName { get; init; }
    public string? DbType { get; init; }
    public string? DbName { get; init; }
    public string? TtTag { get; init; }
    public string? DbPassword { get; init; }
    public string? TrackerPassword { get; init; }
    public double? TtCpu { get; init; }
    public double? TtMemoryGi { get; init; }
    public double? DbCpu { get; init; }
    public double? DbMemoryGi { get; init; }
    public double? RedisCpu { get; init; }
    public double? RedisMemoryGi { get; init; }
    public bool? PersistDb { get; init; }

    /// <summary>利用可能期限日（yyyy-MM-dd か none。null は既定）。</summary>
    public string? ExpiresOn { get; init; }

    /// <summary>
    /// 既定値を適用した設定を作る。subscription / resource_group は引数が無ければサーバーの既定値を使い、
    /// パスワードは引数 → 環境変数の順に取る（全体設計 4.10）。既定値の解決は CLI と同じ <see cref="DeployOptionsResolver"/> で行う。
    /// </summary>
    public ResolvedParameters Resolve(McpServerSettings settings)
    {
        var (dbPassword, dbSource) = Pick(DbPassword, settings.DbPassword);
        var (trackerPassword, trackerSource) = Pick(TrackerPassword, settings.TrackerPassword);
        var options = DeployOptionsResolver.Resolve(new DeployInput
        {
            Subscription        = OrNull(Subscription) ?? settings.DefaultSubscription,
            ResourceGroup       = OrNull(ResourceGroup) ?? settings.DefaultResourceGroup,
            Location            = Location,
            AppName             = AppName,
            DbType              = DbType,
            DbName              = DbName,
            TimetrackerTag      = TtTag,
            DbPassword          = dbPassword,
            TrackerPassword     = trackerPassword,
            TimetrackerCpu      = TtCpu,
            TimetrackerMemoryGi = TtMemoryGi,
            DbCpu               = DbCpu,
            DbMemoryGi          = DbMemoryGi,
            RedisCpu            = RedisCpu,
            RedisMemoryGi       = RedisMemoryGi,
            PersistDb           = PersistDb,
            ExpiresOn           = ExpiresOn,
            AuthMode            = settings.AuthMode,
        });
        return new ResolvedParameters(options, dbSource, trackerSource);
    }

    private static string? OrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static (string? Value, PasswordSource Source) Pick(string? argument, string? environment) =>
        OrNull(argument) is { } fromArgument ? (fromArgument, PasswordSource.Argument)
        : environment is { } fromEnvironment ? (fromEnvironment, PasswordSource.Environment)
        : (null, PasswordSource.None);
}

/// <summary>既定値を適用した設定と、パスワードの取得元。</summary>
public sealed record ResolvedParameters(DeployOptions Options, PasswordSource DbPasswordSource, PasswordSource TrackerPasswordSource);
