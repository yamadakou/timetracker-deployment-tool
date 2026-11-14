using System.Globalization;
using System.Text;

namespace TTDeployTool.SystemTests.E2E;

/// <summary>
/// サンプル DB の作成に要るスペックの調査（オーナーの依頼、2026-11-14）で、docker compose のコンテナに CPU とメモリの上限を付ける上書きのファイルを作る。
/// 入力は「tt=1.25:2.5,db=0.5:1,redis=0.25:0.5」（vCPU:GiB。書かなかったコンテナは上限なし）。スワップは使わせない（Azure の Container Apps に合わせる）。
/// </summary>
public static class ComposeLimits
{
    private static readonly Dictionary<string, string> Services = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tt"] = "timetracker",
        ["db"] = "db",
        ["redis"] = "redis",
    };

    public static string Override(string limits)
    {
        var sb = new StringBuilder("services:\n");
        foreach (var part in limits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (name, spec) = (part.Split('=')[0], part.Split('=')[1]);
            var cpu = decimal.Parse(spec.Split(':')[0], CultureInfo.InvariantCulture);
            var mib = (int)(decimal.Parse(spec.Split(':')[1], CultureInfo.InvariantCulture) * 1024);
            sb.Append(CultureInfo.InvariantCulture, $"  {Services[name]}:\n    cpus: {cpu}\n    mem_limit: {mib}m\n    memswap_limit: {mib}m\n");
        }
        return sb.ToString();
    }
}
