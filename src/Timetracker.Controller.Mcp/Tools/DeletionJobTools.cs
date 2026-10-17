using System.ComponentModel;
using Azure.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Timetracker.Application.Deletion;
using Timetracker.Application.Jobs;
using Timetracker.Application.Permissions;
using Timetracker.Common;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Mcp.Tools;

/// <summary>
/// Azure からの削除（計画 → 確認 → 実行）のツール（Phase 3c）。削除の規則は CLI の delete と同じ
/// （<see cref="DeletionPlanner"/> / <see cref="DeletionRunner"/>）。実行は plan_deletion で作った計画に対してだけ行い、
/// 利用者の確認として、計画のアプリ名と同じ confirm_app_name を求める。
/// </summary>
[McpServerToolType]
public sealed class DeletionJobTools
{
    public const string DbDataLossWarning = "DB アプリを削除すると DB のデータはすべて失われます。";

    private readonly McpServerSettings _settings;
    private readonly ICallerContext _caller;
    private readonly IAzureAccessPolicy _policy;
    private readonly IPlanStore _plans;
    private readonly JobManager _jobs;
    private readonly Func<IDeploymentLogger, TokenCredential, IAzureDeploymentGateway> _gatewayFactory;
    private readonly ILogger<DeletionJobTools> _logger;

    public DeletionJobTools(
        McpServerSettings settings, ICallerContext caller, IAzureAccessPolicy policy, IPlanStore plans, JobManager jobs,
        Func<IDeploymentLogger, TokenCredential, IAzureDeploymentGateway> gatewayFactory, ILogger<DeletionJobTools> logger)
    {
        _settings = settings;
        _caller = caller;
        _policy = policy;
        _plans = plans;
        _jobs = jobs;
        _gatewayFactory = gatewayFactory;
        _logger = logger;
    }

