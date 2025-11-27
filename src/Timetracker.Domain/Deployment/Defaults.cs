namespace Timetracker.Domain.Deployment;

public static class Defaults
{
    public const string DefaultLocation = "japaneast";
    public const string DefaultAppName = "TimeTracker";

    // DockerHub「クイックスタート」準拠の固定 DB ユーザ名（必要に応じて正確な値に更新）
    public const string DbUserFixedPostgres = "postgres";
    public const string DbUserFixedSqlServer = "sa";
}
