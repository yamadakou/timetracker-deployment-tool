using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.SystemTests;

/// <summary>
/// 権限の少ないサービスプリンシパル（専用のリソースグループに Reader と Container Apps Contributor だけを割り当てたもの）で、
/// 権限の事前確認が実際の Azure の挙動と合っていることを確認する。
/// 準備（サービスプリンシパルとリソースグループ）は docs/SYSTEM_TESTS.md を参照。
/// </summary>
public sealed class LimitedPermissionTests
{
    [Fact(Timeout = 60 * 60 * 1000)]
    public async Task Precheck_Should_Match_Azure_Behavior()
    {
        var ct = TestContext.Current.CancellationToken;
        var subscription = SystemTestSettings.RequireSubscription();
        var (tenantId, clientId, clientSecret) = SystemTestSettings.RequireLimitedServicePrincipal();
        var rg = SystemTestSettings.LimitedResourceGroup;
        var azure = new AzureTestEnvironment(subscription);
        var privileged = new CliRunner(subscription, rg);
        var limited = new CliRunner(subscription, rg, CliRunner.ServicePrincipal(tenantId, clientId, clientSecret));
        var password = AzureTestEnvironment.NewPassword();

        (await azure.ResourceGroupExistsAsync(rg, ct)).Should().BeTrue($"リソースグループ {rg} を事前に作成してください（docs/SYSTEM_TESTS.md）");
        // 中断された前回のテストが残したアプリを削除する（このリソースグループは削除しないため）
        foreach (var leftover in await azure.ListLeftoverTestAppNamesAsync(rg, ct))
        {
            var sweep = await privileged.DeleteAsync(leftover, null, "--include-untagged", "--yes");
            sweep.ExitCode.Should().Be(0, $"前回のテストが残したアプリ {leftover} を削除できませんでした\n{sweep}");
        }

        var app = AzureTestEnvironment.NewAppName();
        var newApp = AzureTestEnvironment.NewAppName();
        var completed = false;
        try
        {
            // 準備: 十分な権限の ID で Environment を含めてデプロイする
            var setup = await privileged.DeployAsync(app, password);
            setup.ExitCode.Should().Be(0, setup.ToString());

            // 1. Environment の新規作成が必要なデプロイは、事前確認で止まり何も作成しない
            var blockedDeploy = await limited.DeployAsync(newApp, password);
            blockedDeploy.ExitCode.Should().Be(1, blockedDeploy.ToString());
            blockedDeploy.Error.Should().Contain("権限が不足しています: Microsoft.App/managedEnvironments/write");
            (await azure.ListResourceNamesAsync(rg, ct)).Should().NotContain(n => n.StartsWith(newApp + "-", StringComparison.Ordinal));

            // 2. 既存の Environment への Container App の更新は、事前確認を通り実際に成功する
            //    （Container Apps Contributor の containerApps/*/write で containerApps/write が許可されることの確認）
            var allowedDeploy = await limited.DeployAsync(app, password);
            allowedDeploy.ExitCode.Should().Be(0, allowedDeploy.ToString());
            allowedDeploy.Output.Should().Contain("[VERBOSE] 権限の事前確認: OK");

            // 3. Environment の削除権限が無いため、削除は事前確認で止まり何も削除しない
            var blockedDelete = await limited.DeleteAsync(app, null, "--yes");
            blockedDelete.ExitCode.Should().Be(1, blockedDelete.ToString());
            blockedDelete.Error.Should().Contain("権限が不足しています: Microsoft.App/managedEnvironments/delete");
            (await azure.ListResourceNamesAsync(rg, ct)).Should().Contain(new[]
            {
                ResourceNaming.TimetrackerApp(app), ResourceNaming.RedisApp(app), ResourceNaming.DbApp(app), ResourceNaming.Environment(app),
            });
            completed = true;
        }
        finally
        {
            // 後片付け（このリソースグループ自体は残す）。両方の削除を試み、失敗があればテストを失敗にする
            // （料金が発生するリソースが残ったままテストが成功しないように）。
            // テスト本体が失敗している場合は、その失敗を隠さないよう、後片付けの失敗は出力に書くだけにする
            var failures = new List<CliResult>();
            foreach (var name in new[] { app, newApp })
            {
                var cleanup = await privileged.DeleteAsync(name, null, "--include-untagged", "--yes");
                if (cleanup.ExitCode != 0)
                    failures.Add(cleanup);
            }
            if (failures.Count > 0)
            {
                var message = $"後片付けに失敗しました。{rg} に残ったリソースを削除してください。\n{string.Join("\n", failures)}";
                if (completed)
                    Assert.Fail(message);
                TestContext.Current.TestOutputHelper?.WriteLine(message);
            }
        }
    }
}
