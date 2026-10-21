using System.ComponentModel;
using Azure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>実機の確認で見つかった問題（詳細設計 desktop-jobs.md の 3。P1〜P4）と、処理中の表示。</summary>
public class RealDeviceFixesTests
{
    // 処理中の表示の段階を、表示した順に集める
    private static List<string> Record(BusyState busy)
    {
        var texts = new List<string>();
        busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BusyState.Text) && busy.Text.Length > 0)
                texts.Add(busy.Text);
        };
        return texts;
    }

    [Fact]
    public async Task Errors_Should_Be_Shown_And_Hidden_With_HasError()
    {
        // P1: 表示の切り替えは、エラーがあるか（bool）で行う
        var h = new Harness();
        h.SignIn.SignInError = new InvalidOperationException("WAM が使えません");
        await h.Main.SignIn.SignInCommand.ExecuteAsync();
        h.Main.SignIn.HasError.Should().BeTrue();
        h.SignIn.SignInError = null;
        await h.Main.SignIn.SignInCommand.ExecuteAsync();
        h.Main.SignIn.HasError.Should().BeFalse();

        h.Azure.TenantsError = new RequestFailedException(401, "だめ");
        await h.Main.Target.LoadCommand.ExecuteAsync();
        h.Main.Target.HasError.Should().BeTrue();
        h.Azure.TenantsError = null;
        await h.Main.Target.LoadCommand.ExecuteAsync();
        h.Main.Target.HasError.Should().BeFalse();

        await h.ReadyAsync();
        var notified = new List<string?>();
        h.Main.Deployment.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        h.Main.Settings.DbPassword = "";
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        h.Main.Deployment.HasError.Should().BeTrue();
        h.Main.Settings.DbPassword = "DbPassw0rd!";
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        h.Main.Deployment.HasError.Should().BeFalse();
        notified.Count(n => n == nameof(h.Main.Deployment.HasError)).Should().Be(2, "エラーが出たときと消えたときに、表示の切り替えを知らせる");

        h.Main.Target.AppName = "";
        await h.Main.Deletion.PlanCommand.ExecuteAsync();
        await h.Main.Status.RefreshCommand.ExecuteAsync();
        h.Main.Deletion.HasError.Should().BeTrue();
        h.Main.Status.HasError.Should().BeTrue();
        h.Main.Target.AppName = "demo";
        await h.Main.Deletion.PlanCommand.ExecuteAsync();
        await h.Main.Status.RefreshCommand.ExecuteAsync();
        h.Main.Deletion.HasError.Should().BeFalse();
        h.Main.Status.HasError.Should().BeFalse();

        h.Main.Settings.DbPassword = "";
        await h.Main.DryRun.SaveCommand.ExecuteAsync();
        h.Main.DryRun.HasError.Should().BeTrue();
        h.Main.DryRun.HasComposeContent.Should().BeFalse();
    }

    [Fact]
    public async Task The_Dry_Run_Result_And_The_App_Name_Box_Should_Follow_Their_Values()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ttdeploytool-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var h = await new Harness().ReadyAsync();
            h.Dialogs.Folder = folder;
            await h.Main.DryRun.SaveCommand.ExecuteAsync();
            h.Main.DryRun.HasComposeContent.Should().BeTrue();
            h.Main.DryRun.HasError.Should().BeFalse();

            h.Azure.Deployed();
            h.Main.Deletion.NeedsAppNameConfirmation.Should().BeFalse();
            await h.Main.Deletion.PlanCommand.ExecuteAsync();
            h.Main.Deletion.NeedsAppNameConfirmation.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_Status_Should_Say_When_A_Job_For_The_Target_Is_Running()
    {
        // P2: 実行中のジョブがあれば、まだ反映されていない場合がある旨を添える
        var release = new TaskCompletionSource();
        var h = await new Harness().ReadyAsync();
        await h.Main.Status.RefreshCommand.ExecuteAsync();
        h.Main.Status.Note.Should().BeNull();
        h.Main.Status.HasEndpoint.Should().BeFalse();

        h.Azure.Release = release.Task;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        var start = h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Status.RefreshCommand.ExecuteAsync();
        h.Main.Status.Note.Should().StartWith("この対象のデプロイまたは削除を実行中のため");

        // 別の対象のジョブは関係ない
        h.Main.Target.AppName = "other";
        await h.Main.Status.RefreshCommand.ExecuteAsync();
        h.Main.Status.Note.Should().BeNull();

        release.SetResult();
        await start;
        await h.Main.Queue.WhenIdleAsync();
        h.Main.Target.AppName = "demo";
        h.Azure.Deployed();
        h.Azure.AppDetails.Add(new ContainerAppDetails("demo-tt", "Succeeded", "Running", "tt", 1.5, "3Gi", "demo-tt--r1", "demo-tt.example.io", FakeAzure.Managed));
        await h.Main.Status.RefreshCommand.ExecuteAsync();
        h.Main.Status.Note.Should().BeNull();
        h.Main.Status.HasEndpoint.Should().BeTrue();
    }

    [Fact]
    public async Task Deployment_And_Deletion_Should_Show_Their_Own_Jobs()
    {
        // P4: デプロイの画面にはデプロイのジョブを、削除の画面には削除のジョブを出す
        var h = await new Harness().ReadyAsync();
        h.Main.OpenEndpointCommand.CanExecute(null).Should().BeFalse();
        // ジョブが無ければ、デプロイ・削除の画面のジョブの欄を出さない
        h.Main.HasCurrentDeployment.Should().BeFalse();
        h.Main.HasCurrentDeletion.Should().BeFalse();
        var changes = new ChangeRecorder(h.Main);
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        var deployment = h.Main.CurrentDeployment!;
        deployment.IsDeployment.Should().BeTrue();
        deployment.HasEndpoint.Should().BeTrue();
        h.Main.CurrentDeletion.Should().BeNull();
        h.Main.HasCurrentDeployment.Should().BeTrue();
        h.Main.HasCurrentDeletion.Should().BeFalse();
        changes.Names.Should().Contain(nameof(MainViewModel.HasCurrentDeployment));
        h.Main.OpenEndpointCommand.CanExecute(null).Should().BeTrue();

        // 別の対象の削除では、デプロイの結果を残す
        h.Azure.Apps.Add(new ContainerAppInfo("other-tt", FakeAzure.EnvId, TTDeployTool.Domain.Deployment.ManagedTags.For("other")));
        h.Main.Target.AppName = "other";
        await h.Main.Deletion.PlanCommand.ExecuteAsync();
        h.Main.Deletion.ConfirmAppName = "other";
        await h.Main.Deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        h.Main.HasCurrentDeletion.Should().BeTrue();
        changes.Names.Should().Contain(nameof(MainViewModel.HasCurrentDeletion));
        h.Main.CurrentDeletion!.IsDeployment.Should().BeFalse();
        h.Main.CurrentDeletion.HasEndpoint.Should().BeFalse();
        h.Main.CurrentDeployment.Should().BeSameAs(deployment);
        h.Main.OpenEndpointCommand.CanExecute(null).Should().BeTrue();

        // 同じ対象を削除したら、デプロイの結果（URL）は使えない
        h.Azure.Apps.Clear();
        h.Azure.Deployed();
        h.Main.Target.AppName = "demo";
        await h.Main.Deletion.PlanCommand.ExecuteAsync();
        h.Main.Deletion.ConfirmAppName = "demo";
        await h.Main.Deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        h.Main.CurrentDeployment.Should().BeNull();
        h.Main.HasCurrentDeployment.Should().BeFalse();
        h.Main.OpenEndpointCommand.CanExecute(null).Should().BeFalse();
        h.Main.CurrentJob.Should().BeSameAs(h.Main.CurrentDeletion);
    }

    [Fact]
    public void Jobs_Should_Compare_Targets_Ignoring_Case()
    {
        var job = DeploymentJob("sub", "RG", "demo");

        job.IsFor("SUB", "rg", "Demo").Should().BeTrue();
        job.IsFor("sub", "rg", "other").Should().BeFalse();
        job.IsSameTarget(DeploymentJob("sub", "rg", "demo")).Should().BeTrue();
    }

    [Fact]
    public async Task Plans_Status_And_Loading_Should_Report_What_They_Are_Reading()
    {
        // P3: 処理中であることと、今している段階を出す
        var h = await new Harness().ReadyAsync();
        h.Azure.TenantList.Add(new TenantInfo("t2", null, "other.example.com"));
        var loading = Record(h.Main.Target.Busy);
        await h.Main.Target.LoadCommand.ExecuteAsync();
        loading.Should().Equal(
            "テナントの一覧を読んでいます…",
            "テナント 1 / 2（開発）のサブスクリプションを読んでいます…",
            "テナント 2 / 2（other.example.com）のサブスクリプションを読んでいます…");
        h.Main.Target.Busy.IsBusy.Should().BeFalse();
        h.Main.Target.Busy.Text.Should().BeEmpty();

        var planning = Record(h.Main.Deployment.Busy);
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        planning.Should().StartWith("計画を作っています…").And.Contain("リソースグループを確認しています…");

        h.Azure.Deployed();
        var deleting = Record(h.Main.Deletion.Busy);
        await h.Main.Deletion.PlanCommand.ExecuteAsync();
        deleting.Should().StartWith("計画を作っています…").And.Contain("Container Apps を読んでいます…");

        var reading = Record(h.Main.Status.Busy);
        await h.Main.Status.RefreshCommand.ExecuteAsync();
        reading.Should().StartWith("状態を読んでいます…").And.Contain("Container Apps の状態を読んでいます…");
    }

    [Fact]
    public async Task Busy_Should_Be_Cleared_When_The_Work_Fails()
    {
        var busy = new BusyState();
        var states = new List<bool>();
        busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BusyState.IsBusy))
                states.Add(busy.IsBusy);
        };

        var act = () => busy.RunAsync("読んでいます", () => Task.FromException(new InvalidOperationException("失敗")));

        await act.Should().ThrowAsync<InvalidOperationException>();
        states.Should().Equal(true, false);
        busy.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Reporting_Gateway_Should_Report_Only_Reads_And_Pass_Everything_Through()
    {
        var inner = new Mock<IAzureDeploymentGateway>();
        inner.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        inner.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>()))
            .Returns(Task.FromException<bool>(new RequestFailedException(409, "使用中")));
        var reported = new List<string>();
        var gateway = ReportingGateway.Wrap(inner.Object, new ActionProgressForTest(reported.Add));

        (await gateway.GetResourceGroupAsync("sub", "rg"))!.Name.Should().Be("rg");
        var delete = () => gateway.DeleteContainerAppAsync("sub", "rg", "demo-tt");

        (await delete.Should().ThrowAsync<RequestFailedException>()).Which.Status.Should().Be(409);
        reported.Should().Equal("リソースグループを確認しています");
        ReportingGateway.Steps.Should().NotContainKey(nameof(IAzureDeploymentGateway.DeleteContainerAppAsync));
    }

    [Fact]
    public void Synchronous_Exceptions_Should_Not_Be_Wrapped()
    {
        var inner = new Mock<IAzureDeploymentGateway>();
        inner.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>())).Throws(new InvalidOperationException("同期の失敗"));
        var gateway = ReportingGateway.Wrap(inner.Object, new ActionProgressForTest(_ => { }));

        Action act = () => gateway.GetEnvironmentAsync("sub", "rg", "demo-env");

        act.Should().Throw<InvalidOperationException>().WithMessage("同期の失敗");
    }

    [Fact]
    public void Setting_The_Same_Value_Should_Not_Notify_The_Dependent_Property()
    {
        var model = new DependentModel();
        var notified = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        model.Value = "a";
        model.Value = "a";

        notified.Should().Equal(nameof(DependentModel.Value), nameof(DependentModel.HasValue));
    }

    private static JobViewModel DeploymentJob(string subscription, string resourceGroup, string appName) =>
        new(new JobManager(new InMemoryJobStore(), TimeProvider.System, NullLogger<JobManager>.Instance)
            .TryStart(JobKind.Deploy, new JobTarget(subscription, resourceGroup, appName), "caller", _ => Task.FromResult<object?>(null)).Job!);

    private sealed class DependentModel : ObservableObject
    {
        private string? _value;

        public string? Value
        {
            get => _value;
            set => SetProperty(ref _value, value, new Dependent(nameof(HasValue)));
        }

        public bool HasValue => Value != null;
    }

    private sealed class ActionProgressForTest(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
