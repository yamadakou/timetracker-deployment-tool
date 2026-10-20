using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Common;

namespace TTDeployTool.Application.Workflows;

/// <summary>削除の計画と、実行できない理由・保存した場合の ID と有効期限（削除するものが無い・ブロッカーがあれば保存しない）。</summary>
public sealed record DeletionPlanTicket(DeletionPlan Plan, IReadOnlyList<string> Blockers, SavedPlan? Saved)
{
    public bool CanRun => Saved != null;
}

/// <summary>
/// 削除のジョブの結果。<see cref="AlreadyDeleted"/> は、実行の時点で既に無かったもの。
/// <see cref="ResourceGroupDeleted"/> は、リソースグループも削除したか（選ばなかった場合と、既に無かった場合は false）。
/// </summary>
public sealed record DeletionOutcome(string ResourceGroup, IReadOnlyList<string> Deleted, IReadOnlyList<string> AlreadyDeleted, bool ResourceGroupDeleted = false);

/// <summary>
/// 削除の計画 → 確認 → 実行（MCP の plan_deletion / start_deletion とデスクトップアプリで共通）。削除の規則は CLI の delete と同じ
/// （<see cref="DeletionPlanner"/> / <see cref="DeletionRunner"/>）。実行には、利用者の確認として計画のアプリ名と同じ入力を求める。
/// </summary>
public sealed class DeletionWorkflow
{
    public const string PermissionBlocker =
        "権限が不足しています: {0}。README の「必要な RBAC ロールと権限」を参照し、ロールの割り当てを確認してください。";

    private readonly IPlanStore _plans;
    private readonly JobManager _jobs;
    private readonly ICallerContext _caller;
    private readonly IDeploymentLogger _logger;
    private readonly TimeProvider _time;

    /// <param name="logger">計画で Azure を読むときのログの出力先（ジョブではジョブのログに書く）</param>
    /// <param name="time">リソースグループが空になるのを待つときの時刻（省略時はシステムの時刻）</param>
    public DeletionWorkflow(IPlanStore plans, JobManager jobs, ICallerContext caller, IDeploymentLogger logger, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _plans = plans;
        _jobs = jobs;
        _caller = caller;
        _logger = logger;
    }

    /// <summary>削除の計画を作り、実行できれば保存する（Azure は変更しない）。</summary>
    /// <param name="keepDbData">DB のデータを置いたストレージアカウントを残すか</param>
    /// <param name="context">計画に付ける情報（実行のときに <see cref="StartAsync"/> の sessionFor に渡る）</param>
    /// <param name="deleteResourceGroup">本ツールのリソースを削除した後に空になる場合、リソースグループも削除するか</param>
    public async Task<DeletionPlanTicket> PlanAsync(
        IAzureSession session, string subscription, string resourceGroup, string appName, bool includeUntagged, bool keepDbData,
        string? context = null, CancellationToken cancellationToken = default, bool deleteResourceGroup = false)
    {
        var plan = await new DeletionPlanner(session.CreateGateway(_logger))
            .PlanAsync(subscription, resourceGroup, appName, includeUntagged, keepDbData, deleteResourceGroup, cancellationToken);
        var blockers = Blockers(plan);
        var saved = plan.HasDeletions && blockers.Count == 0
            ? _plans.Save(new StoredPlan<DeletionPlan>(plan, context), _caller.CallerId)
            : null;
        return new DeletionPlanTicket(plan, blockers, saved);
    }

    /// <summary>保存した削除の計画を読む（無効にしない）。</summary>
    /// <exception cref="WorkflowException">計画が無い・期限切れ</exception>
    public StoredPlan<DeletionPlan> Find(string planId) => DeploymentWorkflow.Require(_plans.Find<StoredPlan<DeletionPlan>>(planId, _caller.CallerId));

