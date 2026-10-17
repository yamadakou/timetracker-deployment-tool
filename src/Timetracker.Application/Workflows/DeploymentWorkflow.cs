using Timetracker.Application.Deployment;
using Timetracker.Application.Jobs;
using Timetracker.Application.Permissions;
using Timetracker.Common;
using Timetracker.Domain.Deployment;

namespace Timetracker.Application.Workflows;

/// <summary>計画と、保存した場合の ID・有効期限（ブロッカーがあれば保存しない）。</summary>
public sealed record DeploymentPlanTicket(DeploymentPlan Plan, SavedPlan? Saved)
{
    public const string LongDuration = "5〜15 分（Environment を新規作成するため）";
    public const string ShortDuration = "2〜5 分";

    /// <summary>所要時間の目安（Environment の新規作成に時間がかかる）。</summary>
    public string EstimatedDuration => Plan.CreatesEnvironment ? LongDuration : ShortDuration;
}

/// <summary>
/// デプロイの計画 → 確認 → 実行（MCP の plan_deployment / start_deployment とデスクトップアプリで共通）。
/// 実行は保存した計画に対してだけ行い、DB を更新する計画では利用者の同意を求め、実行の直前に計画を作り直して状態が変わっていないかを確かめる。
/// </summary>
public sealed class DeploymentWorkflow
{
    private readonly IPlanStore _plans;
    private readonly JobManager _jobs;
    private readonly ICallerContext _caller;
    private readonly IDeploymentLogger _logger;

    /// <param name="logger">計画で Azure を読むときのログの出力先（ジョブではジョブのログに書く）</param>
    public DeploymentWorkflow(IPlanStore plans, JobManager jobs, ICallerContext caller, IDeploymentLogger logger)
    {
        _plans = plans;
        _jobs = jobs;
        _caller = caller;
        _logger = logger;
    }

    /// <summary>計画を作り、実行できれば保存する（Azure は変更しない）。</summary>
    /// <param name="options">検証済みの設定</param>
    /// <param name="context">計画に付ける情報（実行のときに <see cref="StartAsync"/> の sessionFor に渡る）</param>
    public async Task<DeploymentPlanTicket> PlanAsync(
        IAzureSession session, DeployOptions options, string? context = null, CancellationToken cancellationToken = default)
    {
        var plan = await PlanCoreAsync(session, options, cancellationToken);
        var saved = plan.CanRun ? _plans.Save(new StoredPlan<DeploymentPlan>(plan, context), _caller.CallerId) : null;
        return new DeploymentPlanTicket(plan, saved);
    }

    /// <summary>保存した計画を読む（無効にしない）。</summary>
    /// <exception cref="WorkflowException">計画が無い・期限切れ</exception>
    public StoredPlan<DeploymentPlan> Find(string planId) => Require(_plans.Find<StoredPlan<DeploymentPlan>>(planId, _caller.CallerId));

    /// <summary>
    /// 計画に基づいてデプロイのジョブを始める。同意が無い場合と、計画の情報から接続を作れない場合は、計画を使用済みにしない
    /// （同意を得て、同じ計画で実行し直せるように）。それ以外は、実行しなかった場合も計画を使用済みにする。
    /// </summary>
    /// <param name="sessionFor">計画に付けた情報から、実行に使う接続を作る（呼び出し元がまだ使えるかを確かめ、使えなければ例外を投げる）</param>
    /// <exception cref="WorkflowException">実行しなかった場合</exception>
    public async Task<Job> StartAsync(
        string planId, bool acknowledgeDataLoss, Func<string?, IAzureSession> sessionFor, CancellationToken cancellationToken = default)
    {
        var stored = Find(planId);
        if (stored.Plan.RequiresDataLossAck && !acknowledgeDataLoss)
            throw new WorkflowException(WorkflowFailure.DataLossNotAcknowledged);
        var session = sessionFor(stored.Context);
        var plan = Require(_plans.Take<StoredPlan<DeploymentPlan>>(planId, _caller.CallerId)).Plan;

        // 計画の後に Azure の状態が変わっていないかを、実行の直前にもう一度確かめる
        var current = await PlanCoreAsync(session, plan.Options, cancellationToken);
        if (!current.CanRun)
            throw new WorkflowException(WorkflowFailure.Blocked, current.Blockers);
        if (!plan.HasSameOperations(current))
            throw new WorkflowException(WorkflowFailure.Changed);

        var o = plan.Options;
        var target = new JobTarget(o.Subscription, o.ResourceGroup, o.AppName);
        var started = _jobs.TryStart(JobKind.Deploy, target, _caller.CallerId, async context =>
        {
            var progress = new ActionProgress<DeploymentProgress>(p => context.ReportStep(p.Index, p.Total, p.Description));
            return await new DeploymentRunner(session.CreateGateway(context.Logger)).RunAsync(o, progress);
        });
        return started.Job ?? throw new WorkflowException(WorkflowFailure.Conflict, target: target, conflictingJobId: started.ConflictingJobId);
    }

    // 操作してよい範囲に無いリソースグループは、Azure を読まずにブロッカーにする。リソースグループを作成できない接続では、
    // リソースグループが無いこともブロッカーにする（計画時と実行の直前の両方で確かめる）
    private async Task<DeploymentPlan> PlanCoreAsync(IAzureSession session, DeployOptions options, CancellationToken cancellationToken)
    {
        if (session.ScopeBlocker(options.Subscription, options.ResourceGroup) is { } blocker)
            return new DeploymentPlan(options, [], [blocker], [], PermissionCheckResult.NotChecked("操作できる範囲に無いため確認していません。"));

        var plan = await new DeploymentPlanner(session.CreateGateway(_logger)).PlanAsync(options, cancellationToken);
        return plan.Operations.Any(op => op.Kind == PlannedResourceKind.ResourceGroup && op.Action == PlannedAction.Create)
               && session.ResourceGroupCreationBlocker(options.ResourceGroup) is { } cannotCreate
            ? plan with { Blockers = [.. plan.Blockers, cannotCreate] }
            : plan;
    }

    internal static TPlan Require<TPlan>(PlanLookup<TPlan> lookup) where TPlan : class => lookup.Status switch
    {
        PlanLookupStatus.Found => lookup.Plan!,
        PlanLookupStatus.Expired => throw new WorkflowException(WorkflowFailure.PlanExpired),
        _ => throw new WorkflowException(WorkflowFailure.PlanNotFound),
    };
}

/// <summary>
/// 呼んだスレッドで、すぐに通知する IProgress。<see cref="Progress{T}"/> は SynchronizationContext 経由で非同期に通知するため、
/// 手順の順に記録できるよう同期的に呼ぶ。
/// </summary>
public sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
