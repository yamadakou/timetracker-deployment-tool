namespace TTDeployTool.Domain.Deployment;

public class DeployOptions
{
    public required string Subscription { get; set; }
    public required string ResourceGroup { get; set; }
    public string Location { get; set; } = Defaults.DefaultLocation; // japaneast
    public string AppName { get; set; } = Defaults.DefaultAppName;   // TimeTracker
    public string DbType { get; set; } = "postgresql";              // postgresql | sqlserver
    public required string DbPassword { get; set; }                  // 種別問わず統一パラメータ
    public string DbName { get; set; } = Defaults.DbName;
    public required string TrackerPassword { get; set; }
    public bool DryRun { get; set; }
    public bool Verbose { get; set; }

    // DB のデータを Azure Files に置くか。null は既存の DB アプリの今の状態を引き継ぐ（新規は永続化しない）
    public bool? PersistDb { get; set; }

    // 利用可能期限日（yyyy-MM-dd | none）。null は既定（今の期限を引き継ぎ、無ければ今日から 32 日後。DeploymentExpiry）
    public string? ExpiresOn { get; set; }

    // グループ名（前後の空白を除いたもの）。null は指定しない（既にあるデプロイでは今のグループを引き継ぐ。DeploymentGroup）
    public string? Group { get; set; }

    // デプロイで Container App に付けるタグ（利用可能期限・デプロイした時刻）。実行するときに決める（DeploymentRunner）
    public IReadOnlyDictionary<string, string>? LifecycleTags { get; set; }

    // 認証モード（default | azure-cli | sp-env | device-code | managed-identity）
    public string AuthMode { get; set; } = "default";

    // timetracker イメージタグ（バージョン）
    public string TimetrackerTag { get; set; } = Defaults.TimetrackerTagPostgres;

    // コンテナスペック（CPU: vCPU、小数可 / Memory: Gi）
    public double TimetrackerCpu { get; set; } = Defaults.TimetrackerCpu;            // timetracker-tt
    public double TimetrackerMemoryGi { get; set; } = Defaults.TimetrackerMemoryGi;  // timetracker-tt
    public double DbCpu { get; set; } = Defaults.DbCpu;                              // timetracker-db
    public double DbMemoryGi { get; set; } = Defaults.DbMemoryGi;                    // timetracker-db
    public double RedisCpu { get; set; } = Defaults.RedisCpu;                        // timetracker-redis
    public double RedisMemoryGi { get; set; } = Defaults.RedisMemoryGi;              // timetracker-redis

    // Dry-run 用ファイル出力
    public string OutputCompose { get; set; } = "./docker-compose.yml";

    /// <summary>デプロイで付けるタグを決めた設定の複製を返す（元の設定は変えない）。</summary>
    public DeployOptions WithLifecycleTags(IReadOnlyDictionary<string, string> tags)
    {
        var copy = (DeployOptions)MemberwiseClone();
        copy.LifecycleTags = tags;
        return copy;
    }

    /// <summary>実際に永続化するかを決めた設定の複製を返す（元の設定は変えない）。</summary>
    public DeployOptions WithPersistDb(bool persistDb)
    {
        var copy = (DeployOptions)MemberwiseClone();
        copy.PersistDb = persistDb;
        return copy;
    }
}