    /// <summary>
    /// 計画に基づいて削除のジョブを始める。アプリ名が一致しない場合と、計画の情報から接続を作れない場合は、計画を使用済みにしない
    /// （利用者に確認して、同じ計画で実行し直せるように）。それ以外は、実行しなかった場合も計画を使用済みにする。
    /// </summary>
    /// <param name="confirmAppName">利用者が入力したアプリ名（計画のアプリ名と完全に一致すること）</param>
    /// <param name="sessionFor">計画に付けた情報から、実行に使う接続を作る（呼び出し元がまだ使えるかを確かめ、使えなければ例外を投げる）</param>
    /// <param name="keptDbDataNotice">DB のデータを残した場合に、完了のログに添える案内（フロントエンドの言葉で、次の手順を示す）</param>
    /// <exception cref="WorkflowException">実行しなかった場合</exception>
    public async Task<Job> StartAsync(
        string planId, string confirmAppName, Func<string?, IAzureSession> sessionFor, Func<DeletionPlan, string> keptDbDataNotice,
        CancellationToken cancellationToken = default)
    {
        var stored = Find(planId);
        if (confirmAppName != stored.Plan.AppName)
            throw new WorkflowException(WorkflowFailure.AppNameMismatch);
        var session = sessionFor(stored.Context);
        var plan = DeploymentWorkflow.Require(_plans.Take<StoredPlan<DeletionPlan>>(planId, _caller.CallerId)).Plan;

        // 操作してよい範囲と、計画の後に Azure の状態が変わっていないかを、実行の直前にもう一度確かめる
        if (session.ScopeBlocker(plan.Subscription, plan.ResourceGroup) is { } outOfScope)
            throw new WorkflowException(WorkflowFailure.OutOfScope, [outOfScope]);
        var current = await new DeletionPlanner(session.CreateGateway(_logger))
            .PlanAsync(plan.Subscription, plan.ResourceGroup, plan.AppName, plan.IncludeUntagged, plan.KeepDbData, plan.DeleteResourceGroupRequested,
                cancellationToken);
        var blockers = Blockers(current);
        if (blockers.Count > 0)
            throw new WorkflowException(WorkflowFailure.Blocked, blockers);
        if (!plan.HasSameDeletions(current))
            throw new WorkflowException(WorkflowFailure.Changed);

        var total = plan.Targets.Count(t => t.WillDelete) + (plan.DeletesResourceGroup ? 1 : 0);
        var target = new JobTarget(plan.Subscription, plan.ResourceGroup, plan.AppName);
        var started = _jobs.TryStart(JobKind.Delete, target, _caller.CallerId, async context =>
        {
            var index = 0;
            var progress = new ActionProgress<DeletionTarget>(t =>
            {
                context.ReportStep(++index, total, $"{t.Name} の削除");
                context.Logger.Info($"削除中: {t.Name}");
            });
            var runner = new DeletionRunner(session.CreateGateway(context.Logger), _time);
            var results = await runner.RunAsync(plan, progress);
            foreach (var r in results.Where(r => !r.Deleted))
                context.Logger.Info($"{r.Name} は既に削除されていました。");
            var resourceGroupDeleted = false;
            if (plan.DeletesResourceGroup)
            {
                context.ReportStep(++index, total, $"リソースグループ {plan.ResourceGroup} の削除");
                context.Logger.Info($"削除中: リソースグループ {plan.ResourceGroup}");
                resourceGroupDeleted = await runner.DeleteResourceGroupAsync(plan);
                if (!resourceGroupDeleted)
                    context.Logger.Info($"リソースグループ {plan.ResourceGroup} は既に削除されていました。");
            }
            context.Logger.Success(CompletedMessage(plan));
            if (plan.KeepsDbData)
                context.Logger.Info(keptDbDataNotice(plan));
            return new DeletionOutcome(
                plan.ResourceGroup,
                results.Where(r => r.Deleted).Select(r => r.Name).ToList(),
                results.Where(r => !r.Deleted).Select(r => r.Name).ToList(),
                resourceGroupDeleted);
        });
        return started.Job ?? throw new WorkflowException(WorkflowFailure.Conflict, target: target, conflictingJobId: started.ConflictingJobId);
    }

    /// <summary>削除が終わったときのメッセージ（CLI と同じ）。</summary>
    public static string CompletedMessage(DeletionPlan plan) => plan.DeletesResourceGroup
        ? $"削除が完了しました（リソースグループ {plan.ResourceGroup} も削除しました）。"
        : $"削除が完了しました（リソースグループ {plan.ResourceGroup} は残しています）。";

    private static List<string> Blockers(DeletionPlan plan)
    {
        var blockers = new List<string>();
        if (plan.ResourceGroupBlocker is { } resourceGroup)
            blockers.Add(resourceGroup);
        if (plan.HasDeletions && plan.PermissionCheck.Status == PermissionCheckStatus.Missing)
            blockers.Add(string.Format(PermissionBlocker, string.Join(", ", plan.PermissionCheck.MissingActions)));
        return blockers;
    }
}
