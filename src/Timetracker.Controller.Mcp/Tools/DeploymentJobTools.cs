using System.ComponentModel;
using Azure.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Timetracker.Application.Deployment;
using Timetracker.Application.Jobs;
using Timetracker.Application.Permissions;
using Timetracker.Common;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Mcp.Tools;

/// <summary>
/// Azure へのデプロイ（計画 → 確認 → 実行）、ジョブの状態、デプロイ済みの環境の状態のツール（Phase 3b）。
/// DB を再デプロイするとデータが消えるため、実行は plan_deployment で作った計画（plan_id）に対してだけ行い、
/// デプロイのパラメータは start_deployment では受け取らない（計画と違う内容で実行されないように）。
/// </summary>
[McpServerToolType]
public sealed class DeploymentJobTools
{
    public const string PollHint = "get_job_status を 15〜30 秒の間隔で呼び、status が succeeded または failed になるまで待ってください。";

    public const string CannotCreateResourceGroup =
        "リソースグループ {0} がありません。このサーバーではリソースグループを作成できない設定（TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP）のため、" +
        "管理者にリソースグループの作成を依頼してください。";

    private readonly McpServerSettings _settings;
    private readonly ICallerContext _caller;
    private readonly IAzureAccessPolicy _policy;
    private readonly IPlanStore _plans;
    private readonly JobManager _jobs;
    private readonly Func<IDeploymentLogger, TokenCredential, IAzureDeploymentGateway> _gatewayFactory;
    private readonly ILogger<DeploymentJobTools> _logger;

    /// <param name="gatewayFactory">渡したロガーにログを書き、渡した資格情報で操作する Azure の操作を作る（ジョブではジョブのログに書く）</param>
    public DeploymentJobTools(
        McpServerSettings settings, ICallerContext caller, IAzureAccessPolicy policy, IPlanStore plans, JobManager jobs,
        Func<IDeploymentLogger, TokenCredential, IAzureDeploymentGateway> gatewayFactory, ILogger<DeploymentJobTools> logger)
    {
        _settings = settings;
        _caller = caller;
        _policy = policy;
        _plans = plans;
        _jobs = jobs;
        _gatewayFactory = gatewayFactory;
        _logger = logger;
    }

    [McpServerTool(Name = "plan_deployment", Title = "Azure へのデプロイの計画",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Azure Container Apps へのデプロイの計画を作る（Azure を読むだけで、変更しない）。作成・更新するリソース、" +
                 "DB のデータが消えるおそれがあるか（requires_data_loss_ack）、警告、権限の確認結果を返す。結果を利用者に示して確認してから、" +
                 "plan_id を start_deployment に渡す。blockers があると plan_id は null になり、実行できない。計画の有効期限は 15 分。")]
    public async Task<DeploymentPlanResult> PlanDeployment(
        [Description("サブスクリプション ID（省略時はサーバーの既定値 TT_MCP_DEFAULT_SUBSCRIPTION）")] string? subscription = null,
        [Description("リソースグループ名（省略時はサーバーの既定値 TT_MCP_DEFAULT_RESOURCE_GROUP）")] string? resource_group = null,
        [Description("リージョン（既定 japaneast）")] string? location = null,
        [Description("アプリ名（既定 timetracker）。リソース名の接頭辞になる")] string? app_name = null,
        [Description("DB 種別: postgresql（既定）または sqlserver")] string? db_type = null,
        [Description("DB 名（既定 timetracker）")] string? db_name = null,
        [Description("timetracker イメージのタグ（既定は DB 種別に応じたタグ）")] string? tt_tag = null,
        [Description("DB のパスワード。会話に残るため、サーバーの環境変数 TT_MCP_DB_PASSWORD での設定を推奨")] string? db_password = null,
        [Description("Timetracker のパスワード。会話に残るため、サーバーの環境変数 TT_MCP_TRACKER_PASSWORD での設定を推奨")] string? tracker_password = null,
        [Description("Timetracker の CPU（vCPU、既定 1.5）")] double? tt_cpu = null,
        [Description("Timetracker のメモリ（Gi、既定 3.0）")] double? tt_memory_gi = null,
        [Description("DB の CPU（vCPU、既定 1.0）")] double? db_cpu = null,
        [Description("DB のメモリ（Gi、既定 2.0）")] double? db_memory_gi = null,
        [Description("Redis の CPU（vCPU、既定 0.5）")] double? redis_cpu = null,
        [Description("Redis のメモリ（Gi、既定 1.0）")] double? redis_memory_gi = null,
        [Description(DeploymentTools.AzureIdentityDescription)] string? azure_identity = null,
        CancellationToken cancellationToken = default)
    {
        var access = _policy.Resolve(azure_identity);
        var resolved = new DeploymentParameters
        {
            Subscription = subscription, ResourceGroup = resource_group, Location = location, AppName = app_name, DbType = db_type,
            DbName = db_name, TtTag = tt_tag, DbPassword = db_password, TrackerPassword = tracker_password,
            TtCpu = tt_cpu, TtMemoryGi = tt_memory_gi, DbCpu = db_cpu, DbMemoryGi = db_memory_gi, RedisCpu = redis_cpu, RedisMemoryGi = redis_memory_gi,
        }.Resolve(_settings);
        var o = resolved.Options;
        var advice = DeploymentAdvisor.GetWarnings(o, ValidationTarget.Azure);
        var identity = access.Identity;
        var errors = DeploymentValidator.GetErrors(o, ValidationTarget.Azure).Concat(ToolSupport.PasswordHints(resolved, _settings)).ToList();
        if (errors.Count > 0)
        {
            // 設定に誤りがあれば Azure は読まない
            return new DeploymentPlanResult(null, null, errors, identity,
                new PermissionCheckInfo("not_checked", [], "設定に誤りがあるため確認していません。"),
                [], advice, false, null, ToolSupport.ToResolvedSettings(resolved), ToolSupport.ToResourceNames(o.AppName));
        }

        var plan = await PlanAsync(access, o, cancellationToken);
        var saved = plan.CanRun ? _plans.Save(new IdentityPlan<DeploymentPlan>(plan, access.Mode), _caller.CallerId) : null;
        return new DeploymentPlanResult(
            PlanId: saved?.PlanId,
            ExpiresAt: saved?.ExpiresAt,
            Blockers: plan.Blockers,
            Identity: identity,
            PermissionCheck: ToPermissionCheckInfo(plan.PermissionCheck),
            Actions: plan.Operations.Select(ToActionInfo).ToList(),
            Warnings: advice.Concat(plan.Warnings).ToList(),
            RequiresDataLossAck: plan.RequiresDataLossAck,
            EstimatedDuration: plan.CreatesEnvironment ? "5〜15 分（Environment を新規作成するため）" : "2〜5 分",
            Resolved: ToolSupport.ToResolvedSettings(resolved),
            ResourceNames: ToolSupport.ToResourceNames(o.AppName));
    }

