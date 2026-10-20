using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>DB のデータの永続化オプション（docs/design/db-persistence.md）の画面での扱い。</summary>
public class PersistenceTests
{
    [Fact]
    public void Persistence_Should_Default_To_Keeping_The_Current_State()
    {
        var vm = new Harness().Main.Settings;
        var changes = new ChangeRecorder(vm);

        vm.PersistDb.Should().Be(SettingsViewModel.PersistDbOptions[0]);
        SettingsViewModel.PersistDbOptions.Select(o => o.Value).Should().Equal(null, true, false);
        vm.PersistDb = SettingsViewModel.PersistDbOptions[0];
        changes.Names.Should().BeEmpty();

        vm.PersistDb = SettingsViewModel.PersistDbOptions[1];
        changes.Names.Should().Equal("PersistDb");
        vm.Build(ValidationTarget.Azure).Options.Should().BeNull();
        vm.Build(ValidationTarget.Compose).Warnings.Should().ContainSingle().Which.Should().StartWith("persist-db は Azure 向けの設定");
    }

    [Fact]
    public async Task A_Persisted_Deployment_Should_Show_The_Storage_And_Its_Account()
    {
        var h = await new Harness().ReadyAsync();
        h.Main.Settings.PersistDb = SettingsViewModel.PersistDbOptions[1];
        var vm = h.Main.Deployment;

        await vm.PlanCommand.ExecuteAsync();

        vm.PersistDbText.Should().Be($"DB のデータ: Azure Files に永続化する（ストレージアカウント {FakeAzure.StorageAccountName}）");
        vm.Operations.Select(o => o.Resource).Should().Contain(["ストレージアカウント（DB のデータ）", "ファイル共有（DB のデータ）", "Environment のストレージ"]);
        vm.Warnings.Should().Contain(DbPersistence.StorageNotice(FakeAzure.StorageAccountName));

        await vm.StartCommand.ExecuteAsync();

        h.Main.CurrentJob!.StatusText.Should().Be("完了");
        h.Main.CurrentJob.PersistenceSummary.Should().Be($"DB のデータはストレージアカウント {FakeAzure.StorageAccountName}（Azure Files）に保存しています。");
    }

    [Fact]
    public async Task Updating_A_Persisted_Db_Should_Not_Need_The_Data_Loss_Check()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed(persisted: true);
        var vm = h.Main.Deployment;

        await vm.PlanCommand.ExecuteAsync();

