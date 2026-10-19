using Azure.ResourceManager.AppContainers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Common;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Application.Tests;

/// <summary>テストで範囲と、リソースグループを作れるかを変えられる接続。</summary>
internal sealed class TestSession(IAzureDeploymentGateway gateway) : IAzureSession
{
    public string? Scope { get; set; }

    public string? CannotCreate { get; set; }

    public List<IDeploymentLogger> Loggers { get; } = [];

    public IAzureDeploymentGateway CreateGateway(IDeploymentLogger logger)
    {
        Loggers.Add(logger);
        return gateway;
    }

    public string? ScopeBlocker(string subscription, string resourceGroup) => Scope;

    public string? ResourceGroupCreationBlocker(string resourceGroup) => CannotCreate;
}

public class DeploymentWorkflowTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];
    private readonly InMemoryPlanStore _plans;
    private readonly JobManager _jobs;
    private readonly TestSession _session;
    private readonly DeploymentWorkflow _workflow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyDictionary<string, string> NoTags = new Dictionary<string, string>();

    private static DeployOptions Opts() => new()
    {
        Subscription = "sub", ResourceGroup = "rg", AppName = "demo", DbPassword = "DbPassw0rd!", TrackerPassword = "TrackerPass!", Location = "japaneast",
    };

    public DeploymentWorkflowTests()
    {
        _plans = new InMemoryPlanStore(_time);
        _jobs = new JobManager(new InMemoryJobStore(), _time, NullLogger<JobManager>.Instance);
        _session = new TestSession(_gateway.Object);
        _workflow = new DeploymentWorkflow(_plans, _jobs, new LocalCallerContext(), new SimpleLogger(false, TextWriter.Null));
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps.ToList());
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private void ResourceGroup() =>
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));

    private void Environment(string location = "japaneast") =>
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo("id", "demo-env", location, NoTags));

    // デプロイの実行（Azure の作成・更新）。release を渡すと、それが終わるまでリソースグループの手順で止まる
    private void Deployable(Task? release = null)
    {
        var env = new Mock<ContainerAppManagedEnvironmentResource>().Object;
        _gateway.Setup(g => g.EnsureResourceGroupAsync("sub", "rg", "japaneast", It.IsAny<CancellationToken>())).Returns(() => release ?? Task.CompletedTask);
        _gateway.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "demo", "japaneast", It.IsAny<CancellationToken>())).ReturnsAsync(env);
        _gateway.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(), env, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerAppsDeployment("demo-tt", "demo-db", "demo-redis", "https://demo-tt.example.io"));
    }

    private Func<string?, IAzureSession> SessionFor(List<string?>? contexts = null) => context =>
    {
        contexts?.Add(context);
        return _session;
    };

    private static async Task<WorkflowException> Fails(Func<Task> act) => (await act.Should().ThrowAsync<WorkflowException>()).Which;

    [Fact]
    public async Task Plan_Should_Save_A_Runnable_Plan_With_Its_Context()
    {
        var ticket = await _workflow.PlanAsync(_session, Opts(), "user", Ct);

        ticket.Saved.Should().NotBeNull();
        ticket.Saved!.ExpiresAt.Should().Be(_time.Now + InMemoryPlanStore.DefaultLifetime);
        ticket.EstimatedDuration.Should().Be(DeploymentPlanTicket.LongDuration);
        _workflow.Find(ticket.Saved.PlanId).Should().Be(new StoredPlan<DeploymentPlan>(ticket.Plan, "user"));
        // 計画で Azure を読むときは、渡したロガーに書く
        _session.Loggers.Should().ContainSingle().Which.Should().BeOfType<SimpleLogger>();
    }

    [Fact]
    public async Task Plan_Should_Estimate_A_Short_Duration_When_The_Environment_Exists()
    {
        ResourceGroup();
        Environment();

        var ticket = await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct);

        ticket.EstimatedDuration.Should().Be(DeploymentPlanTicket.ShortDuration);
        _workflow.Find(ticket.Saved!.PlanId).Context.Should().BeNull();
    }

    [Fact]
    public async Task Plan_Should_Not_Save_A_Plan_With_Blockers()
    {
        ResourceGroup();
        Environment("westus");

        var ticket = await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct);

        ticket.Plan.Blockers.Should().ContainSingle().Which.Should().Contain("リージョン");
        ticket.Saved.Should().BeNull();
    }

    [Fact]
    public async Task Plan_Should_Block_Out_Of_Scope_Resource_Groups_Without_Reading_Azure()
    {
        _session.Scope = "範囲外です";

        var ticket = await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct);

        ticket.Plan.Blockers.Should().Equal("範囲外です");
        ticket.Plan.Operations.Should().BeEmpty();
        ticket.Plan.PermissionCheck.Status.Should().Be(PermissionCheckStatus.NotChecked);
        ticket.Saved.Should().BeNull();
        _session.Loggers.Should().BeEmpty();
    }

    [Fact]
    public async Task Plan_Should_Block_Creating_A_Resource_Group_Only_When_The_Session_Cannot()
    {
        _session.CannotCreate = "作成できません";

        var missing = await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct);
        ResourceGroup();
        var existing = await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct);

        missing.Plan.Blockers.Should().Equal("作成できません");
        missing.Saved.Should().BeNull();
        existing.Plan.Blockers.Should().BeEmpty();
        existing.Saved.Should().NotBeNull();
    }

    [Fact]
    public async Task Start_Should_Run_The_Plan_As_A_Job()
    {
        Deployable();
        var planId = (await _workflow.PlanAsync(_session, Opts(), "user", Ct)).Saved!.PlanId;
        var contexts = new List<string?>();

        var job = await _workflow.StartAsync(planId, acknowledgeDataLoss: false, SessionFor(contexts), Ct);
        await job.Completion;

        contexts.Should().Equal("user");
        job.Kind.Should().Be(JobKind.Deploy);
        job.Target.Should().Be(new JobTarget("sub", "rg", "demo"));
        var s = job.Snapshot();
        s.Status.Should().Be(JobStatus.Succeeded);
        s.CurrentStep.Should().Be(new JobStep(3, 3, "Container Apps（DB / Redis / Timetracker）"));
        s.Result.Should().Be(new DeploymentResult("rg", "demo-env", "demo-tt", "demo-db", "demo-redis", "https://demo-tt.example.io"));
        // 計画は一度だけ使える
        (await Fails(() => _workflow.StartAsync(planId, false, SessionFor(), Ct))).Failure.Should().Be(WorkflowFailure.PlanNotFound);
    }

    [Fact]
    public async Task Start_Should_Reject_Unknown_And_Expired_Plans()
    {
        var planId = (await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct)).Saved!.PlanId;

        (await Fails(() => _workflow.StartAsync("pln_unknown", false, SessionFor(), Ct))).Failure.Should().Be(WorkflowFailure.PlanNotFound);
        _time.Now += InMemoryPlanStore.DefaultLifetime;
        (await Fails(() => _workflow.StartAsync(planId, false, SessionFor(), Ct))).Failure.Should().Be(WorkflowFailure.PlanExpired);
    }

    [Fact]
    public async Task Start_Should_Require_Data_Loss_Ack_Without_Using_Up_The_Plan()
    {
        ResourceGroup();
        Environment();
        _apps.Add(new ContainerAppInfo("demo-db", "id", NoTags));
        Deployable();
        var planId = (await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct)).Saved!.PlanId;
        var contexts = new List<string?>();

        var ex = await Fails(() => _workflow.StartAsync(planId, false, SessionFor(contexts), Ct));

        ex.Failure.Should().Be(WorkflowFailure.DataLossNotAcknowledged);
        contexts.Should().BeEmpty();
        var job = await _workflow.StartAsync(planId, true, SessionFor(), Ct);
        await job.Completion;
        job.Status.Should().Be(JobStatus.Succeeded);
    }

    [Fact]
    public async Task Start_Should_Keep_The_Plan_When_The_Session_Cannot_Be_Created()
    {
        var planId = (await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct)).Saved!.PlanId;

        var act = () => _workflow.StartAsync(planId, false, _ => throw new InvalidOperationException("使えません"), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _workflow.Find(planId).Should().NotBeNull();
    }

    [Fact]
    public async Task Start_Should_Stop_When_The_Plan_Is_Blocked_Just_Before_Running()
    {
        ResourceGroup();
        var planId = (await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct)).Saved!.PlanId;
        _session.Scope = "範囲外になりました";

        var ex = await Fails(() => _workflow.StartAsync(planId, false, SessionFor(), Ct));

        ex.Failure.Should().Be(WorkflowFailure.Blocked);
        ex.Blockers.Should().Equal("範囲外になりました");
        // 実行しなかった場合も、計画は使用済みにする
        (await Fails(() => _workflow.StartAsync(planId, false, SessionFor(), Ct))).Failure.Should().Be(WorkflowFailure.PlanNotFound);
    }

    [Fact]
    public async Task Start_Should_Stop_When_Azure_Changed_After_The_Plan()
    {
        ResourceGroup();
        var planId = (await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct)).Saved!.PlanId;
        _apps.Add(new ContainerAppInfo("demo-redis", "id", NoTags));

        var ex = await Fails(() => _workflow.StartAsync(planId, false, SessionFor(), Ct));

        ex.Failure.Should().Be(WorkflowFailure.Changed);
        ex.Blockers.Should().BeEmpty();
        ex.Target.Should().BeNull();
        ex.ConflictingJobId.Should().BeNull();
    }

    [Fact]
    public async Task Start_Should_Reject_A_Second_Job_For_The_Same_Target()
    {
        var release = new TaskCompletionSource();
        Deployable(release.Task);
        var first = (await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct)).Saved!.PlanId;
        var second = (await _workflow.PlanAsync(_session, Opts(), cancellationToken: Ct)).Saved!.PlanId;
        var running = await _workflow.StartAsync(first, false, SessionFor(), Ct);

        var ex = await Fails(() => _workflow.StartAsync(second, false, SessionFor(), Ct));
        release.SetResult();
        await running.Completion;

        ex.Failure.Should().Be(WorkflowFailure.Conflict);
        ex.Target.Should().Be(new JobTarget("sub", "rg", "demo"));
        ex.ConflictingJobId.Should().Be(running.Id);
        ex.Message.Should().Be("Conflict");
    }

    [Fact]
    public void ActionProgress_Should_Report_Synchronously()
    {
        var reported = new List<int>();

        new ActionProgress<int>(reported.Add).Report(1);

        reported.Should().Equal(1);
    }

    [Fact]
    public void UnrestrictedAzureSession_Should_Not_Limit_Anything()
    {
        IDeploymentLogger? used = null;
        var logger = new SimpleLogger(false, TextWriter.Null);
        var session = new UnrestrictedAzureSession(l =>
        {
            used = l;
            return _gateway.Object;
        });

        session.CreateGateway(logger).Should().BeSameAs(_gateway.Object);
        used.Should().BeSameAs(logger);
        session.ScopeBlocker("sub", "rg").Should().BeNull();
        session.ResourceGroupCreationBlocker("rg").Should().BeNull();
    }
}

