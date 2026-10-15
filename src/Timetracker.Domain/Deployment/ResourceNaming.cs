namespace Timetracker.Domain.Deployment;

/// <summary>
/// アプリ名から作成するリソース名を組み立てる。リソース名の規則はここに集約する。
/// </summary>
public static class ResourceNaming
{
    public const string EnvironmentSuffix = "-env";
    public const string DbSuffix = "-db";
    public const string RedisSuffix = "-redis";
    public const string TimetrackerSuffix = "-tt";

    /// <summary>アプリ名に付けて作成するリソース名の接尾辞。</summary>
    public static readonly IReadOnlyList<string> Suffixes = new[] { EnvironmentSuffix, DbSuffix, RedisSuffix, TimetrackerSuffix };

    public static string Environment(string appName) => appName + EnvironmentSuffix;
    public static string DbApp(string appName) => appName + DbSuffix;
    public static string RedisApp(string appName) => appName + RedisSuffix;
    public static string TimetrackerApp(string appName) => appName + TimetrackerSuffix;
}
