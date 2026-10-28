using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.SystemTests;

/// <summary>
/// DB データの永続化（--persist-db）の一連の流れを、本物の Azure で確認する（詳細設計 docs/design/common/db-persistence.md の 10・14）。
/// デプロイ → 再デプロイ（停止 → 更新 → 開始）→ 削除（--keep-db-data）→ 削除（既定）。DB は PostgreSQL。
/// DB のデータが残っていることは、ファイル共有の上の PostgreSQL のファイルで確かめる: PG_VERSION は初期化（initdb）のときにだけ書かれ、
/// postmaster.pid は起動のたびに作られる（正常に停止すると消える）。再デプロイの開始より後に作られた postmaster.pid があり、
/// PG_VERSION が変わらなければ、新しい DB コンテナが同じデータを初期化せずに使っている。
/// 実行ごとに専用のリソースグループを作り、最後に削除する（所要時間 20〜30 分。Environment の削除に時間がかかる）。
/// </summary>
public sealed class DbPersistenceLifecycleTests
{
    private const string PgVersion = "pgdata/PG_VERSION";
    private const string PostmasterPid = "pgdata/postmaster.pid";

    [Fact(Timeout = 90 * 60 * 1000)]
    public async Task Persist_Redeploy_Keep_And_Delete()
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
        var account = ResourceNaming.StorageAccount(subscription, rg, app);
        var share = ResourceNaming.DbFileShare(app);
        var db = ResourceNaming.DbApp(app);

        await azure.CreateResourceGroupAsync(rg, SystemTestSettings.Location, ct);
        try
        {
            // 1. --persist-db でデプロイ: ストレージアカウント（管理タグ付き）を作り、DB アプリが Azure Files をマウントし、DB を共有の上に初期化する
            var deploy = await cli.DeployAsync(app, password, "--persist-db");
            deploy.ExitCode.Should().Be(0, deploy.ToString());
            deploy.Output.Should().Contain("[VERBOSE] 権限の事前確認: OK").And.Contain(DbPersistence.StorageNotice(account));
            (await azure.GetStorageAccountTagsAsync(rg, account, ct)).Should().NotBeNull().And.Contain(ManagedTags.For(app));
            (await azure.GetContainerAppStorageNamesAsync(rg, db, ct)).Should().Equal(DbPersistence.StorageName);
            var initialized = await azure.WaitForShareFileAsync(rg, account, share, PgVersion, t => t != null, TimeSpan.FromMinutes(10), ct);
            initialized.Should().NotBeNull("DB のデータのディレクトリが共有の上に作られる");
            (await azure.WaitForShareFileAsync(rg, account, share, PostmasterPid, t => t != null, TimeSpan.FromMinutes(5), ct))
                .Should().NotBeNull("DB が共有の上のデータで動いている");

            // 2. --persist-db を省略し、設定を変えて再デプロイ: 永続化を引き継ぎ、停止 → 更新 → 開始で、データ消失の警告は出ない
            var redeployStarted = DateTimeOffset.UtcNow;
            var redeploy = await cli.DeployAsync(app, password, "--db-cpu", "1.25", "--db-memory", "2.5");
            redeploy.ExitCode.Should().Be(0, redeploy.ToString());
            redeploy.Output.Should().Contain(DbPersistence.RestartNotice(db)).And.Contain($"DB アプリを開始: {db}")
                .And.NotContain($"[WARN] DB アプリ '{db}'");
            (await azure.GetContainerAppStorageNamesAsync(rg, db, ct)).Should().Equal(DbPersistence.StorageName);
            var restarted = await azure.WaitForShareFileAsync(rg, account, share, PostmasterPid, t => t > redeployStarted, TimeSpan.FromMinutes(10), ct);
            restarted.Should().BeAfter(redeployStarted, "新しい DB コンテナが共有の上のデータで起動する");
            (await azure.GetShareFileLastModifiedAsync(rg, account, share, PgVersion, ct)).Should().Be(initialized, "DB を初期化し直していない");

            // 3. --keep-db-data で削除: Container App と Environment は削除し、ストレージアカウントは残す
            var keep = await cli.DeleteAsync(app, null, "--keep-db-data", "--yes");
            keep.ExitCode.Should().Be(0, keep.ToString());
            keep.Output.Should().Contain($"削除しない   {account} (ストレージアカウント)").And.Contain("料金がかかり続けます");
            (await azure.WaitForResourceNamesAsync(rg, names => names.Count == 0, ct)).Should().BeEmpty();
            (await azure.GetStorageAccountTagsAsync(rg, account, ct)).Should().NotBeNull();
            (await azure.GetShareFileLastModifiedAsync(rg, account, share, PgVersion, ct)).Should().Be(initialized, "DB のデータは残る");

            // 4. 既定の削除: 残したストレージアカウントも削除する。リソースグループは残る
            var delete = await cli.DeleteAsync(app, null, "--yes");
            delete.ExitCode.Should().Be(0, delete.ToString());
            delete.Output.Should().Contain($"削除する     {account} (ストレージアカウント)");
            (await azure.GetStorageAccountTagsAsync(rg, account, ct)).Should().BeNull();
            (await azure.ResourceGroupExistsAsync(rg, ct)).Should().BeTrue();
        }
        finally
        {
            await azure.BeginDeleteResourceGroupAsync(rg, CancellationToken.None);
        }
    }
}