    [McpServerTool(Name = "start_deployment", Title = "Azure へのデプロイの開始",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("plan_deployment の計画に基づいてデプロイを始める（バックグラウンドのジョブ）。すぐに job_id を返すので、" +
                 "get_job_status で進捗と結果を確認する。計画が requires_data_loss_ack=true の場合は、DB のデータが消えるおそれがあることに" +
                 "利用者が明示的に同意したときだけ acknowledge_data_loss=true を指定する。計画は一度だけ使える。")]
    public async Task<StartJobResult> StartDeployment(
        [Description("plan_deployment が返した plan_id")] string plan_id,
        [Description("DB のデータが消えるおそれがあることに利用者が同意したか（requires_data_loss_ack=true の計画では必須）")] bool acknowledge_data_loss = false,
        CancellationToken cancellationToken = default)
    {
        _policy.RequireAzureAccess();
        var (plan, mode) = RequirePlan(_plans.Find<IdentityPlan<DeploymentPlan>>(plan_id, _caller.CallerId), plan_id, "plan_deployment");
        // 同意が無い場合は計画を使用済みにしない（同意を得て、同じ計画で実行し直せるように）
        if (plan.RequiresDataLossAck && !acknowledge_data_loss)
            throw new McpException(
                "この計画は既存の DB アプリを更新します。設定が変わると DB コンテナが再起動し、DB のデータは失われます。利用者が同意した場合だけ、" +
                "acknowledge_data_loss=true を指定して実行してください。");
        // 計画を作った方式で実行する（呼び出し元がまだその方式を使えるかも確かめる）
        var access = _policy.ForMode(mode);
        plan = RequirePlan(_plans.Take<IdentityPlan<DeploymentPlan>>(plan_id, _caller.CallerId), plan_id, "plan_deployment").Plan;

        // 計画の後に Azure の状態が変わっていないかを、実行の直前にもう一度確かめる
        var current = await PlanAsync(access, plan.Options, cancellationToken);
        if (!current.CanRun)
            throw new McpException("実行の直前の確認で、実行できない状態になっていました:\n- " + string.Join("\n- ", current.Blockers));
        if (!plan.HasSameOperations(current))
            throw new McpException("計画の後に Azure の状態が変わりました（リソースが作成・削除されたなど）。plan_deployment で計画を作り直してください。");

        var o = plan.Options;
        var started = _jobs.TryStart(JobKind.Deploy, new JobTarget(o.Subscription, o.ResourceGroup, o.AppName), _caller.CallerId, async context =>
        {
            var gateway = _gatewayFactory(context.Logger, access.CreateCredential(context.Logger));
            var progress = new ActionProgress<DeploymentProgress>(p => context.ReportStep(p.Index, p.Total, p.Description));
            return await new DeploymentRunner(gateway).RunAsync(o, progress);
        });
        if (started.Job == null)
            throw new McpException(
                $"同じ対象（{o.ResourceGroup} / {o.AppName}）のジョブ {started.ConflictingJobId} が実行中です。" +
                "get_job_status で完了を確認してから、plan_deployment で計画を作り直してください。");
        _logger.LogInformation("デプロイのジョブ {JobId} を開始しました（{ResourceGroup} / {AppName}）", started.Job.Id, o.ResourceGroup, o.AppName);
        return new StartJobResult(started.Job.Id, "deploy", "running", PollHint);
    }

    [McpServerTool(Name = "get_job_status", Title = "ジョブの状態",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("デプロイ・削除のジョブの状態（running / succeeded / failed）、実行中の手順、ログ、結果（デプロイの成功時は result に " +
                 "Timetracker の URL、削除の成功時は deletion_result に削除したリソース）、エラーを返す。" +
                 "log_offset に前回の next_log_offset を渡すと、続きのログだけを返す。")]
    public JobStatusResult GetJobStatus(
        [Description("start_deployment または start_deletion が返した job_id")] string job_id,
        [Description("この位置から後のログを返す（既定 0。前回の next_log_offset を渡す）")] int log_offset = 0)
    {
        var job = _jobs.Get(job_id, _caller.CallerId)
                  ?? throw new McpException($"ジョブ '{job_id}' が見つかりません（サーバーを再起動すると、ジョブの情報は消えます。Azure の状態は get_deployment_info で確認できます）。");
        var s = job.Snapshot(log_offset);
        return new JobStatusResult(
            s.JobId, KindName(s.Kind), StatusName(s.Status), ToTargetInfo(s.Target), s.CreatedAt, s.CompletedAt,
            s.CurrentStep == null ? null : new JobStepInfo(s.CurrentStep.Index, s.CurrentStep.Total, s.CurrentStep.Name),
            s.Logs.Select(l => new JobLogInfo(l.Level, l.Message, l.At)).ToList(),
            s.NextLogOffset,
            s.Result is DeploymentResult r
                ? new DeploymentJobResult(r.ResourceGroup, r.EnvironmentName, r.TimetrackerAppName, r.DbAppName, r.RedisAppName, r.TimetrackerUrl)
                : null,
            s.Result as DeletionJobResult,
            s.Error,
            s.DeviceCodeMessage);
    }

    [McpServerTool(Name = "list_jobs", Title = "ジョブの一覧",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("このサーバーで実行したジョブの一覧（新しい順。ログは含めない）。status で絞り込める。")]
    public JobListResult ListJobs([Description("running / succeeded / failed（省略時はすべて）")] string? status = null)
    {
        JobStatus? filter = status?.ToLowerInvariant() switch
        {
            null => null,
            "running" => JobStatus.Running,
            "succeeded" => JobStatus.Succeeded,
            "failed" => JobStatus.Failed,
            _ => throw new McpException($"status '{status}' は無効です。running / succeeded / failed のいずれかを指定してください。"),
        };
        return new JobListResult(_jobs.List(_caller.CallerId, filter)
            .Select(j => j.Snapshot(int.MaxValue))
            .Select(s => new JobSummary(s.JobId, KindName(s.Kind), StatusName(s.Status), ToTargetInfo(s.Target), s.CreatedAt, s.CompletedAt))
            .ToList());
    }

    [McpServerTool(Name = "get_deployment_info", Title = "デプロイ済みの環境の状態",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Azure 上のデプロイ済みの環境（Environment と DB / Redis / Timetracker の Container App）の状態、イメージ、" +
                 "CPU とメモリ、最新のリビジョン、管理タグの有無、Timetracker の URL を返す（Azure は変更しない）。")]
    public async Task<DeploymentInfoResult> GetDeploymentInfo(
        [Description("サブスクリプション ID（省略時はサーバーの既定値 TT_MCP_DEFAULT_SUBSCRIPTION）")] string? subscription = null,
        [Description("リソースグループ名（省略時はサーバーの既定値 TT_MCP_DEFAULT_RESOURCE_GROUP）")] string? resource_group = null,
        [Description("アプリ名（既定 timetracker）")] string? app_name = null,
        [Description(DeploymentTools.AzureIdentityDescription)] string? azure_identity = null,
        CancellationToken cancellationToken = default)
    {
        var access = _policy.Resolve(azure_identity);
        var o = ToolSupport.ResolveTarget(_settings, access, subscription, resource_group, app_name);

        var info = await ToolSupport.CallAzureAsync(access, _logger, () =>
            new DeploymentInspector(CreateGateway(access)).InspectAsync(o.Subscription, o.ResourceGroup, o.AppName, cancellationToken));
        ContainerAppState? App(ContainerAppDetails? a) => a == null ? null : new ContainerAppState(
            a.Name, a.ProvisioningState, a.RunningStatus, a.Image, a.Cpu, a.Memory, a.LatestRevisionName, a.Fqdn, ManagedTags.IsManaged(a.Tags, o.AppName));
        return new DeploymentInfoResult(
            info.Exists, info.Subscription, info.ResourceGroup, info.AppName,
            info.ResourceGroupInfo == null ? null : new ResourceGroupState(info.ResourceGroupInfo.Name, info.ResourceGroupInfo.Location),
            info.Environment == null ? null : new EnvironmentState(
                info.Environment.Name, info.Environment.Location, info.Environment.ProvisioningState, ManagedTags.IsManaged(info.Environment.Tags, o.AppName)),
            App(info.Timetracker), App(info.Db), App(info.Redis),
            info.Endpoint);
    }

    // 操作してよい範囲に無いリソースグループは、Azure を読まずにブロッカーにする。リソースグループを作成できない設定では、
    // リソースグループが無いこともブロッカーにする（計画時と実行の直前の両方で確かめる）
    private async Task<DeploymentPlan> PlanAsync(AzureAccess access, DeployOptions options, CancellationToken cancellationToken)
    {
        if (access.ResourceGroupBlocker(options.Subscription, options.ResourceGroup) is { } blocker)
            return new DeploymentPlan(options, [], [blocker], [], PermissionCheckResult.NotChecked("操作できる範囲に無いため確認していません。"));

        var plan = await ToolSupport.CallAzureAsync(access, _logger, () =>
            new DeploymentPlanner(CreateGateway(access)).PlanAsync(options, cancellationToken));
        return !access.CanCreateResourceGroup
               && plan.Operations.Any(op => op.Kind == PlannedResourceKind.ResourceGroup && op.Action == PlannedAction.Create)
            ? plan with { Blockers = [.. plan.Blockers, string.Format(CannotCreateResourceGroup, options.ResourceGroup)] }
            : plan;
    }

    // ツールのログに書き、方式の資格情報で操作する Azure の操作
    private IAzureDeploymentGateway CreateGateway(AzureAccess access)
    {
        var logger = new McpDeploymentLogger(_logger);
        return _gatewayFactory(logger, access.CreateCredential(logger));
    }

    // 存在しない・期限切れ・使用済み・ほかの呼び出し元・種類の違う計画は、実行できない理由と次の手順を返す
    internal static TPlan RequirePlan<TPlan>(PlanLookup<TPlan> lookup, string planId, string planTool) where TPlan : class => lookup.Status switch
    {
        PlanLookupStatus.Found => lookup.Plan!,
        PlanLookupStatus.Expired => throw new McpException($"計画 '{planId}' の有効期限（15 分）が切れました。{planTool} で計画を作り直してください。"),
        _ => throw new McpException($"計画 '{planId}' が見つかりません（使用済みか、サーバーが再起動されました）。{planTool} で計画を作り直してください。"),
    };

    internal static PermissionCheckInfo ToPermissionCheckInfo(PermissionCheckResult result) => new(
        result.Status switch
        {
            PermissionCheckStatus.Ok => "ok",
            PermissionCheckStatus.Missing => "missing",
            _ => "not_checked",
        },
        result.MissingActions,
        result.Note);

    private static PlannedActionInfo ToActionInfo(PlannedOperation op) => new(
        op.Kind switch
        {
            PlannedResourceKind.ResourceGroup => "resource_group",
            PlannedResourceKind.Environment => "environment",
            _ => "container_app",
        },
        op.Name,
        op.Action switch
        {
            PlannedAction.Create => "create",
            PlannedAction.UseExisting => "use_existing",
            _ => "update",
        },
        op.DataLoss);

    private static JobTargetInfo ToTargetInfo(JobTarget target) => new(target.Subscription, target.ResourceGroup, target.AppName);

    private static string KindName(JobKind kind) => kind == JobKind.Deploy ? "deploy" : "delete";

    private static string StatusName(JobStatus status) => status switch
    {
        JobStatus.Running => "running",
        JobStatus.Succeeded => "succeeded",
        _ => "failed",
    };

    // Progress<T> は SynchronizationContext 経由で非同期に通知するため、手順の順に記録できるよう同期的に呼ぶ
    internal sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
