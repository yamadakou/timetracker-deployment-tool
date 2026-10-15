using System.Text.Json;
using Azure.Identity;
using Azure.ResourceManager.AppContainers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Moq;
using Timetracker.Application.Jobs;
using Timetracker.Common;
using Timetracker.Controller.Mcp.Tools;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;
using Xunit;

namespace Timetracker.Controller.Mcp.Tests;

/// <summary>テストで時刻を進められる TimeProvider。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class DeploymentJobToolsTests
{
    private const string DbPassword = "DbPassw0rd!x";
    private const string TrackerPassword = "TrackerPass!y";
    private const string EnvId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/demo-env";

    private static readonly McpServerSettings Settings = new()
    {
        DefaultSubscription = "sub", DefaultResourceGroup = "rg", DbPassword = DbPassword, TrackerPassword = TrackerPassword,
    };

    private readonly ManualTimeProvider _time = new();
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];
    private readonly InMemoryPlanStore _plans;
    private readonly JobManager _jobs;
    private readonly List<IDeploymentLogger> _gatewayLoggers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeploymentJobToolsTests()
    {
        _plans = new InMemoryPlanStore(_time);
        _jobs = new JobManager(new InMemoryJobStore(), _time, NullLogger<JobManager>.Instance);
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps.ToList());
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private DeploymentJobTools Tools(McpServerSettings? settings = null) => new(
        settings ?? Settings, new LocalCallerContext(), _plans, _jobs,
        logger => { _gatewayLoggers.Add(logger); return _gateway.Object; },
        NullLogger<DeploymentJobTools>.Instance);

    private static string Json(object value) => JsonSerializer.Serialize(value, McpServerHost.JsonOptions);

    private void ExistingResourceGroup(string location = "japaneast") =>
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", location));

    private void ExistingEnvironment(string location = "japaneast", IReadOnlyDictionary<string, string>? tags = null) =>
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo(EnvId, "demo-env", location, tags ?? ManagedTags.For("demo"), "Succeeded"));

    private void ExistingApp(string name) => _apps.Add(new ContainerAppInfo(name, EnvId, ManagedTags.For("demo")));

    private void DeploymentSucceeds(string? url = "https://demo-tt.example.io")
    {
        _gateway.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "demo", "japaneast", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<ContainerAppManagedEnvironmentResource>());
        _gateway.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(),
                It.IsAny<ContainerAppManagedEnvironmentResource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerAppsDeployment("demo-tt", "demo-db", "demo-redis", url));
    }

    private Task<DeploymentPlanResult> Plan(DeploymentJobTools? tools = null) => (tools ?? Tools()).PlanDeployment(app_name: "demo", cancellationToken: Ct);

    private async Task<Job> WaitForJob(string jobId)
    {
        var job = _jobs.Get(jobId, LocalCallerContext.Id)!;
        await job.Completion;
        return job;
    }

    // ---- plan_deployment

    [Fact]
    public async Task PlanDeployment_Should_Return_Errors_Without_Reading_Azure()
    {
        var result = await Tools(new McpServerSettings()).PlanDeployment(db_type: "mysql", cancellationToken: Ct);

        result.PlanId.Should().BeNull();
        result.ExpiresAt.Should().BeNull();
        result.Blockers.Should().Contain("subscription は必須です。").And.Contain(b => b.StartsWith("db_password が未設定です"));
        result.PermissionCheck.Should().BeEquivalentTo(new PermissionCheckInfo("not_checked", [], "設定に誤りがあるため確認していません。"));
        result.Actions.Should().BeEmpty();
        result.EstimatedDuration.Should().BeNull();
        _gatewayLoggers.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanDeployment_Should_Plan_A_New_Deployment()
    {
        var result = await Plan();

        result.PlanId.Should().MatchRegex("^pln_");
        result.ExpiresAt.Should().Be(_time.Now + TimeSpan.FromMinutes(15));
        result.Blockers.Should().BeEmpty();
        result.Identity.Should().Be(new IdentityInfo("azure-cli", "az login でサインインしているアカウント"));
        result.PermissionCheck.Status.Should().Be("not_checked");
        result.Actions.Should().Equal(
            new PlannedActionInfo("resource_group", "rg", "create", false),
            new PlannedActionInfo("environment", "demo-env", "create", false),
            new PlannedActionInfo("container_app", "demo-db", "create", false),
            new PlannedActionInfo("container_app", "demo-redis", "create", false),
            new PlannedActionInfo("container_app", "demo-tt", "create", false));
        result.RequiresDataLossAck.Should().BeFalse();
        result.EstimatedDuration.Should().StartWith("5〜15 分");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("リソースグループ 'rg' がまだ無い");
        result.Resolved.DbPassword.Should().Be("********（環境変数）");
        result.ResourceNames.Should().Be(new ResourceNames("demo-env", "demo-tt", "demo-db", "demo-redis"));
        Json(result).Should().NotContain(DbPassword).And.NotContain(TrackerPassword);
    }

    [Fact]
    public async Task PlanDeployment_Should_Require_Data_Loss_Ack_When_The_Db_Exists()
    {
        ExistingResourceGroup();
        ExistingEnvironment();
        ExistingApp("demo-db");

        var result = await Tools().PlanDeployment(app_name: "demo", db_type: "sqlserver", cancellationToken: Ct);

        result.RequiresDataLossAck.Should().BeTrue();
        result.Actions.Should().Contain(new PlannedActionInfo("container_app", "demo-db", "update", true))
            .And.Contain(new PlannedActionInfo("environment", "demo-env", "use_existing", false));
        result.PermissionCheck.Should().BeEquivalentTo(new PermissionCheckInfo("ok", [], null));
        result.EstimatedDuration.Should().Be("2〜5 分");
        // DeploymentAdvisor の警告（SQL Server のサンプル DB）と、計画の警告（DB のデータの消失）
        result.Warnings.Should().HaveCount(2);
    }

    [Fact]
    public async Task PlanDeployment_Should_Not_Issue_A_Plan_With_Blockers()
    {
        ExistingResourceGroup();
        ExistingEnvironment("eastus");
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*/read"], [])]);

        var result = await Plan();

        result.PlanId.Should().BeNull();
        result.Blockers.Should().HaveCount(2);
        result.PermissionCheck.Status.Should().Be("missing");
        result.PermissionCheck.Missing.Should().Contain(AzurePermissions.ContainerAppWrite);
    }

    [Fact]
    public async Task PlanDeployment_Should_Report_Azure_Errors()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ThrowsAsync(new CredentialUnavailableException("no"));

        var act = () => Plan();

        await act.Should().ThrowAsync<McpException>().WithMessage("Azure の認証に失敗しました（認証モード: azure-cli）。az login*");
    }

    // ---- start_deployment

    [Fact]
    public async Task StartDeployment_Should_Run_The_Plan_As_A_Job()
    {
        DeploymentSucceeds();
        var plan = await Plan();

        var started = await Tools().StartDeployment(plan.PlanId!, cancellationToken: Ct);

        started.Should().Be(new StartJobResult(started.JobId, "deploy", "running", DeploymentJobTools.PollHint));
        await WaitForJob(started.JobId);
        var status = Tools().GetJobStatus(started.JobId);
        status.Status.Should().Be("succeeded");
        status.Kind.Should().Be("deploy");
        status.Target.Should().Be(new JobTargetInfo("sub", "rg", "demo"));
        status.CurrentStep.Should().Be(new JobStepInfo(3, 3, "Container Apps（DB / Redis / Timetracker）"));
        status.Result.Should().Be(new DeploymentJobResult("rg", "demo-env", "demo-tt", "demo-db", "demo-redis", "https://demo-tt.example.io"));
        status.Error.Should().BeNull();
        status.CompletedAt.Should().Be(_time.Now);
        // 計画は一度だけ使える
        var again = () => Tools().StartDeployment(plan.PlanId!, cancellationToken: Ct);
        await again.Should().ThrowAsync<McpException>().WithMessage("*が見つかりません*");
        // ジョブの Azure の操作は、ジョブのログに書くロガーで作る
        _gatewayLoggers.Should().HaveCount(3).And.Contain(l => l.GetType().Name == "JobLogger");
        _gateway.Verify(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast",
            It.Is<DeployOptions>(o => o.DbPassword == DbPassword), It.IsAny<ContainerAppManagedEnvironmentResource>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartDeployment_Should_Require_Consent_For_Data_Loss_Without_Consuming_The_Plan()
    {
        ExistingResourceGroup();
        ExistingEnvironment();
        ExistingApp("demo-db");
        DeploymentSucceeds();
        var plan = await Plan();

        var withoutAck = () => Tools().StartDeployment(plan.PlanId!, cancellationToken: Ct);

        await withoutAck.Should().ThrowAsync<McpException>().WithMessage("*設定が変わると DB コンテナが再起動し、DB のデータは失われます*acknowledge_data_loss=true*");
        var started = await Tools().StartDeployment(plan.PlanId!, acknowledge_data_loss: true, cancellationToken: Ct);
        (await WaitForJob(started.JobId)).Status.Should().Be(JobStatus.Succeeded);
    }

    [Fact]
    public async Task StartDeployment_Should_Reject_Unknown_And_Expired_Plans()
    {
        var plan = await Plan();
        _time.Now += TimeSpan.FromMinutes(15);

        var unknown = () => Tools().StartDeployment("pln_unknown", cancellationToken: Ct);
        var expired = () => Tools().StartDeployment(plan.PlanId!, cancellationToken: Ct);

        await unknown.Should().ThrowAsync<McpException>().WithMessage("計画 'pln_unknown' が見つかりません*");
        await expired.Should().ThrowAsync<McpException>().WithMessage("*有効期限（15 分）が切れました*");
    }

    [Fact]
    public async Task StartDeployment_Should_Stop_When_Azure_Changed_After_Planning()
    {
        ExistingResourceGroup();
        ExistingEnvironment();
        var plan = await Plan();
        // 計画の後に DB アプリが作られた（更新するとデータが消える）
        ExistingApp("demo-db");

        var act = () => Tools().StartDeployment(plan.PlanId!, cancellationToken: Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage("計画の後に Azure の状態が変わりました*");
        _jobs.List(LocalCallerContext.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task StartDeployment_Should_Stop_When_A_Blocker_Appeared()
    {
        ExistingResourceGroup();
        var plan = await Plan();
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*/read"], [])]);

        var act = () => Tools().StartDeployment(plan.PlanId!, cancellationToken: Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage("実行の直前の確認で、実行できない状態になっていました:*権限が不足しています*");
    }

    [Fact]
    public async Task StartDeployment_Should_Reject_A_Second_Job_For_The_Same_Target()
    {
        var release = new TaskCompletionSource();
        var running = _jobs.TryStart(JobKind.Delete, new JobTarget("sub", "rg", "demo"), LocalCallerContext.Id, async _ => { await release.Task; return null; }).Job!;
        var plan = await Plan();

        var act = () => Tools().StartDeployment(plan.PlanId!, cancellationToken: Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage($"*ジョブ {running.Id} が実行中です*");
        release.SetResult();
        await running.Completion;
    }

    [Fact]
    public async Task A_Failed_Deployment_Should_Be_Reported_By_GetJobStatus()
    {
        _gateway.Setup(g => g.EnsureResourceGroupAsync("sub", "rg", "japaneast", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("quota exceeded"));
        var plan = await Plan();

        var started = await Tools().StartDeployment(plan.PlanId!, cancellationToken: Ct);
        await WaitForJob(started.JobId);

        var status = Tools().GetJobStatus(started.JobId);
        status.Status.Should().Be("failed");
        status.Result.Should().BeNull();
        status.Error.Should().Be("リソースグループ rg（手順 1/3）で失敗しました: quota exceeded");
    }

    // ---- get_job_status / list_jobs

    [Fact]
    public async Task GetJobStatus_Should_Return_Logs_From_The_Offset_And_Reject_Unknown_Jobs()
    {
        var job = _jobs.TryStart(JobKind.Delete, new JobTarget("sub", "rg", "demo"), LocalCallerContext.Id, ctx =>
        {
            ctx.Logger.Info("first");
            ctx.Logger.Warn("second");
            return Task.FromResult<object?>("not a deployment result");
        }).Job!;
        await job.Completion;

        var status = Tools().GetJobStatus(job.Id, log_offset: 1);
        var unknown = () => Tools().GetJobStatus("job_unknown");

        status.Kind.Should().Be("delete");
        status.CurrentStep.Should().BeNull();
        status.Result.Should().BeNull();
        status.Logs.Should().ContainSingle().Which.Should().Be(new JobLogInfo("warn", "second", _time.Now));
        status.NextLogOffset.Should().Be(2);
        unknown.Should().Throw<McpException>().WithMessage("ジョブ 'job_unknown' が見つかりません*get_deployment_info*");
    }

    [Fact]
    public async Task ListJobs_Should_Filter_By_Status()
    {
        var succeeded = _jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "a"), LocalCallerContext.Id, _ => Task.FromResult<object?>(null)).Job!;
        var failed = _jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "b"), LocalCallerContext.Id, _ => throw new InvalidOperationException("x")).Job!;
        await Task.WhenAll(succeeded.Completion, failed.Completion);
        var release = new TaskCompletionSource();
        var running = _jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "c"), LocalCallerContext.Id, async _ => { await release.Task; return null; }).Job!;

        Tools().ListJobs().Jobs.Should().HaveCount(3);
        Tools().ListJobs("RUNNING").Jobs.Should().ContainSingle()
            .Which.Should().Be(new JobSummary(running.Id, "deploy", "running", new JobTargetInfo("sub", "rg", "c"), _time.Now, null));
        Tools().ListJobs("succeeded").Jobs.Single().JobId.Should().Be(succeeded.Id);
        Tools().ListJobs("failed").Jobs.Single().Status.Should().Be("failed");
        var invalid = () => Tools().ListJobs("done");
        invalid.Should().Throw<McpException>().WithMessage("status 'done' は無効です*");

        release.SetResult();
        await running.Completion;
    }

    // ---- get_deployment_info

    [Fact]
    public async Task GetDeploymentInfo_Should_Return_The_State_Of_Each_Resource()
    {
        ExistingResourceGroup();
        ExistingEnvironment(tags: new Dictionary<string, string>());
        _gateway.Setup(g => g.ListContainerAppDetailsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ContainerAppDetails("demo-tt", "Succeeded", "Running", "densocreate/timetracker:11.0.1-linux-postgres", 1.5, "3Gi", "demo-tt--r1",
                "demo-tt.example.io", ManagedTags.For("demo")),
            new ContainerAppDetails("demo-db", "Succeeded", "Running", "postgres:16", 1, "2Gi", "demo-db--r1", null, new Dictionary<string, string>()),
        ]);

        var info = await Tools().GetDeploymentInfo(app_name: "Demo", cancellationToken: Ct);

        info.Exists.Should().BeTrue();
        (info.Subscription, info.ResourceGroup, info.AppName).Should().Be(("sub", "rg", "demo"));
        info.ResourceGroupInfo.Should().Be(new ResourceGroupState("rg", "japaneast"));
        info.Environment.Should().Be(new EnvironmentState("demo-env", "japaneast", "Succeeded", false));
        info.Tt.Should().Be(new ContainerAppState("demo-tt", "Succeeded", "Running", "densocreate/timetracker:11.0.1-linux-postgres", 1.5, "3Gi",
            "demo-tt--r1", "demo-tt.example.io", true));
        info.Db!.Managed.Should().BeFalse();
        info.Redis.Should().BeNull();
        info.Endpoint.Should().Be("https://demo-tt.example.io");
    }

    [Fact]
    public async Task GetDeploymentInfo_Should_Report_A_Missing_Resource_Group()
    {
        var info = await Tools().GetDeploymentInfo(subscription: "sub", resource_group: "rg", cancellationToken: Ct);

        info.Exists.Should().BeFalse();
        (info.ResourceGroupInfo, info.Environment, info.Tt, info.Endpoint).Should().Be(((ResourceGroupState?)null, (EnvironmentState?)null, (ContainerAppState?)null, (string?)null));
        info.AppName.Should().Be("timetracker");
    }

    [Fact]
    public async Task GetDeploymentInfo_Should_Validate_The_Target()
    {
        var noTarget = () => Tools(new McpServerSettings()).GetDeploymentInfo(cancellationToken: Ct);
        var noResourceGroup = () => Tools(new McpServerSettings()).GetDeploymentInfo(subscription: "sub", cancellationToken: Ct);
        var badName = () => Tools().GetDeploymentInfo(app_name: "-bad", cancellationToken: Ct);

        await noTarget.Should().ThrowAsync<McpException>().WithMessage("subscription と resource_group を指定してください*");
        await noResourceGroup.Should().ThrowAsync<McpException>().WithMessage("resource_group を指定してください*");
        await badName.Should().ThrowAsync<McpException>().WithMessage("アプリ名 '-bad'*");
    }
}
