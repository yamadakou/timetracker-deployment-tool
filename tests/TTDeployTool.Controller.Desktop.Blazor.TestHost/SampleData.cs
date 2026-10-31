using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.TestHost;

/// <summary>見本のモックのデータ（デプロイ 45 件・終わったジョブの記録 30 件。どちらも、ファイルが無いときだけ置く）。</summary>
public static class SampleData
{
    public const int Deployments = 45;

    public const int Records = 30;

    public static void WriteIfMissing(string directory, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        var fixture = Path.Combine(directory, FixtureData.FileName);
        if (!File.Exists(fixture))
            File.WriteAllText(fixture, FixtureSamples.Data(Deployments, now).ToJson());
        var history = Path.Combine(directory, "job-history.json");
        if (!File.Exists(history))
            new FileJobHistory(history).Records = FixtureSamples.JobRecords(Records, now);
    }
}
