namespace Timetracker.Domain.Deployment;

/// <summary>
/// 未指定の項目に既定値を適用して <see cref="DeployOptions"/> を作る。
/// CLI と他のフロントエンドで既定値の解決がずれないよう、解決はここで行う。
/// 値の検証は行わない（<see cref="ComposeGenerator.Validate"/> / <see cref="AppNameValidator"/> で行う）。
/// </summary>
public static class DeployOptionsResolver
{
    public const string DefaultDbType = "postgresql";
    public const string DefaultAuthMode = "default";

    public static DeployOptions Resolve(DeployInput input)
    {
        var dbType = OrDefault(input.DbType, DefaultDbType);

        return new DeployOptions
        {
            Subscription        = OrDefault(input.Subscription, string.Empty),
            ResourceGroup       = OrDefault(input.ResourceGroup, string.Empty),
            Location            = OrDefault(input.Location, Defaults.DefaultLocation),
            // Container Apps のリソース名は英小文字のみのため小文字に揃える
            AppName             = OrDefault(input.AppName, Defaults.DefaultAppName).ToLowerInvariant(),
            DbType              = dbType,
            DbPassword          = OrDefault(input.DbPassword, string.Empty),
            DbName              = OrDefault(input.DbName, Defaults.DbName),
            TrackerPassword     = OrDefault(input.TrackerPassword, string.Empty),
            // 未指定時は DB 種別に合わせる
            TimetrackerTag      = OrDefault(input.TimetrackerTag, Defaults.GetDefaultTimetrackerTag(dbType)),
            DryRun              = input.DryRun,
            TimetrackerCpu      = input.TimetrackerCpu ?? Defaults.TimetrackerCpu,
            TimetrackerMemoryGi = input.TimetrackerMemoryGi ?? Defaults.TimetrackerMemoryGi,
            DbCpu               = input.DbCpu ?? Defaults.DbCpu,
            DbMemoryGi          = input.DbMemoryGi ?? Defaults.DbMemoryGi,
            RedisCpu            = input.RedisCpu ?? Defaults.RedisCpu,
            RedisMemoryGi       = input.RedisMemoryGi ?? Defaults.RedisMemoryGi,
            Verbose             = input.Verbose,
            PersistDb           = input.PersistDb,
            AuthMode            = OrDefault(input.AuthMode, DefaultAuthMode),
        };
    }

    private static string OrDefault(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value;
}
