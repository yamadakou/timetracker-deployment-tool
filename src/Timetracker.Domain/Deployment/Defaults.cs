namespace Timetracker.Domain.Deployment;

public static class Defaults
{
    public const string DefaultLocation = "japaneast";
    public const string DefaultAppName = "timetracker";

    // timetracker イメージの既定タグ（DB 種別ごと）
    public const string TimetrackerTagPostgres = "7.0-linux-postgres";
    public const string TimetrackerTagSqlServer = "7.0-linux-mssql";

    /// <summary>--tt-tag 未指定時に使う、DB 種別に対応した既定のイメージタグを返す。</summary>
    public static string GetDefaultTimetrackerTag(string dbType) =>
        dbType == "sqlserver" ? TimetrackerTagSqlServer : TimetrackerTagPostgres;

    // DockerHub「クイックスタート」準拠の固定 DB ユーザ名（必要に応じて正確な値に更新）
    public const string DbUserFixedPostgres = "postgres";
    public const string DbUserFixedSqlServer = "sa";

    // デフォルトパスワードおよびデータベース名
    public const string DbPassword = "DefaultDbPassword123!";
    public const string DbName = "timetracker_db";
    public const string TimetrackerPassword = "DefaultTimetrackerPassword123!";
}
