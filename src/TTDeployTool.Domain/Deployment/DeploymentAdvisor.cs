using System.Globalization;

namespace TTDeployTool.Domain.Deployment;

/// <summary>
/// エラーではないが、利用者に知らせておくべき注意点（README の「本ツールの目的と注意事項」に対応）。
/// </summary>
public static class DeploymentAdvisor
{
    /// <summary>
    /// サンプル DB を作れる実用上の最低スペック（vCPU / GiB。オーナーの判断、2026-11-14。docs/design/desktop/desktop-blazor-hybrid.md の 16.14）。
    /// TimeTracker は 2Gi 以下だと復元の SQL を読み込むところで OutOfMemory で失敗する。DB は 0.25 vCPU でも作れるが、作成に約 1.6 倍の時間がかかる。
    /// </summary>
    public static readonly IReadOnlyList<SampleDbMinimum> SampleDbMinimums =
    [
        new("TimeTracker", "tt", 1.25, 2.5),
        new("DB", "db", 0.5, 1.0),
        new("Redis", "redis", 0.25, 0.5),
    ];

    /// <summary>実用上の最低スペックの文（「TimeTracker 1.25 vCPU / 2.5Gi、DB 0.5 vCPU / 1Gi、Redis 0.25 vCPU / 0.5Gi」）。</summary>
    public static string SampleDbMinimumText => string.Join("、", SampleDbMinimums.Select(m => $"{m.Name} {m.Text}"));

    /// <summary>名前（TimeTracker・DB・Redis）の最低スペック。</summary>
    public static SampleDbMinimum SampleDbMinimumOf(string name) => SampleDbMinimums.Single(m => m.Name == name);

    public static IReadOnlyList<string> GetWarnings(DeployOptions o, ValidationTarget target)
    {
        var warnings = new List<string>();
        if (o.DbType == "sqlserver" && target == ValidationTarget.Azure)
            warnings.Add("Azure 上の SQL Server 構成ではサンプル DB を作成できません（TimeTracker のセットアップで空の DB を選択してください）。");
        // スペックは Azure でだけ使う（docker-compose にはスペックの上限を書かない）
        if (target == ValidationTarget.Azure)
        {
            foreach (var (minimum, cpu, memory) in new[]
                     {
                         (SampleDbMinimums[0], o.TimetrackerCpu, o.TimetrackerMemoryGi), (SampleDbMinimums[1], o.DbCpu, o.DbMemoryGi),
                         (SampleDbMinimums[2], o.RedisCpu, o.RedisMemoryGi),
                     })
            {
                if (minimum.IsBelow(cpu, memory))
                    warnings.Add(
                        $"{minimum.Option}-cpu / {minimum.Option}-memory（{minimum.Name}）が {SampleDbMinimum.Format(cpu, memory)} です。" +
                        $"サンプル DB を作れる実用上の最低スペック（{SampleDbMinimumText}）を下回るため、サンプル DB の作成ができない場合があります。");
            }
        }
        if ((o.DbType is "postgresql" or "sqlserver") && o.TimetrackerTag != Defaults.GetDefaultTimetrackerTag(o.DbType))
            warnings.Add(
                $"tt-tag '{o.TimetrackerTag}' は既定のタグ（{Defaults.GetDefaultTimetrackerTag(o.DbType)}）ではありません。" +
                "本ツールで動作を確認しているのは既定のタグです。");
        if (o.PersistDb == true && target == ValidationTarget.Compose)
            warnings.Add("persist-db は Azure 向けの設定のため、ローカル（Dry-run）では使いません（docker-compose は名前付きボリュームで DB のデータを保持します）。");
        return warnings;
    }
}

/// <summary>サンプル DB を作れる実用上の最低スペック（<see cref="Option"/> は CLI のオプションの頭: tt・db・redis）。</summary>
public sealed record SampleDbMinimum(string Name, string Option, double Cpu, double MemoryGi)
{
    /// <summary>「1.25 vCPU / 2.5Gi」。</summary>
    public string Text => Format(Cpu, MemoryGi);

    /// <summary>CPU かメモリが最低を下回るか。</summary>
    public bool IsBelow(double cpu, double memoryGi) => cpu < Cpu || memoryGi < MemoryGi;

    public static string Format(double cpu, double memoryGi) =>
        $"{cpu.ToString(CultureInfo.InvariantCulture)} vCPU / {memoryGi.ToString(CultureInfo.InvariantCulture)}Gi";
}
