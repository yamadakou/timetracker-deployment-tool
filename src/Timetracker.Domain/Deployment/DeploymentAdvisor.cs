using System.Globalization;

namespace Timetracker.Domain.Deployment;

/// <summary>
/// エラーではないが、利用者に知らせておくべき注意点（README の「本ツールの目的と注意事項」に対応）。
/// </summary>
public static class DeploymentAdvisor
{
    /// <summary>この値以下のメモリでは、サンプル DB の作成が OutOfMemory で失敗することがある。</summary>
    public const double LowTimetrackerMemoryGi = 2.0;

    public static IReadOnlyList<string> GetWarnings(DeployOptions o, ValidationTarget target)
    {
        var warnings = new List<string>();
        if (o.DbType == "sqlserver" && target == ValidationTarget.Azure)
            warnings.Add("Azure 上の SQL Server 構成ではサンプル DB を作成できません（Timetracker のセットアップで空の DB を選択してください）。");
        if (o.TimetrackerMemoryGi <= LowTimetrackerMemoryGi)
            warnings.Add(
                $"tt-memory が {o.TimetrackerMemoryGi.ToString(CultureInfo.InvariantCulture)}Gi です。" +
                $"{LowTimetrackerMemoryGi.ToString(CultureInfo.InvariantCulture)}Gi 以下ではサンプル DB の作成が OutOfMemory で失敗することがあります" +
                $"（{Defaults.TimetrackerMemoryGi.ToString(CultureInfo.InvariantCulture)}Gi 以上を推奨）。");
        if ((o.DbType is "postgresql" or "sqlserver") && o.TimetrackerTag != Defaults.GetDefaultTimetrackerTag(o.DbType))
            warnings.Add(
                $"tt-tag '{o.TimetrackerTag}' は既定のタグ（{Defaults.GetDefaultTimetrackerTag(o.DbType)}）ではありません。" +
                "本ツールで動作を確認しているのは既定のタグです。");
        return warnings;
    }
}
