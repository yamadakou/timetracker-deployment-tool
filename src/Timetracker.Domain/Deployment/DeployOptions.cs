namespace Timetracker.Domain.Deployment;

public class DeployOptions
{
    public required string Subscription { get; set; }
    public required string ResourceGroup { get; set; }
    public string Location { get; set; } = Defaults.DefaultLocation; // japaneast
    public string AppName { get; set; } = Defaults.DefaultAppName;   // TimeTracker
    public string DbType { get; set; } = "postgresql";              // postgresql | sqlserver
    public required string DbPassword { get; set; }                  // 種別問わず統一パラメータ
    public string DbName { get; set; } = "timetracker";
    public required string TrackerPassword { get; set; }
    public bool DryRun { get; set; }
    public bool Verbose { get; set; }

    // 認証モード（default | azure-cli | sp-env | device-code | managed-identity）
    public string AuthMode { get; set; } = "default";

    // timetracker イメージタグ（バージョン）
    public string TimetrackerTag { get; set; } = "7.0-linux-postgres";

    // コンテナスペック（CPU: vCPU、小数可 / Memory: Gi）
    // デフォルト値を要件に合わせて更新
    public double TimetrackerCpu { get; set; } = 0.75;      // timetracker-tt
    public double TimetrackerMemoryGi { get; set; } = 1.5;  // timetracker-tt
    public double DbCpu { get; set; } = 1.0;                // timetracker-db
    public double DbMemoryGi { get; set; } = 2.0;           // timetracker-db
    public double RedisCpu { get; set; } = 0.5;             // timetracker-redis
    public double RedisMemoryGi { get; set; } = 1.0;        // timetracker-redis

    // Dry-run 用ファイル出力
    public string OutputCompose { get; set; } = "./docker-compose.yml";
}