    [McpServerTool(Name = "plan_deletion", Title = "Azure からの削除の計画",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("本ツールが作ったリソース（{app}-tt / -redis / -db / -env）を削除する計画を作る（Azure を読むだけで、変更しない）。" +
                 "対象ごとに削除するか・しない理由、残すもの（リソースグループは削除しない）、警告、権限の確認結果を返す。" +
                 "結果を利用者に示し、削除してよいかとアプリ名を確認してから start_deletion に渡す。" +
                 "ブロッカーがある場合や削除するものが無い場合は plan_id が null になる。計画の有効期限は 15 分。")]
    public async Task<DeletionPlanResult> PlanDeletion(
        [Description("サブスクリプション ID（省略時はサーバーの既定値 TT_MCP_DEFAULT_SUBSCRIPTION）")] string? subscription = null,
        [Description("リソースグループ名（省略時はサーバーの既定値 TT_MCP_DEFAULT_RESOURCE_GROUP）")] string? resource_group = null,
        [Description("アプリ名（既定 timetracker）")] string? app_name = null,
        [Description("管理タグの無いリソース（本ツールの以前の版で作ったものなど）も対象にするか（既定 false）。" +
                     "別のアプリ・ツールの管理タグがあるものは対象にしない")] bool include_untagged = false,
        [Description(DeploymentTools.AzureIdentityDescription)] string? azure_identity = null,
        CancellationToken cancellationToken = default)
    {
        var access = _policy.Resolve(azure_identity);
        var o = ToolSupport.ResolveTarget(_settings, access, subscription, resource_group, app_name);
        var plan = await PlanAsync(access, o.Subscription, o.ResourceGroup, o.AppName, include_untagged, cancellationToken);

        var blockers = Blockers(plan);
        var warnings = new List<string>();
        if (plan.DeletesDb)
            warnings.Add(DbDataLossWarning);
        if (plan.HasUnmanaged)
            warnings.Add("管理タグが無いため削除しないリソースがあります。本ツールで作ったものであれば、include_untagged=true で対象にできます。");
        if (plan.PermissionCheck.Status == PermissionCheckStatus.NotChecked)
            warnings.Add($"権限を事前に確認できませんでした: {plan.PermissionCheck.Note}");

        var canRun = plan.HasDeletions && blockers.Count == 0;
        var saved = canRun ? _plans.Save(new IdentityPlan<DeletionPlan>(plan, access.Mode), _caller.CallerId) : null;
        return new DeletionPlanResult(
            PlanId: saved?.PlanId,
            ExpiresAt: saved?.ExpiresAt,
            Blockers: blockers,
            Identity: access.Identity,
            PermissionCheck: DeploymentJobTools.ToPermissionCheckInfo(plan.PermissionCheck),
            Targets: plan.Targets.Select(ToTargetInfo).ToList(),
            Kept: [$"resource_group: {o.ResourceGroup}（削除しません。不要であれば Azure ポータルや az group delete で削除してください）"],
            Warnings: warnings,
            DeletesDb: plan.DeletesDb,
            ConfirmationRequired: canRun ? $"start_deletion の confirm_app_name に '{o.AppName}' を指定してください（利用者に入力してもらってください）" : null,
            Note: plan.HasDeletions ? null : "削除するリソースはありません。");
    }

    [McpServerTool(Name = "start_deletion", Title = "Azure からの削除の開始",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("plan_deletion の計画に基づいて削除を始める（バックグラウンドのジョブ）。すぐに job_id を返すので、" +
                 "get_job_status で進捗と結果を確認する。confirm_app_name には、利用者が入力した（または明示的に確認した）アプリ名を指定する。" +
                 "計画のアプリ名と一致しなければ削除しない。計画は一度だけ使える。")]
    public async Task<StartJobResult> StartDeletion(
        [Description("plan_deletion が返した plan_id")] string plan_id,
        [Description("確認のためのアプリ名（計画のアプリ名と完全に一致すること）")] string confirm_app_name,
        CancellationToken cancellationToken = default)
    {
        _policy.RequireAzureAccess();
        var (plan, mode) = DeploymentJobTools.RequirePlan(_plans.Find<IdentityPlan<DeletionPlan>>(plan_id, _caller.CallerId), plan_id, "plan_deletion");
        // 一致しない場合は計画を使用済みにしない（利用者に確認して、同じ計画で実行し直せるように）
        if (confirm_app_name != plan.AppName)
            throw new McpException(
                $"confirm_app_name '{confirm_app_name}' が計画のアプリ名 '{plan.AppName}' と一致しないため、削除しませんでした。" +
                "削除してよいかとアプリ名を利用者に確認してください。");
        // 計画を作った方式で実行する（呼び出し元がまだその方式を使えるかも確かめる）
        var access = _policy.ForMode(mode);
        plan = DeploymentJobTools.RequirePlan(_plans.Take<IdentityPlan<DeletionPlan>>(plan_id, _caller.CallerId), plan_id, "plan_deletion").Plan;

        // 操作してよい範囲（サーバーの設定）と、計画の後に Azure の状態が変わっていないかを、実行の直前にもう一度確かめる
        if (access.ResourceGroupBlocker(plan.Subscription, plan.ResourceGroup) is { } outOfScope)
            throw new McpException(outOfScope);
        var current = await PlanAsync(access, plan.Subscription, plan.ResourceGroup, plan.AppName, plan.IncludeUntagged, cancellationToken);
        var blockers = Blockers(current);
        if (blockers.Count > 0)
            throw new McpException("実行の直前の確認で、実行できない状態になっていました:\n- " + string.Join("\n- ", blockers));
        if (!HasSameDeletions(plan, current))
            throw new McpException("計画の後に Azure の状態が変わりました（リソースが作成・削除された、管理タグが変わったなど）。plan_deletion で計画を作り直してください。");

        var total = plan.Targets.Count(t => t.WillDelete);
        var started = _jobs.TryStart(JobKind.Delete, new JobTarget(plan.Subscription, plan.ResourceGroup, plan.AppName), _caller.CallerId, async context =>
        {
            var index = 0;
            var progress = new DeploymentJobTools.ActionProgress<DeletionTarget>(t =>
            {
                context.ReportStep(++index, total, $"{t.Name} の削除");
                context.Logger.Info($"削除中: {t.Name}");
            });
            var results = await new DeletionRunner(_gatewayFactory(context.Logger, access.CreateCredential(context.Logger))).RunAsync(plan, progress);
            foreach (var r in results.Where(r => !r.Deleted))
                context.Logger.Info($"{r.Name} は既に削除されていました。");
            context.Logger.Success($"削除が完了しました（リソースグループ {plan.ResourceGroup} は残しています）。");
            return new DeletionJobResult(
                plan.ResourceGroup,
                results.Where(r => r.Deleted).Select(r => r.Name).ToList(),
                results.Where(r => !r.Deleted).Select(r => r.Name).ToList(),
                plan.ResourceGroup);
        });
        if (started.Job == null)
            throw new McpException(
                $"同じ対象（{plan.ResourceGroup} / {plan.AppName}）のジョブ {started.ConflictingJobId} が実行中です。" +
                "get_job_status で完了を確認してから、plan_deletion で計画を作り直してください。");
        _logger.LogInformation("削除のジョブ {JobId} を開始しました（{ResourceGroup} / {AppName}）", started.Job.Id, plan.ResourceGroup, plan.AppName);
        return new StartJobResult(started.Job.Id, "delete", "running", DeploymentJobTools.PollHint);
    }

    private Task<DeletionPlan> PlanAsync(
        AzureAccess access, string subscription, string resourceGroup, string appName, bool includeUntagged, CancellationToken cancellationToken) =>
        ToolSupport.CallAzureAsync(access, _logger, () =>
        {
            var logger = new McpDeploymentLogger(_logger);
            return new DeletionPlanner(_gatewayFactory(logger, access.CreateCredential(logger)))
                .PlanAsync(subscription, resourceGroup, appName, includeUntagged, cancellationToken: cancellationToken);
        });

    private static List<string> Blockers(DeletionPlan plan) =>
        plan.HasDeletions && plan.PermissionCheck.Status == PermissionCheckStatus.Missing
            ?
            [
                $"権限が不足しています: {string.Join(", ", plan.PermissionCheck.MissingActions)}。" +
                "README の「必要な RBAC ロールと権限」を参照し、ロールの割り当てを確認してください。",
            ]
            : [];

    // 対象ごとの「削除するか」が計画と同じか（削除しない理由の文言の違いは問わない）
    private static bool HasSameDeletions(DeletionPlan plan, DeletionPlan current) =>
        plan.Targets.Select(t => (t.Name, t.WillDelete)).SequenceEqual(current.Targets.Select(t => (t.Name, t.WillDelete)));

    private static DeletionTargetInfo ToTargetInfo(DeletionTarget t) => new(
        t.Name, t.Kind == DeletionResourceKind.ContainerApp ? "container_app" : "environment", t.Exists, t.Managed, t.WillDelete, t.Reason);
}