public class DeletionWorkflowTests
{
    private const string EnvId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/demo-env";

    private readonly ManualTimeProvider _time = new();
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];
    private readonly List<PermissionSet> _permissions = [new(["*"], [])];
    private readonly TestSession _session;
    private readonly DeletionWorkflow _workflow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyDictionary<string, string> Managed => ManagedTags.For("demo");

    public DeletionWorkflowTests()
    {
        var jobs = new JobManager(new InMemoryJobStore(), _time, NullLogger<JobManager>.Instance);
        _session = new TestSession(_gateway.Object);
        _workflow = new DeletionWorkflow(new InMemoryPlanStore(_time), jobs, new LocalCallerContext(), new SimpleLogger(false, TextWriter.Null));
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps.ToList());
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _permissions);
        _gateway.Setup(g => g.GetResourcePermissionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    // Timetracker と Redis だけがある（DB と Environment は無い）
    private void TwoApps()
    {
        _apps.Add(new ContainerAppInfo("demo-tt", EnvId, Managed));
        _apps.Add(new ContainerAppInfo("demo-redis", EnvId, Managed));
    }

    private Task<DeletionPlanTicket> Plan(string? context = null, bool keepDbData = false) =>
        _workflow.PlanAsync(_session, "sub", "rg", "demo", includeUntagged: false, keepDbData, context, Ct);

    private static string Notice(DeletionPlan plan) => $"{plan.StorageAccountName} を残しました";

    private Func<string?, IAzureSession> SessionFor(List<string?>? contexts = null) => context =>
    {
        contexts?.Add(context);
        return _session;
    };

    private static async Task<WorkflowException> Fails(Func<Task> act) => (await act.Should().ThrowAsync<WorkflowException>()).Which;

    [Fact]
    public async Task Plan_Should_Save_A_Plan_That_Deletes_Something()
    {
        TwoApps();

        var ticket = await Plan("user");

        ticket.CanRun.Should().BeTrue();
        ticket.Blockers.Should().BeEmpty();
        ticket.Plan.Targets.Where(t => t.WillDelete).Select(t => t.Name).Should().Equal("demo-tt", "demo-redis");
        _workflow.Find(ticket.Saved!.PlanId).Should().Be(new StoredPlan<DeletionPlan>(ticket.Plan, "user"));
    }

    [Fact]
    public async Task Plan_Should_Not_Save_When_There_Is_Nothing_To_Delete()
    {
        var ticket = await Plan();

        ticket.CanRun.Should().BeFalse();
        ticket.Blockers.Should().BeEmpty();
    }

    [Fact]
    public async Task Plan_Should_Block_When_Permissions_Are_Missing()
    {
        TwoApps();
        _permissions[0] = new PermissionSet(["*/read"], []);

        var ticket = await Plan();

        ticket.CanRun.Should().BeFalse();
        ticket.Blockers.Should().ContainSingle().Which.Should().StartWith("権限が不足しています: Microsoft.App/containerApps/delete");
    }

    [Fact]
    public async Task Start_Should_Delete_As_A_Job_And_Report_What_Was_Already_Gone()
    {
        TwoApps();
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-redis", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var planId = (await Plan("user")).Saved!.PlanId;
        var contexts = new List<string?>();

        var job = await _workflow.StartAsync(planId, "demo", SessionFor(contexts), Notice, Ct);
        await job.Completion;

        contexts.Should().Equal("user");
        job.Kind.Should().Be(JobKind.Delete);
        var s = job.Snapshot();
        s.Status.Should().Be(JobStatus.Succeeded);
        s.CurrentStep.Should().Be(new JobStep(2, 2, "demo-redis の削除"));
        s.Logs.Select(l => l.Message).Should().Equal(
            "削除中: demo-tt", "削除中: demo-redis", "demo-redis は既に削除されていました。", "削除が完了しました（リソースグループ rg は残しています）。");
        var outcome = s.Result.Should().BeOfType<DeletionOutcome>().Which;
        outcome.ResourceGroup.Should().Be("rg");
        outcome.Deleted.Should().Equal("demo-tt");
        outcome.AlreadyDeleted.Should().Equal("demo-redis");
    }

    [Fact]
    public async Task Start_Should_Add_The_Notice_When_The_Db_Data_Is_Kept()
    {
        _apps.Add(new ContainerAppInfo("demo-db", EnvId, Managed));
        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string name, CancellationToken _) => new StorageAccountInfo(name, "japaneast", Managed));
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-db", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        (await Plan()).Plan.LosesDbData.Should().BeTrue();
        var ticket = await Plan(keepDbData: true);
        ticket.Plan.KeepsDbData.Should().BeTrue();
        ticket.Plan.LosesDbData.Should().BeFalse();

        var job = await _workflow.StartAsync(ticket.Saved!.PlanId, "demo", SessionFor(), Notice, Ct);
        await job.Completion;

        job.Snapshot().Logs.Select(l => l.Message).Should().EndWith($"{ticket.Plan.StorageAccountName} を残しました");
        job.Snapshot().Result.Should().BeOfType<DeletionOutcome>().Which.Deleted.Should().Equal("demo-db");
    }

    [Fact]
    public async Task Start_Should_Require_The_Same_App_Name_Without_Using_Up_The_Plan()
    {
        TwoApps();
        var planId = (await Plan()).Saved!.PlanId;
        var contexts = new List<string?>();

        var ex = await Fails(() => _workflow.StartAsync(planId, "Demo", SessionFor(contexts), Notice, Ct));

        ex.Failure.Should().Be(WorkflowFailure.AppNameMismatch);
        contexts.Should().BeEmpty();
        _workflow.Find(planId).Plan.AppName.Should().Be("demo");
    }

    [Fact]
    public async Task Start_Should_Reject_Unknown_Plans()
    {
        (await Fails(() => _workflow.StartAsync("pln_unknown", "demo", SessionFor(), Notice, Ct))).Failure.Should().Be(WorkflowFailure.PlanNotFound);
    }

    [Fact]
    public async Task Start_Should_Stop_When_The_Resource_Group_Is_Out_Of_Scope()
    {
        TwoApps();
        var planId = (await Plan()).Saved!.PlanId;
        _session.Scope = "範囲外です";

        var ex = await Fails(() => _workflow.StartAsync(planId, "demo", SessionFor(), Notice, Ct));

        ex.Failure.Should().Be(WorkflowFailure.OutOfScope);
        ex.Blockers.Should().Equal("範囲外です");
    }

    [Fact]
    public async Task Start_Should_Stop_When_Permissions_Are_Missing_Just_Before_Running()
    {
        TwoApps();
        var planId = (await Plan()).Saved!.PlanId;
        _permissions[0] = new PermissionSet(["*/read"], []);

        var ex = await Fails(() => _workflow.StartAsync(planId, "demo", SessionFor(), Notice, Ct));

        ex.Failure.Should().Be(WorkflowFailure.Blocked);
        ex.Blockers.Should().ContainSingle().Which.Should().StartWith("権限が不足しています");
    }

    [Fact]
    public async Task Start_Should_Stop_When_The_Targets_Changed_After_The_Plan()
    {
        TwoApps();
        var planId = (await Plan()).Saved!.PlanId;
        _apps.RemoveAt(1);
        _apps.Add(new ContainerAppInfo("demo-redis", EnvId, new Dictionary<string, string>()));

        (await Fails(() => _workflow.StartAsync(planId, "demo", SessionFor(), Notice, Ct))).Failure.Should().Be(WorkflowFailure.Changed);
    }

    [Fact]
    public async Task Start_Should_Reject_A_Second_Job_For_The_Same_Target()
    {
        TwoApps();
        var release = new TaskCompletionSource<bool>();
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(release.Task);
        var first = (await Plan()).Saved!.PlanId;
        var second = (await Plan()).Saved!.PlanId;
        var running = await _workflow.StartAsync(first, "demo", SessionFor(), Notice, Ct);

        var ex = await Fails(() => _workflow.StartAsync(second, "demo", SessionFor(), Notice, Ct));
        release.SetResult(true);
        await running.Completion;

        ex.Failure.Should().Be(WorkflowFailure.Conflict);
        ex.Target.Should().Be(new JobTarget("sub", "rg", "demo"));
        ex.ConflictingJobId.Should().Be(running.Id);
    }
}
