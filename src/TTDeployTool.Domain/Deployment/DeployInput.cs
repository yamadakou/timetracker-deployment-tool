namespace TTDeployTool.Domain.Deployment;

/// <summary>
/// フロントエンド（CLI など）から受け取った、既定値を適用する前のデプロイ設定。
/// null・空白の項目は未指定として扱い、<see cref="DeployOptionsResolver"/> が既定値を適用する。
/// </summary>
public sealed record DeployInput
{
    public string? Subscription { get; init; }
    public string? ResourceGroup { get; init; }
    public string? Location { get; init; }
    public string? AppName { get; init; }
    public string? DbType { get; init; }
    public string? DbName { get; init; }
    public string? DbPassword { get; init; }
    public string? TrackerPassword { get; init; }
    public string? TimetrackerTag { get; init; }
    public string? AuthMode { get; init; }

    public double? TimetrackerCpu { get; init; }
    public double? TimetrackerMemoryGi { get; init; }
    public double? DbCpu { get; init; }
    public double? DbMemoryGi { get; init; }
    public double? RedisCpu { get; init; }
    public double? RedisMemoryGi { get; init; }

    public bool DryRun { get; init; }
    public bool Verbose { get; init; }

    /// <summary>DB のデータを Azure Files に置くか（null は今の状態を引き継ぐ）。</summary>
    public bool? PersistDb { get; init; }

    /// <summary>利用可能期限日（yyyy-MM-dd か none。null は既定。<see cref="DeploymentExpiry"/>）。</summary>
    public string? ExpiresOn { get; init; }

    /// <summary>グループ名（null・空は指定しない。既にあるデプロイでは今のグループを引き継ぐ。<see cref="DeploymentGroup"/>）。</summary>
    public string? Group { get; init; }
}
