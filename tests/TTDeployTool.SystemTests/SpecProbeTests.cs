using System.Diagnostics;
using FluentAssertions;
using Microsoft.Playwright;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.SystemTests.E2E;
using Xunit;

namespace TTDeployTool.SystemTests;

/// <summary>
/// サンプル DB の作成に要るスペックの Azure での確認（オーナーの依頼、2026-11-14。docs/design/desktop/desktop-blazor-hybrid.md の 16.14）。
/// TT_AZURE_SPEC_PROBE（「tt=1.25:2.5,db=0.5:1.0,redis=0.25:0.5」。vCPU:GiB）のスペックでデプロイし、セットアップウィザードでサンプルデータベースを作って
/// ログインできるかを確かめる。成否とかかった時間を TT_E2E_PROBE_OUT のファイルに書く。指定が無ければスキップする（system-test.yml の spec_probe）。
/// 実行ごとに専用のリソースグループを作り、最後に削除する。
/// </summary>
public sealed class SpecProbeTests
{
    [Fact(Timeout = 80 * 60 * 1000)]
    public async Task Sample_Database_Should_Be_Created_With_The_Given_Spec()
    {
        var spec = Environment.GetEnvironmentVariable("TT_AZURE_SPEC_PROBE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(spec), "TT_AZURE_SPEC_PROBE が無いためスキップします。");
        var (licenseKey, user) = E2ESettings.Require();
        var ct = TestContext.Current.CancellationToken;
        var subscription = SystemTestSettings.RequireSubscription();
        var azure = new AzureTestEnvironment(subscription);
        var rg = AzureTestEnvironment.NewResourceGroupName();
        var app = AzureTestEnvironment.NewAppName();
        var password = AzureTestEnvironment.NewPassword();
        var cli = new CliRunner(subscription, rg);
        var args = Args(spec!);
        var stopwatch = Stopwatch.StartNew();
        var result = "失敗（デプロイ）";

        await azure.CreateResourceGroupAsync(rg, SystemTestSettings.Location, ct);
        try
        {
            var deploy = await cli.DeployAsync(app, password, args);
            deploy.ExitCode.Should().Be(0, deploy.ToString());
            var fqdn = await azure.GetContainerAppFqdnAsync(rg, ResourceNaming.TimetrackerApp(app), ct);
            await AzureTestEnvironment.WaitForHttpResponseAsync($"https://{fqdn}", TimeSpan.FromMinutes(10), ct);
            var deployed = stopwatch.Elapsed;
            result = "失敗（サンプル DB）";

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await TimetrackerSetupWizard.LaunchBrowserAsync(playwright);
            var wizard = new TimetrackerSetupWizard(await browser.NewPageAsync(new() { Locale = "ja-JP" }));
            await wizard.CreateSampleDatabaseAsync($"https://{fqdn}", licenseKey, TimeSpan.FromMinutes(30));
            var home = await wizard.LoginFromCompletionAsync(user);
            TimetrackerSetupWizard.ActiveUserCount(home).Should().BeGreaterThan(1, home);
            result = $"成功（デプロイ {deployed:mm\\:ss}、サンプル DB {stopwatch.Elapsed - deployed:mm\\:ss}）";
        }
        finally
        {
            var line = $"{spec!.Trim()} {result} 合計 {stopwatch.Elapsed:mm\\:ss}";
            TestContext.Current.TestOutputHelper?.WriteLine(line);
            if (Environment.GetEnvironmentVariable("TT_E2E_PROBE_OUT") is { Length: > 0 } outFile)
                await File.AppendAllTextAsync(outFile, line + "\n", CancellationToken.None);
            await azure.BeginDeleteResourceGroupAsync(rg, CancellationToken.None);
        }
    }

    // 「tt=1.25:2.5,db=0.5:1.0,redis=0.25:0.5」→ --tt-cpu 1.25 --tt-memory 2.5 …
    internal static string[] Args(string spec) =>
    [
        .. spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SelectMany(part =>
        {
            var (name, values) = (part.Split('=')[0], part.Split('=')[1].Split(':'));
            return new[] { $"--{name}-cpu", values[0], $"--{name}-memory", values[1] };
        }),
    ];
}
