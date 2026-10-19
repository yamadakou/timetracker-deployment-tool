using System.Text.Json;
using Azure.Identity;
using Azure.ResourceManager.AppContainers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Common;
using TTDeployTool.Controller.Mcp.Tools;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using TTDeployTool.Controller.Mcp.Tests.Http;
using Xunit;

namespace TTDeployTool.Controller.Mcp.Tests;

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
    private readonly List<Azure.Core.TokenCredential> _credentials = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeploymentJobToolsTests()
    {
        _plans = new InMemoryPlanStore(_time);
        _jobs = new JobManager(new InMemoryJobStore(), _time, NullLogger<JobManager>.Instance);
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps.ToList());
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private DeploymentJobTools Tools(McpServerSettings? settings = null, IAzureAccessPolicy? policy = null) => new(
        settings ?? Settings, new LocalCallerContext(), policy ?? new UnrestrictedAccessPolicy(settings ?? Settings), _plans, _jobs,
        (logger, credential) =>
        {
            _gatewayLoggers.Add(logger);
            _credentials.Add(credential);
            return _gateway.Object;
        },
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

    // ---- DB のデータの永続化（persist_db）

    private static readonly string Account = ResourceNaming.StorageAccount("sub", "rg", "demo");

    [Fact]
    public async Task PlanDeployment_Should_Plan_The_Storage_When_Persisting()
    {
        _gateway.Setup(g => g.IsStorageAccountNameAvailableAsync("sub", Account, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Tools().PlanDeployment(app_name: "demo", persist_db: true, cancellationToken: Ct);

        result.PlanId.Should().NotBeNull();
        result.PersistDb.Should().BeTrue();
        result.StorageAccount.Should().Be(Account);
        result.Resolved.PersistDb.Should().BeTrue();
        result.Actions.Select(a => (a.Resource, a.Name, a.Action)).Should().ContainInOrder(
            ("storage_account", Account, "create"), ("file_share", "demo-db", "create"), ("environment_storage", "dbdata", "create"));
        result.Warnings.Should().Contain(DbPersistence.StorageNotice(Account));
    }

    [Fact]
    public async Task PlanDeployment_Should_Not_Persist_By_Default()
    {
        var result = await Plan();

        result.PersistDb.Should().BeFalse();
        result.StorageAccount.Should().BeNull();
        result.Resolved.PersistDb.Should().BeNull();
    }

    [Fact]
    public async Task StartDeployment_Should_Not_Require_Consent_When_The_Persisted_Db_Is_Kept()
    {
        // 永続化している DB アプリを、persist_db を省略して再デプロイする（今の状態を引き継ぐ）
        ExistingResourceGroup();
        ExistingEnvironment();
        _apps.Add(new ContainerAppInfo("demo-db", EnvId, ManagedTags.For("demo"), [DbPersistence.StorageName]));
        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageAccountInfo(Account, "japaneast", ManagedTags.For("demo")));
        _gateway.Setup(g => g.FileShareExistsAsync("sub", "rg", Account, "demo-db", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _gateway.Setup(g => g.EnvironmentStorageExistsAsync("sub", "rg", "demo-env", "dbdata", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        DeploymentSucceeds();
        var plan = await Plan();

        plan.RequiresDataLossAck.Should().BeFalse();
        plan.PersistDb.Should().BeTrue();
        plan.Actions.Single(a => a.Name == "demo-db" && a.Resource == "container_app").DataLoss.Should().BeFalse();
        var started = await Tools().StartDeployment(plan.PlanId!, cancellationToken: Ct);
        await WaitForJob(started.JobId);

        var status = Tools().GetJobStatus(started.JobId);
        status.Status.Should().Be("succeeded");
        status.CurrentStep.Should().Be(new JobStepInfo(4, 4, "Container Apps（DB / Redis / Timetracker）"));
        status.Result!.StorageAccount.Should().Be(Account);
        _gateway.Verify(g => g.EnsureDbStorageAsync("sub", "rg", "demo", "japaneast", It.IsAny<ContainerAppManagedEnvironmentResource>(), It.IsAny<CancellationToken>()), Times.Once);
        _gateway.Verify(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast",
            It.Is<DeployOptions>(o => o.PersistDb == true), It.IsAny<ContainerAppManagedEnvironmentResource>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PlanDeployment_Should_Require_Consent_To_Stop_Persisting()
    {
        ExistingResourceGroup();
        ExistingEnvironment();
        _apps.Add(new ContainerAppInfo("demo-db", EnvId, ManagedTags.For("demo"), [DbPersistence.StorageName]));

        var plan = await Tools().PlanDeployment(app_name: "demo", persist_db: false, cancellationToken: Ct);

        plan.RequiresDataLossAck.Should().BeTrue();
        plan.PersistDb.Should().BeFalse();
        plan.Warnings.Should().Contain(DbPersistence.DataLossWarning("demo-db", true, false));
    }

    [Fact]
    public async Task GetDeploymentInfo_Should_Return_The_Persistence()
    {
        ExistingResourceGroup();
        _gateway.Setup(g => g.ListContainerAppDetailsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ContainerAppDetails("demo-db", "Succeeded", "Running", "postgres:16", 1, "2Gi", "demo-db--r1", null, ManagedTags.For("demo"),
                [DbPersistence.StorageName]),
        ]);
        _gateway.Setup(g => g.GetStorageAccountAsync("sub", "rg", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageAccountInfo(Account, "japaneast", ManagedTags.For("demo")));

        var info = await Tools().GetDeploymentInfo(app_name: "demo", cancellationToken: Ct);

        info.Persistence.Should().Be(new PersistenceState(true, Account, true));
    }

    [Fact]
    public async Task GetDeploymentInfo_Should_Report_No_Persistence_Without_A_Storage_Account()
    {
        ExistingResourceGroup();
        _gateway.Setup(g => g.ListContainerAppDetailsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var info = await Tools().GetDeploymentInfo(app_name: "demo", cancellationToken: Ct);

        info.Persistence.Should().Be(new PersistenceState(false, null, null));
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

    // ---- HTTP（server 方式）

    [Fact]
    public async Task PlanDeployment_Should_Block_A_Resource_Group_Out_Of_The_Server_Scope_Without_Reading_Azure()
    {
        var settings = HttpTestSupport.Settings(groups: "/subscriptions/sub/resourceGroups/other");

        var result = await Tools(settings, HttpTestSupport.Policy(settings)).PlanDeployment(
            app_name: "demo", db_password: DbPassword, tracker_password: TrackerPassword, cancellationToken: Ct);

        result.PlanId.Should().BeNull();
        result.Blockers.Should().ContainSingle().Which.Should().StartWith("リソースグループ rg（サブスクリプション sub）は、このサーバーで server 方式で操作できる範囲にありません。");
        result.Identity.Should().Be(new IdentityInfo("server", "MCP サーバーのマネージド ID（server 方式）"));
        result.PermissionCheck.Status.Should().Be("not_checked");
        _gateway.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanDeployment_Should_Block_A_Missing_Resource_Group_Unless_The_Server_Can_Create_It(bool canCreate)
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceGroupInfo?)null);
        var settings = HttpTestSupport.Settings(canCreate);

        var result = await Tools(settings, HttpTestSupport.Policy(settings)).PlanDeployment(
            app_name: "demo", db_password: DbPassword, tracker_password: TrackerPassword, cancellationToken: Ct);

        if (canCreate)
        {
            result.PlanId.Should().NotBeNull();
            result.Blockers.Should().BeEmpty();
        }
        else
        {
            result.PlanId.Should().BeNull();
            result.Blockers.Should().Equal(string.Format(DeploymentJobTools.CannotCreateResourceGroup, "rg"));
        }
    }

    [Fact]
    public async Task PlanDeployment_Should_Ask_For_Password_Arguments_Over_Http()
    {
        var settings = HttpTestSupport.Settings();

        var result = await Tools(settings, HttpTestSupport.Policy(settings)).PlanDeployment(app_name: "demo", cancellationToken: Ct);

        result.Blockers.Should().Contain(b => b.StartsWith("db_password が未設定です。引数で指定してください（HTTP"))
            .And.Contain(b => b.StartsWith("tracker_password が未設定です。引数で指定してください（HTTP"));
    }

    [Fact]
    public async Task Azure_Tools_Should_Require_The_Server_Role()
    {
        var settings = HttpTestSupport.Settings();
        var tools = Tools(settings, HttpTestSupport.Policy(settings, withRole: false));

        await tools.Invoking(t => t.PlanDeployment(cancellationToken: Ct)).Should().ThrowAsync<McpException>().WithMessage("*TTDeployTool.Deploy.Server*");
        await tools.Invoking(t => t.StartDeployment("p", cancellationToken: Ct)).Should().ThrowAsync<McpException>().WithMessage("*TTDeployTool.Deploy.Server*");
        await tools.Invoking(t => t.GetDeploymentInfo(cancellationToken: Ct)).Should().ThrowAsync<McpException>().WithMessage("*TTDeployTool.Deploy.Server*");
        _gateway.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task StartDeployment_Should_Recheck_The_Server_Scope_Just_Before_Running()
    {
        ExistingResourceGroup();
        var original = HttpTestSupport.Settings();
        var planId = (await Tools(original, HttpTestSupport.Policy(original))
            .PlanDeployment(app_name: "demo", db_password: "Passw0rd!", tracker_password: "Passw0rd!", cancellationToken: Ct)).PlanId!;
        // 計画の後にサーバーの設定が変わり、範囲外になった
        var settings = HttpTestSupport.Settings(groups: "/subscriptions/sub/resourceGroups/other");

        var act = () => Tools(settings, HttpTestSupport.Policy(settings)).StartDeployment(planId, cancellationToken: Ct);

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().StartWith("実行の直前の確認で、実行できない状態になっていました").And.Contain("操作できる範囲にありません");
        _jobs.List(LocalCallerContext.Id, null).Should().BeEmpty();
    }

    [Fact]
    public async Task GetDeploymentInfo_Should_Reject_A_Resource_Group_Out_Of_The_Server_Scope()
    {
        var settings = HttpTestSupport.Settings();

        var act = () => Tools(settings, HttpTestSupport.Policy(settings)).GetDeploymentInfo(resource_group: "other", cancellationToken: Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage("リソースグループ other（サブスクリプション sub）は、このサーバーで server 方式で操作できる範囲にありません。*");
        _gateway.Invocations.Should().BeEmpty();
    }

    // ---- HTTP（user 方式。詳細設計 Phase 4b）

    private static readonly McpServerSettings BothModes = HttpTestSupport.Settings(groups: "/subscriptions/sub/resourceGroups/other", modes: "server,user");

    private Task<DeploymentPlanResult> PlanAsUser(HttpAccessPolicy policy) => Tools(BothModes, policy).PlanDeployment(
        app_name: "demo", db_password: DbPassword, tracker_password: TrackerPassword, azure_identity: "user", cancellationToken: Ct);

    [Fact]
    public async Task PlanDeployment_Should_Use_The_Callers_Own_Permissions_In_The_User_Mode()
    {
        ExistingResourceGroup();

        // server 方式の範囲（other）の外でも、user 方式なら本人の権限で計画できる
        var result = await PlanAsUser(HttpTestSupport.PolicyWithToken(BothModes, roles: HttpAccessPolicy.UserRole));

        result.PlanId.Should().NotBeNull();
        result.Blockers.Should().BeEmpty();
        result.Identity.Should().Be(new IdentityInfo("user", "サインインした利用者本人の権限（user 方式）"));
        _credentials.Should().NotBeEmpty().And.AllBeOfType<OnBehalfOfCredential>();
    }

    [Fact]
    public async Task StartDeployment_Should_Run_With_The_Mode_Recorded_In_The_Plan()
    {
        ExistingResourceGroup();
        var policy = HttpTestSupport.PolicyWithToken(BothModes, roles: HttpAccessPolicy.ServerRole);
        var planId = (await PlanAsUser(policy)).PlanId!;
        _credentials.Clear();

        var started = await Tools(BothModes, policy).StartDeployment(planId, cancellationToken: Ct);
        await WaitForJob(started.JobId);

        // 実行の直前の確認とジョブのどちらも、計画の方式（user）の資格情報で操作する
        _credentials.Should().HaveCountGreaterThanOrEqualTo(2).And.AllBeOfType<OnBehalfOfCredential>();
    }

    [Fact]
    public async Task StartDeployment_Should_Recheck_The_Role_For_The_Planned_Mode()
    {
        ExistingResourceGroup();
        var settings = HttpTestSupport.Settings(modes: "server,user");
        var planId = (await Tools(settings, HttpTestSupport.PolicyWithToken(settings, roles: HttpAccessPolicy.ServerRole)).PlanDeployment(
            app_name: "demo", db_password: DbPassword, tracker_password: TrackerPassword, cancellationToken: Ct)).PlanId!;

        // 計画の後に TTDeployTool.Deploy.Server のロールが外れた（user 方式は使える）
        var act = () => Tools(settings, HttpTestSupport.PolicyWithToken(settings, roles: HttpAccessPolicy.UserRole)).StartDeployment(planId, cancellationToken: Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage("server 方式を使うには、MCP サーバーのアプリロール TTDeployTool.Deploy.Server が必要です（使える方式: user）。");
        // 計画は使用済みにしない
        _plans.Find<TTDeployTool.Application.Workflows.StoredPlan<TTDeployTool.Application.Deployment.DeploymentPlan>>(planId, LocalCallerContext.Id).Status.Should().Be(PlanLookupStatus.Found);
        _jobs.List(LocalCallerContext.Id, null).Should().BeEmpty();
    }

    [Fact]
    public async Task User_Mode_Should_Explain_How_To_Recover_From_A_Token_Exchange_Failure()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationFailedException("AADSTS65001: consent required"));

        var act = () => PlanAsUser(HttpTestSupport.PolicyWithToken(BothModes, roles: HttpAccessPolicy.UserRole));

        await act.Should().ThrowAsync<McpException>().WithMessage($"Azure の認証に失敗しました（user 方式（利用者本人の権限））。{HttpAccessPolicy.UserAuthHint}");
    }

    [Theory]
    [InlineData("AADSTS50076: Due to a configuration change made by your administrator, ... you must use multi-factor authentication", ToolSupport.MfaHint)]
    [InlineData("AADSTS50079: Due to a configuration change made by your administrator, ... you must enroll in multi-factor authentication", ToolSupport.MfaRegistrationHint)]
    public async Task User_Mode_Should_Explain_That_Mfa_Is_Required_For_Azure(string error, string hint)
    {
        // Azure の管理 API は MFA が必須。MCP サーバーへのサインインで MFA を行っていないと、OBO で交換できない（Phase 4c の確認結果）
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ThrowsAsync(new AuthenticationFailedException(error));

        var act = () => PlanAsUser(HttpTestSupport.PolicyWithToken(BothModes, roles: HttpAccessPolicy.UserRole));

        await act.Should().ThrowAsync<McpException>().WithMessage($"Azure の認証に失敗しました（user 方式（利用者本人の権限））。{hint}");
    }

    [Fact]
    public async Task GetDeploymentInfo_Should_Accept_The_Azure_Identity()
    {
        _gateway.Setup(g => g.ListContainerAppDetailsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var info = await Tools(BothModes, HttpTestSupport.PolicyWithToken(BothModes, roles: HttpAccessPolicy.UserRole))
            .GetDeploymentInfo(app_name: "demo", azure_identity: "user", cancellationToken: Ct);
        var stdio = () => Tools().GetDeploymentInfo(app_name: "demo", azure_identity: "user", cancellationToken: Ct);

        info.AppName.Should().Be("demo");
        _credentials.Should().ContainSingle().Which.Should().BeOfType<OnBehalfOfCredential>();
        await stdio.Should().ThrowAsync<McpException>().WithMessage("azure_identity は HTTP のサーバーでだけ指定できます*");
    }

    [Fact]
    public async Task StartDeployment_Should_Exchange_The_Token_Of_The_Start_Request()
    {
        ExistingResourceGroup();
        var tokens = new List<string>();
        Azure.Core.TokenCredential Record(string token, IDeploymentLogger _)
        {
            tokens.Add(token);
            return Mock.Of<Azure.Core.TokenCredential>();
        }
        var planPolicy = HttpTestSupport.PolicyWithToken(BothModes, "plan-token", Record, HttpAccessPolicy.UserRole);
        var startPolicy = HttpTestSupport.PolicyWithToken(BothModes, "start-token", Record, HttpAccessPolicy.UserRole);
        var planId = (await PlanAsUser(planPolicy)).PlanId!;
        tokens.Should().NotBeEmpty().And.OnlyContain(t => t == "plan-token");
        tokens.Clear();

        var started = await Tools(BothModes, startPolicy).StartDeployment(planId, cancellationToken: Ct);
        await WaitForJob(started.JobId);

        // 実行の直前の確認とジョブは、計画のときではなく実行の要求のトークンで交換する（計画の後にトークンが更新されても使える）
        tokens.Should().HaveCountGreaterThanOrEqualTo(2).And.OnlyContain(t => t == "start-token");
    }
}