        vm.PersistDbText.Should().StartWith("DB のデータ: Azure Files に永続化する");
        vm.RequiresDataLossAck.Should().BeFalse();
        vm.Warnings.Should().Contain(DbPersistence.RestartNotice("demo-db"));
        vm.CanStart.Should().BeTrue();
    }

    [Fact]
    public async Task Not_Persisting_Should_Be_Shown()
    {
        var h = await new Harness().ReadyAsync();

        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();

        h.Main.Deployment.PersistDbText.Should().BeNull();
        h.Main.CurrentJob!.PersistenceSummary.Should().BeNull();
        h.Main.Settings.PersistDb = SettingsViewModel.PersistDbOptions[2];
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        h.Main.Deployment.PersistDbText.Should().Be("DB のデータ: 永続化しない（DB コンテナの再起動でデータが失われる）");
    }

    [Fact]
    public async Task Deleting_Should_Remove_The_Db_Data_Unless_It_Is_Kept()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed(persisted: true);
        var vm = h.Main.Deletion;

        await vm.PlanCommand.ExecuteAsync();
        vm.Targets.Last().Should().Be(new DeletionRow(FakeAzure.StorageAccountName, "ストレージアカウント（DB のデータ）", "削除する", null, true));
        vm.Warnings.Should().HaveCount(2).And.Contain(DeletionViewModel.DbDataLossWarning);
        vm.Warnings[1].Should().StartWith($"ストレージアカウント {FakeAzure.StorageAccountName}（Azure Files に永続化した DB のデータ）も削除します。");
        vm.KeptText.Should().NotContain("\n");

        vm.KeepDbData = true;
        vm.KeepDbData = true;
        vm.HasPlan.Should().BeFalse();
        await vm.PlanCommand.ExecuteAsync();

        vm.Targets.Last().Should().Be(new DeletionRow(FakeAzure.StorageAccountName, "ストレージアカウント（DB のデータ）", "削除しない", DeletionViewModel.KeptReason, false));
        vm.Warnings.Should().BeEmpty();
        vm.KeptText.Should().EndWith("不要になったら「DB のデータを残す」のチェックを外して削除してください。");
        vm.ConfirmAppName = "demo";
        await vm.StartCommand.ExecuteAsync();

        var job = h.Main.CurrentJob!;
        job.StatusText.Should().Be("完了");
        job.Logs.Last().Should().EndWith("不要になったら「DB のデータを残す」のチェックを外して削除してください。");
    }

    [Fact]
    public async Task The_Status_Should_Show_Where_The_Db_Data_Is()
    {
        var h = await new Harness().ReadyAsync();
        var vm = h.Main.Status;
        vm.PersistenceText.Should().BeNull();

        // 環境が無ければ、永続化の行は出さない（デプロイの途中などで、誤解されないように）
        await vm.RefreshCommand.ExecuteAsync();
        vm.Summary.Should().Be("デプロイされた環境はありません。");
        vm.PersistenceText.Should().BeNull();

        h.Azure.Deployed();
        await vm.RefreshCommand.ExecuteAsync();
        vm.PersistenceText.Should().Be("DB のデータ: 永続化していません（DB コンテナの再起動でデータが失われます）");
        h.Azure.Apps.Clear();
        h.Azure.Environment = null;

        h.Azure.HasStorageAccount = true;
        await vm.RefreshCommand.ExecuteAsync();
        vm.PersistenceText.Should().Be($"DB のデータ: 永続化していません（以前の DB のデータがストレージアカウント {FakeAzure.StorageAccountName} に残っています）");

        h.Azure.AppDetails.Add(new ContainerAppDetails("demo-db", "Succeeded", "Running", "postgres:16", 1, "2Gi", "demo-db--r1", null, FakeAzure.Managed,
            [DbPersistence.StorageName]));
        await vm.RefreshCommand.ExecuteAsync();
        vm.PersistenceText.Should().Be($"DB のデータ: Azure Files に永続化しています（ストレージアカウント {FakeAzure.StorageAccountName}）");
    }
}

public class OperationRowTests
{
    [Theory]
    [InlineData(TTDeployTool.Application.Deployment.PlannedResourceKind.ResourceGroup, TTDeployTool.Application.Deployment.PlannedAction.UseExisting, false, "リソースグループ", "既存のものを使う（変更しない）")]
    [InlineData(TTDeployTool.Application.Deployment.PlannedResourceKind.Environment, TTDeployTool.Application.Deployment.PlannedAction.Create, false, "Container Apps Environment", "作成する")]
    [InlineData(TTDeployTool.Application.Deployment.PlannedResourceKind.StorageAccount, TTDeployTool.Application.Deployment.PlannedAction.UseExisting, false, "ストレージアカウント（DB のデータ）", "既存のものを使う（変更しない）")]
    [InlineData(TTDeployTool.Application.Deployment.PlannedResourceKind.FileShare, TTDeployTool.Application.Deployment.PlannedAction.Create, false, "ファイル共有（DB のデータ）", "作成する")]
    [InlineData(TTDeployTool.Application.Deployment.PlannedResourceKind.EnvironmentStorage, TTDeployTool.Application.Deployment.PlannedAction.Update, false, "Environment のストレージ", "更新する")]
    [InlineData(TTDeployTool.Application.Deployment.PlannedResourceKind.ContainerApp, TTDeployTool.Application.Deployment.PlannedAction.Update, true, "Container App", "更新する（DB のデータが失われるおそれがあります）")]
    // 定義に無い値は Container App として表示する
    [InlineData((TTDeployTool.Application.Deployment.PlannedResourceKind)99, TTDeployTool.Application.Deployment.PlannedAction.Create, false, "Container App", "作成する")]
    public void Every_Resource_And_Action_Should_Have_A_Label(
        TTDeployTool.Application.Deployment.PlannedResourceKind kind, TTDeployTool.Application.Deployment.PlannedAction action, bool dataLoss, string resource, string label)
    {
        DeploymentViewModel.ToRow(new(kind, "x", action, dataLoss)).Should().Be(new OperationRow(resource, "x", label, dataLoss));
    }
}
