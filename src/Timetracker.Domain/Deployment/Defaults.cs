namespace Timetracker.Domain.Deployment;

public static class Defaults
{
    public const string DefaultLocation = "japaneast";
    public const string DefaultAppName = "timetracker";

    // timetracker イメージの既定タグ（DB 種別ごと）
    public const string TimetrackerTagPostgres = "11.0.1-linux-postgres";
    public const string TimetrackerTagSqlServer = "11.0.1-linux-mssql";

    /// <summary>--tt-tag 未指定時に使う、DB 種別に対応した既定のイメージタグを返す。</summary>
    public static string GetDefaultTimetrackerTag(string dbType) =>
        dbType == "sqlserver" ? TimetrackerTagSqlServer : TimetrackerTagPostgres;

    // DockerHub「クイックスタート」準拠の固定 DB ユーザ名（必要に応じて正確な値に更新）
    public const string DbUserFixedPostgres = "postgres";
    public const string DbUserFixedSqlServer = "sa";

    // データベース名
    public const string DbName = "timetracker";

    // コンテナスペックの既定値（CPU: vCPU / Memory: Gi）。DeployOptions と CLI の両方がここを参照する
    // timetracker-tt は 2Gi 以下だとサンプル DB の復元（約 155MB の SQL）が OutOfMemoryException で失敗するため 1.5 / 3.0 とする
    public const double TimetrackerCpu = 1.5;
    public const double TimetrackerMemoryGi = 3.0;
    public const double DbCpu = 1.0;
    public const double DbMemoryGi = 2.0;
    public const double RedisCpu = 0.5;
    public const double RedisMemoryGi = 1.0;
}
