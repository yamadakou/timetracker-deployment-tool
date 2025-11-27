namespace Timetracker.Domain.Deployment;

public class DeployOptions
{
    public required string Subscription { get; set; }
    public required string ResourceGroup { get; set; }
    public string Location { get; set; } = Defaults.DefaultLocation; // japaneast
    public string AppName { get; set; } = Defaults.DefaultAppName;   // TimeTracker
    public string DbType { get; set; } = "postgres";                 // postgres | sqlserver
    public required string DbPassword { get; set; }                  // 種別問わず統一パラメータ
    public string DbName { get; set; } = "timetracker";
    public required string TrackerPassword { get; set; }
    public bool DryRun { get; set; }
    public bool Verbose { get; set; }

    // timetracker イメージタグ（バージョン）
    public string TimetrackerTag { get; set; } = "latest";

    // コンテナスペック（CPU: vCPU、小数可 / Memory: Gi）
    public double TimetrackerCpu { get; set; } = 0.5;
    public double TimetrackerMemoryGi { get; set; } = 1.0;
    public double DbCpu { get; set; } = 0.5;
    public double DbMemoryGi { get; set; } = 1.0;
    public double RedisCpu { get; set; } = 0.25;
    public double RedisMemoryGi { get; set; } = 0.5;

    // Dry-run 用ファイル出力
    public string OutputCompose { get; set; } = "./docker-compose.yml";
}
