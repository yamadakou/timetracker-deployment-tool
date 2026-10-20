using System.ComponentModel;
using Azure.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Common;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Mcp.Tools;

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
    [Description("本ツールが作ったリソース（{app}-tt / -redis / -db / -env と、persist_db で作ったストレージアカウント）を削除する計画を作る（Azure を読むだけで、変更しない）。" +
                 "対象ごとに削除するか・しない理由、残すもの（リソースグループは既定では削除しない）、警告、権限の確認結果を返す。" +
                 "結果を利用者に示し、削除してよいかとアプリ名を確認してから start_deletion に渡す。" +
                 "ブロッカーがある場合や削除するものが無い場合は plan_id が null になる。計画の有効期限は 15 分。")]
    public async Task<DeletionPlanResult> PlanDeletion(
        [Description("サブスクリプション ID（省略時はサーバーの既定値 TT_MCP_DEFAULT_SUBSCRIPTION）")] string? subscription = null,
        [Description("リソースグループ名（省略時はサーバーの既定値 TT_MCP_DEFAULT_RESOURCE_GROUP）")] string? resource_group = null,
        [Description("アプリ名（既定 timetracker）")] string? app_name = null,
        [Description("管理タグの無いリソース（本ツールの以前の版で作ったものなど）も対象にするか（既定 false）。" +
                     "別のアプリ・ツールの管理タグがあるものは対象にしない")] bool include_untagged = false,
        [Description("persist_db で DB のデータを置いたストレージアカウントを残すか（既定 false: DB のデータごと削除する）。" +
                     "残すと料金がかかり続けるが、同じアプリ名で persist_db=true のデプロイをするとデータを引き継げる")] bool keep_db_data = false,
        [Description("リソースグループも削除するか（既定 false）。本ツールのリソースを削除した後にリソースグループが空になる場合だけ、最後に削除する。" +
                     "ほかのリソース（別のアプリ名の環境を含む）が残る場合は blockers に理由が出て、何も削除しない。元に戻せない")] bool delete_resource_group = false,
        [Description(DeploymentTools.AzureIdentityDescription)] string? azure_identity = null,
        CancellationToken cancellationToken = default)
    {
        var access = _policy.Resolve(azure_identity);
        var o = ToolSupport.ResolveTarget(_settings, access, subscription, resource_group, app_name);
        var ticket = await ToolSupport.CallAzureAsync(access, _logger, () =>
            Workflow().PlanAsync(new AccessSession(access, _gatewayFactory), o.Subscription, o.ResourceGroup, o.AppName, include_untagged, keep_db_data, access.Mode, cancellationToken,
                delete_resource_group));
        var plan = ticket.Plan;
        var blockers = ticket.Blockers;
        var warnings = new List<string>();
        if (plan.LosesDbData)
            warnings.Add(DbDataLossWarning);
        if (plan.DeletesDbData)
            warnings.Add($"ストレージアカウント {plan.StorageAccountName}（Azure Files に永続化した DB のデータ）も削除します。元に戻せません（残すには keep_db_data=true）。");
        if (plan.DeletesResourceGroup)
            warnings.Add($"リソースグループ {plan.ResourceGroup} も削除します。元に戻せません。");
        if (plan.HasUnmanaged)
            warnings.Add("管理タグが無いため削除しないリソースがあります。本ツールで作ったものであれば、include_untagged=true で対象にできます。");
        if (plan.PermissionCheck.Status == PermissionCheckStatus.NotChecked)
            warnings.Add($"権限を事前に確認できませんでした: {plan.PermissionCheck.Note}");

        var canRun = ticket.CanRun;
        return new DeletionPlanResult(
            PlanId: ticket.Saved?.PlanId,
            ExpiresAt: ticket.Saved?.ExpiresAt,
            Blockers: blockers,
            Identity: access.Identity,
            PermissionCheck: DeploymentJobTools.ToPermissionCheckInfo(plan.PermissionCheck),
            Targets: Targets(plan),
            Kept: Kept(plan),
            Warnings: warnings,
            DeletesDb: plan.DeletesDb,
            ConfirmationRequired: canRun ? $"start_deletion の confirm_app_name に '{o.AppName}' を指定してください（利用者に入力してもらってください）" : null,
            Note: plan.HasDeletions ? null : "削除するリソースはありません。",
            DeletesDbData: plan.DeletesDbData,
            KeepsDbData: plan.KeepsDbData,
            DeletesResourceGroup: plan.DeletesResourceGroup);
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
        var workflow = Workflow();
        Job job;
        AzureAccess? access = null;
        try
        {
            // 計画を作った方式で実行する（呼び出し元がまだその方式を使えるかも確かめる）
            job = await ToolSupport.CallAzureAsync(() => access, _logger, () =>
                workflow.StartAsync(plan_id, confirm_app_name,
                    mode => new AccessSession(access = _policy.ForMode(mode!), _gatewayFactory), KeptDbDataNotice, cancellationToken));
        }
        catch (WorkflowException ex) when (ex.Failure == WorkflowFailure.AppNameMismatch)
        {
            // 一致しない場合は計画を使用済みにしない（利用者に確認して、同じ計画で実行し直せるように）
            throw new McpException(
                $"confirm_app_name '{confirm_app_name}' が計画のアプリ名 '{workflow.Find(plan_id).Plan.AppName}' と一致しないため、削除しませんでした。" +
                "削除してよいかとアプリ名を利用者に確認してください。");
        }
        catch (WorkflowException ex)
        {
            throw WorkflowMessages.ToMcpException(ex, plan_id, "plan_deletion",
                "計画の後に Azure の状態が変わりました（リソースが作成・削除された、管理タグが変わったなど）。plan_deletion で計画を作り直してください。");
        }
        var plan = job.Target;
        _logger.LogInformation("削除のジョブ {JobId} を開始しました（{ResourceGroup} / {AppName}）", job.Id, plan.ResourceGroup, plan.AppName);
        return new StartJobResult(job.Id, "delete", "running", DeploymentJobTools.PollHint);
    }

    // 計画 → 確認 → 実行の安全策（計画で Azure を読むときは、ツールのログに書く）
    private DeletionWorkflow Workflow() => new(_plans, _jobs, _caller, new McpDeploymentLogger(_logger));

    // 削除しないもの（リソースグループと、keep_db_data で残すストレージアカウント）
    private static List<string> Kept(DeletionPlan plan)
    {
        var kept = new List<string>();
        if (!plan.DeletesResourceGroup)
            kept.Add($"resource_group: {plan.ResourceGroup}（削除しません。不要であれば delete_resource_group=true で計画を作り直すか、Azure ポータルや az group delete で削除してください）");
        if (plan.KeepsDbData)
            kept.Add($"storage_account: {KeptDbDataNotice(plan)}");
        return kept;
    }

    private static string KeptDbDataNotice(DeletionPlan plan) =>
        $"ストレージアカウント {plan.StorageAccountName}（DB のデータ）は残します。料金がかかり続けます。同じアプリ名で persist_db=true のデプロイをすると" +
        "データを引き継ぎます。不要になったら keep_db_data を指定せずに削除してください。";

    // リソースグループの削除を選んだ場合は、リソースグループを最後の対象として加える
    private static List<DeletionTargetInfo> Targets(DeletionPlan plan)
    {
        var targets = plan.Targets.Select(ToTargetInfo).ToList();
        if (plan.ResourceGroupTarget is { } rg)
            targets.Add(new DeletionTargetInfo(plan.ResourceGroup, "resource_group", rg.Exists, false, rg.WillDelete, rg.Reason));
        return targets;
    }

    private static DeletionTargetInfo ToTargetInfo(DeletionTarget t) => new(
        t.Name,
        t.Kind switch
        {
            DeletionResourceKind.ContainerApp => "container_app",
            DeletionResourceKind.Environment => "environment",
            _ => "storage_account",
        },
        t.Exists, t.Managed, t.WillDelete, t.Reason);
}
