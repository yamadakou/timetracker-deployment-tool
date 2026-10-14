namespace Timetracker.Domain.Deployment;

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
}
