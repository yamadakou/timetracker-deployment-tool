namespace Timetracker.Domain.Deployment;

public static class Defaults
{
    public const string DefaultLocation = "japaneast";
    public const string DefaultAppName = "timetracker";

    // DockerHub「クイックスタート」準拠の固定 DB ユーザ名（必要に応じて正確な値に更新）
    public const string DbUserFixedPostgres = "postgres";
    public const string DbUserFixedSqlServer = "sa";

    // テスト用のデフォルト値
    public const string DbPassword = "testDbPassword123!";
    public const string DbName = "timetracker";
    public const string TimetrackerPassword = "testTimetrackerPassword123!";
}
