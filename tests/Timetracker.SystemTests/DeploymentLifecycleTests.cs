using FluentAssertions;
using Microsoft.Playwright;
using Timetracker.Domain.Deployment;
using Timetracker.SystemTests.E2E;
using Xunit;

namespace Timetracker.SystemTests;

/// <summary>
/// デプロイ → 管理タグ → 再デプロイ（利用者のタグの引き継ぎ）→ 削除計画 → 削除 の一連の流れを、本物の Azure で確認する。
/// TT_E2E_LICENSE_KEY がある場合は、デプロイの後にセットアップウィザードでサンプルデータベースを作り、管理ユーザーでログインできることも確かめる。
/// 実行ごとに専用のリソースグループを作り、最後に削除する（所要時間 20〜40 分。Environment の削除とサンプル DB の作成に時間がかかる）。
/// </summary>
public sealed class DeploymentLifecycleTests
{
    [Fact(Timeout = 90 * 60 * 1000)]
    public async Task Deploy_Redeploy_And_Delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var subscription = SystemTestSettings.RequireSubscription();
        var azure = new AzureTestEnvironment(subscription);
        var swept = await azure.SweepLeftoverResourceGroupsAsync(ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"中断されたテストのリソースグループを削除: {string.Join(", ", swept)}");

        var rg = AzureTestEnvironment.NewResourceGroupName();
        var app = AzureTestEnvironment.NewAppName();
        var password = AzureTestEnvironment.NewPassword();
        var cli = new CliRunner(subscription, rg);
        string Db = ResourceNaming.DbApp(app), Redis = ResourceNaming.RedisApp(app), Tt = ResourceNaming.TimetrackerApp(app), Env = ResourceNaming.Environment(app);

        await azure.CreateResourceGroupAsync(rg, SystemTestSettings.Location, ct);
        try
        {
            // 1. デプロイ: 4 つのリソースに管理タグが付き、Timetracker が HTTP で応答する
            var deploy = await cli.DeployAsync(app, password);
            deploy.ExitCode.Should().Be(0, deploy.ToString());
            deploy.Output.Should().Contain("[VERBOSE] 権限の事前確認: OK");
            (await azure.ListResourceNamesAsync(rg, ct)).Should().Equal(new[] { Db, Env, Redis, Tt }.Order(StringComparer.Ordinal));
            foreach (var name in new[] { Db, Redis, Tt })
            {
                (await azure.GetContainerAppTagsAsync(rg, name, ct)).Should().Contain(ManagedTags.For(app), name);
            }
            (await azure.GetEnvironmentTagsAsync(rg, Env, ct)).Should().Contain(ManagedTags.For(app));
            var fqdn = await azure.GetContainerAppFqdnAsync(rg, Tt, ct);
            fqdn.Should().NotBeNullOrEmpty();
            var status = await AzureTestEnvironment.WaitForHttpResponseAsync($"https://{fqdn}", TimeSpan.FromMinutes(10), ct);
            TestContext.Current.TestOutputHelper?.WriteLine($"https://{fqdn} → {status}");

            // 1b. セットアップウィザードでサンプルデータベースを作り、管理ユーザーでログインできる（Azure の既定のスペックで足りること）
            if (E2ESettings.LicenseKey is { } licenseKey)
            {
                using var playwright = await Playwright.CreateAsync();
                await using var browser = await TimetrackerSetupWizard.LaunchBrowserAsync(playwright);
                var wizard = new TimetrackerSetupWizard(await browser.NewPageAsync(new() { Locale = "ja-JP" }));
                var admin = E2ESettings.NewAdmin();
                await wizard.RunAsync($"https://{fqdn}", licenseKey, admin, sampleDatabase: true, TimeSpan.FromMinutes(30));
                (await wizard.LoginFromCompletionAsync(admin)).Should().NotBeNullOrWhiteSpace();
            }
            else
            {
                TestContext.Current.TestOutputHelper?.WriteLine("TT_E2E_LICENSE_KEY が未設定のため、セットアップウィザードとログインの確認を省略します。");
            }

            // 2. 再デプロイ: 利用者が付けたタグが残る（No.33）
            await azure.AddContainerAppTagAsync(rg, Tt, "owner", "systemtest", ct);
            var redeploy = await cli.DeployAsync(app, password);
            redeploy.ExitCode.Should().Be(0, redeploy.ToString());
            (await azure.GetContainerAppTagsAsync(rg, Tt, ct)).Should().Contain("owner", "systemtest").And.Contain(ManagedTags.For(app));

            // 3. DB アプリの管理タグを外す（同じ名前の別リソースに相当）→ DB アプリと Environment は削除の対象外
            await azure.RemoveContainerAppTagAsync(rg, Db, ManagedTags.ManagedByKey, ct);
            var plan = await cli.DeleteAsync(app, null, "--dry-run");
            plan.ExitCode.Should().Be(0, plan.ToString());
            plan.Output.Should().Contain($"削除する     {Tt} (Container App)")
                .And.Contain($"削除する     {Redis} (Container App)")
                .And.Contain($"削除しない   {Db} (Container App): 管理タグがありません")
                .And.Contain($"削除しない   {Env} (Environment): Environment 内に削除しない Container App（{Db}）が残るため")
                .And.Contain("--include-untagged を指定してください");

            var planUntagged = await cli.DeleteAsync(app, null, "--dry-run", "--include-untagged");
            planUntagged.Output.Should().Contain($"削除する     {Db} (Container App) ※管理タグなし").And.Contain($"削除する     {Env} (Environment)");
            (await azure.ListResourceNamesAsync(rg, ct)).Should().HaveCount(4, "Dry-run では何も削除しない");

            // 4. 確認の入力が一致しなければ中止する
            var cancelled = await cli.DeleteAsync(app, "wrong\n");
            cancelled.ExitCode.Should().Be(1, cancelled.ToString());
            cancelled.Error.Should().Contain("削除を中止しました");
            (await azure.ListResourceNamesAsync(rg, ct)).Should().HaveCount(4);

            // 5. 確認して削除: 管理タグがあるものだけ削除される
            var partial = await cli.DeleteAsync(app, app + "\n");
            partial.ExitCode.Should().Be(0, partial.ToString());
            var remaining = new[] { Db, Env }.Order(StringComparer.Ordinal).ToList();
            (await azure.WaitForResourceNamesAsync(rg, names => names.SequenceEqual(remaining), ct)).Should().Equal(remaining);

            // 6. 残りを --include-untagged で削除。リソースグループは残る
            var rest = await cli.DeleteAsync(app, null, "--include-untagged", "--yes");
            rest.ExitCode.Should().Be(0, rest.ToString());
            (await azure.WaitForResourceNamesAsync(rg, names => names.Count == 0, ct)).Should().BeEmpty();
            (await azure.ResourceGroupExistsAsync(rg, ct)).Should().BeTrue();

            // 7. 再実行しても失敗しない
            var again = await cli.DeleteAsync(app, null, "--yes");
            again.ExitCode.Should().Be(0, again.ToString());
            again.Output.Should().Contain("削除するリソースはありません。");
        }
        finally
        {
            await azure.BeginDeleteResourceGroupAsync(rg, CancellationToken.None);
        }
    }
}
