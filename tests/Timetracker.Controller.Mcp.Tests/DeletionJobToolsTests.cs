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
using Timetracker.Controller.Mcp.Tests.Http;
using Xunit;

namespace Timetracker.Controller.Mcp.Tests;

public sealed class DeletionJobToolsTests
{
    private const string EnvId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/demo-env";

    private static readonly McpServerSettings Settings = new() { DefaultSubscription = "sub", DefaultResourceGroup = "rg" };

    private readonly ManualTimeProvider _time = new();
    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly List<ContainerAppInfo> _apps = [];
    private readonly InMemoryPlanStore _plans;
    private readonly JobManager _jobs;
    private readonly List<string> _deleted = [];
    private readonly List<Azure.Core.TokenCredential> _credentials = [];
    private IReadOnlyList<PermissionSet> _permissions = [new PermissionSet(["*"], [])];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeletionJobToolsTests()
    {
        _plans = new InMemoryPlanStore(_time);
        _jobs = new JobManager(new InMemoryJobStore(), _time, NullLogger<JobManager>.Instance);
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _apps.ToList());
        _gateway.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedEnvironmentInfo(EnvId, "demo-env", "japaneast", ManagedTags.For("demo")));
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => _permissions);
        _gateway.Setup(g => g.GetResourcePermissionsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string name, CancellationToken _) => Delete(name));
        _gateway.Setup(g => g.DeleteEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>())).ReturnsAsync(() => Delete("demo-env"));
        foreach (var name in new[] { "demo-tt", "demo-redis", "demo-db" })
            App(name);
    }

    private bool Delete(string name)
    {
        _deleted.Add(name);
        return _apps.RemoveAll(a => a.Name == name) > 0 || name == "demo-env";
    }

    private void App(string name, IReadOnlyDictionary<string, string>? tags = null) => _apps.Add(new ContainerAppInfo(name, EnvId, tags ?? ManagedTags.For("demo")));

    private DeletionJobTools Tools(McpServerSettings? settings = null, IAzureAccessPolicy? policy = null) => new(
        settings ?? Settings, new LocalCallerContext(), policy ?? new UnrestrictedAccessPolicy(settings ?? Settings), _plans, _jobs,
        (_, credential) =>
        {
            _credentials.Add(credential);
            return _gateway.Object;
        },
        NullLogger<DeletionJobTools>.Instance);

    private Task<DeletionPlanResult> Plan(bool includeUntagged = false) => Tools().PlanDeletion(app_name: "demo", include_untagged: includeUntagged, cancellationToken: Ct);

    private async Task<JobStatusResult> WaitForJob(string jobId)
    {
        await _jobs.Get(jobId, LocalCallerContext.Id)!.Completion;
        return new DeploymentJobTools(Settings, new LocalCallerContext(), new UnrestrictedAccessPolicy(Settings), _plans, _jobs, (_, _) => _gateway.Object, NullLogger<DeploymentJobTools>.Instance)
            .GetJobStatus(jobId);
    }

    // ---- plan_deletion

    [Fact]
    public async Task PlanDeletion_Should_Require_The_Target_Without_Reading_Azure()
    {
        var noTarget = () => Tools(new McpServerSettings()).PlanDeletion(cancellationToken: Ct);
        var badName = () => Tools().PlanDeletion(app_name: "Bad--Name", cancellationToken: Ct);

        await noTarget.Should().ThrowAsync<McpException>().WithMessage("subscription と resource_group を指定してください*");
        await badName.Should().ThrowAsync<McpException>();
        _gateway.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanDeletion_Should_Plan_To_Delete_The_Managed_Resources()
    {
        var result = await Plan();

        result.PlanId.Should().MatchRegex("^pln_");
        result.ExpiresAt.Should().Be(_time.Now + TimeSpan.FromMinutes(15));
        result.Blockers.Should().BeEmpty();
        result.Identity.Should().Be(new IdentityInfo("azure-cli", "az login でサインインしているアカウント"));
        result.PermissionCheck.Status.Should().Be("ok");
        result.Targets.Should().Equal(
            new DeletionTargetInfo("demo-tt", "container_app", true, true, true, null),
            new DeletionTargetInfo("demo-redis", "container_app", true, true, true, null),
            new DeletionTargetInfo("demo-db", "container_app", true, true, true, null),
            new DeletionTargetInfo("demo-env", "environment", true, true, true, null));
        result.Kept.Should().ContainSingle().Which.Should().StartWith("resource_group: rg（削除しません");
        result.Warnings.Should().Equal(DeletionJobTools.DbDataLossWarning);
        result.DeletesDb.Should().BeTrue();
        result.ConfirmationRequired.Should().Contain("confirm_app_name に 'demo'");
        result.Note.Should().BeNull();
    }

    [Fact]
    public async Task PlanDeletion_Should_Not_Issue_A_Plan_When_Nothing_Would_Be_Deleted()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceGroupInfo?)null);

        var result = await Plan();

        result.PlanId.Should().BeNull();
        result.Targets.Should().OnlyContain(t => !t.Exists && !t.WillDelete);
        result.Warnings.Should().BeEmpty();
        result.ConfirmationRequired.Should().BeNull();
        result.Note.Should().Be("削除するリソースはありません。");
    }

    [Fact]
    public async Task PlanDeletion_Should_Suggest_Include_Untagged_For_Resources_Without_The_Managed_Tag()
    {
        _apps.Clear();
        App("demo-tt", new Dictionary<string, string>());

        var withoutUntagged = await Plan();
        var withUntagged = await Plan(includeUntagged: true);

        withoutUntagged.Targets[0].Should().Be(new DeletionTargetInfo("demo-tt", "container_app", true, false, false, "管理タグがありません"));
        withoutUntagged.Warnings.Should().ContainSingle().Which.Should().Contain("include_untagged=true");
        withoutUntagged.DeletesDb.Should().BeFalse();
        withUntagged.Targets[0].WillDelete.Should().BeTrue();
        withUntagged.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanDeletion_Should_Block_When_Permissions_Are_Missing()
    {
        _permissions = [new PermissionSet([AzurePermissions.ContainerAppRead], [])];

        var result = await Plan();

        result.PlanId.Should().BeNull();
        result.Blockers.Should().ContainSingle().Which.Should().StartWith("権限が不足しています: ").And.Contain(AzurePermissions.ContainerAppDelete);
        result.PermissionCheck.Status.Should().Be("missing");
        result.ConfirmationRequired.Should().BeNull();
    }

    [Fact]
    public async Task PlanDeletion_Should_Warn_When_Permissions_Cannot_Be_Checked()
    {
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("denied"));

        var result = await Plan();

        result.PlanId.Should().NotBeNull();
        result.PermissionCheck.Status.Should().Be("not_checked");
        result.Warnings.Should().Contain(w => w.StartsWith("権限を事前に確認できませんでした: "));
    }

    // ---- start_deletion

    [Fact]
    public async Task StartDeletion_Should_Delete_After_The_App_Name_Is_Confirmed()
    {
        var plan = await Plan();
        var tools = Tools();

        var mismatch = () => tools.StartDeletion(plan.PlanId!, "Demo", Ct);
        await mismatch.Should().ThrowAsync<McpException>().WithMessage("confirm_app_name 'Demo' が計画のアプリ名 'demo' と一致しないため*");
        _deleted.Should().BeEmpty();

        // 一致しない場合は計画を使用済みにしないため、同じ計画で実行できる
        var started = await tools.StartDeletion(plan.PlanId!, "demo", Ct);
        var status = await WaitForJob(started.JobId);

        started.Should().BeEquivalentTo(new { Kind = "delete", Status = "running", PollHint = DeploymentJobTools.PollHint });
        _deleted.Should().Equal("demo-tt", "demo-redis", "demo-db", "demo-env");
        status.Kind.Should().Be("delete");
        status.Status.Should().Be("succeeded");
        status.CurrentStep.Should().Be(new JobStepInfo(4, 4, "demo-env の削除"));
        status.Logs.Select(l => l.Message).Should().Contain("削除中: demo-tt").And.Contain("削除が完了しました（リソースグループ rg は残しています）。");
        status.Result.Should().BeNull();
        status.DeletionResult.Should().BeEquivalentTo(new DeletionJobResult("rg", ["demo-tt", "demo-redis", "demo-db", "demo-env"], [], "rg"));

        var again = () => tools.StartDeletion(plan.PlanId!, "demo", Ct);
        await again.Should().ThrowAsync<McpException>().WithMessage("*見つかりません*plan_deletion で計画を作り直してください。");
    }

    [Fact]
    public async Task StartDeletion_Should_Report_Resources_Already_Deleted_During_The_Job()
    {
        var plan = await Plan();
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-redis", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var status = await WaitForJob((await Tools().StartDeletion(plan.PlanId!, "demo", Ct)).JobId);

        status.DeletionResult!.AlreadyDeleted.Should().Equal("demo-redis");
        status.DeletionResult.Deleted.Should().NotContain("demo-redis");
        status.Logs.Select(l => l.Message).Should().Contain("demo-redis は既に削除されていました。");
    }

    [Fact]
    public async Task StartDeletion_Should_Reject_Plans_That_Are_Not_Deletion_Plans()
    {
        var deploymentPlan = _plans.Save(new object(), LocalCallerContext.Id);

        var unknown = () => Tools().StartDeletion("pln_unknown", "demo", Ct);
        var otherKind = () => Tools().StartDeletion(deploymentPlan.PlanId, "demo", Ct);

        await unknown.Should().ThrowAsync<McpException>().WithMessage("計画 'pln_unknown' が見つかりません*plan_deletion*");
        await otherKind.Should().ThrowAsync<McpException>().WithMessage("*見つかりません*");
    }

    [Fact]
    public async Task StartDeletion_Should_Stop_When_The_State_Changed_After_The_Plan()
    {
        var plan = await Plan();
        // 計画の後に Timetracker のアプリの管理タグが外された
        _apps[0] = _apps[0] with { Tags = new Dictionary<string, string>() };

        var act = () => Tools().StartDeletion(plan.PlanId!, "demo", Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage("計画の後に Azure の状態が変わりました*");
        _deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task StartDeletion_Should_Stop_When_Permissions_Were_Removed_After_The_Plan()
    {
        var plan = await Plan();
        _permissions = [new PermissionSet([AzurePermissions.ContainerAppRead], [])];

        var act = () => Tools().StartDeletion(plan.PlanId!, "demo", Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage("実行の直前の確認で、実行できない状態になっていました:*権限が不足しています*");
        _deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task StartDeletion_Should_Reject_A_Second_Job_For_The_Same_Target()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway.Setup(g => g.DeleteContainerAppAsync("sub", "rg", "demo-tt", It.IsAny<CancellationToken>())).Returns(release.Task);
        var first = await Tools().StartDeletion((await Plan()).PlanId!, "demo", Ct);
        var second = await Plan();

        var act = () => Tools().StartDeletion(second.PlanId!, "demo", Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage($"同じ対象（rg / demo）のジョブ {first.JobId} が実行中です*plan_deletion*");
        release.SetResult(true);
        (await WaitForJob(first.JobId)).Status.Should().Be("succeeded");
    }

    // ---- HTTP（server 方式）

    [Fact]
    public async Task PlanDeletion_Should_Reject_A_Resource_Group_Out_Of_The_Server_Scope_Without_Reading_Azure()
    {
        var settings = HttpTestSupport.Settings(groups: "/subscriptions/sub/resourceGroups/other");
        var withRole = Tools(settings, HttpTestSupport.Policy(settings));
        var withoutRole = Tools(settings, HttpTestSupport.Policy(settings, withRole: false));

        await withRole.Invoking(t => t.PlanDeletion(app_name: "demo", cancellationToken: Ct)).Should().ThrowAsync<McpException>()
            .WithMessage("リソースグループ rg（サブスクリプション sub）は、このサーバーで server 方式で操作できる範囲にありません。*");
        await withoutRole.Invoking(t => t.PlanDeletion(app_name: "demo", cancellationToken: Ct)).Should().ThrowAsync<McpException>()
            .WithMessage("*Timetracker.Deploy.Server*");
        await withoutRole.Invoking(t => t.StartDeletion("p", "demo", Ct)).Should().ThrowAsync<McpException>().WithMessage("*Timetracker.Deploy.Server*");
        _gateway.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanDeletion_Should_Show_The_Server_Identity()
    {
        var settings = HttpTestSupport.Settings();

        var result = await Tools(settings, HttpTestSupport.Policy(settings)).PlanDeletion(app_name: "demo", cancellationToken: Ct);

        result.Identity.AuthMode.Should().Be("server");
        result.PlanId.Should().NotBeNull();
    }

    [Fact]
    public async Task StartDeletion_Should_Recheck_The_Server_Scope_Just_Before_Running()
    {
        var original = HttpTestSupport.Settings();
        var planId = (await Tools(original, HttpTestSupport.Policy(original)).PlanDeletion(app_name: "demo", cancellationToken: Ct)).PlanId!;
        var settings = HttpTestSupport.Settings(groups: "/subscriptions/sub/resourceGroups/other");

        var act = () => Tools(settings, HttpTestSupport.Policy(settings)).StartDeletion(planId, "demo", Ct);

        await act.Should().ThrowAsync<McpException>().WithMessage("リソースグループ rg（サブスクリプション sub）は、このサーバーで server 方式で操作できる範囲にありません。*");
        _deleted.Should().BeEmpty();
    }

    // ---- HTTP（user 方式。詳細設計 Phase 4b）

    [Fact]
    public async Task Deletion_Should_Run_With_The_Mode_Recorded_In_The_Plan()
    {
        // server 方式の範囲（other）の外でも、user 方式なら本人の権限で削除できる
        var settings = HttpTestSupport.Settings(groups: "/subscriptions/sub/resourceGroups/other", modes: "server,user");
        var policy = HttpTestSupport.PolicyWithToken(settings, roles: HttpAccessPolicy.UserRole);

        var plan = await Tools(settings, policy).PlanDeletion(app_name: "demo", cancellationToken: Ct);
        var lostRole = () => Tools(settings, HttpTestSupport.PolicyWithToken(settings)).StartDeletion(plan.PlanId!, "demo", Ct);
        await lostRole.Should().ThrowAsync<McpException>().WithMessage("このツールを使うには*");
        var started = await Tools(settings, policy).StartDeletion(plan.PlanId!, "demo", Ct);
        var status = await WaitForJob(started.JobId);

        plan.Identity.AuthMode.Should().Be("user");
        status.Status.Should().Be("succeeded");
        _deleted.Should().Equal("demo-tt", "demo-redis", "demo-db", "demo-env");
        _credentials.Should().NotBeEmpty().And.AllBeOfType<Azure.Identity.OnBehalfOfCredential>();
    }

    [Fact]
    public async Task StartDeletion_Should_Exchange_The_Token_Of_The_Start_Request()
    {
        var settings = HttpTestSupport.Settings(modes: "user");
        var tokens = new List<string>();
        Azure.Core.TokenCredential Record(string token, IDeploymentLogger _)
        {
            tokens.Add(token);
            return Mock.Of<Azure.Core.TokenCredential>();
        }
        var plan = await Tools(settings, HttpTestSupport.PolicyWithToken(settings, "plan-token", Record, HttpAccessPolicy.UserRole))
            .PlanDeletion(app_name: "demo", cancellationToken: Ct);
        tokens.Clear();

        var started = await Tools(settings, HttpTestSupport.PolicyWithToken(settings, "start-token", Record, HttpAccessPolicy.UserRole))
            .StartDeletion(plan.PlanId!, "demo", Ct);
        var status = await WaitForJob(started.JobId);

        status.Status.Should().Be("succeeded");
        tokens.Should().HaveCountGreaterThanOrEqualTo(2).And.OnlyContain(t => t == "start-token");
    }
}
