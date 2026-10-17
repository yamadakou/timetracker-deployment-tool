using Azure.Core;
using ModelContextProtocol;
using Timetracker.Application.Workflows;
using Timetracker.Common;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Mcp.Tools;

/// <summary>方式（<see cref="AzureAccess"/>）の資格情報と範囲で Azure を操作する接続。</summary>
internal sealed class AccessSession : IAzureSession
{
    private readonly AzureAccess _access;
    private readonly Func<IDeploymentLogger, TokenCredential, IAzureDeploymentGateway> _gatewayFactory;

    public AccessSession(AzureAccess access, Func<IDeploymentLogger, TokenCredential, IAzureDeploymentGateway> gatewayFactory)
    {
        _access = access;
        _gatewayFactory = gatewayFactory;
    }

    public IAzureDeploymentGateway CreateGateway(IDeploymentLogger logger) => _gatewayFactory(logger, _access.CreateCredential(logger));

    public string? ScopeBlocker(string subscription, string resourceGroup) => _access.ResourceGroupBlocker(subscription, resourceGroup);

    public string? ResourceGroupCreationBlocker(string resourceGroup) =>
        _access.CanCreateResourceGroup ? null : string.Format(DeploymentJobTools.CannotCreateResourceGroup, resourceGroup);
}

/// <summary>計画 → 確認 → 実行で実行しなかった理由を、ツールの名前を使った次の手順の案内にする。</summary>
internal static class WorkflowMessages
{
    /// <param name="planTool">計画を作り直すツール（plan_deployment / plan_deletion）</param>
    /// <param name="changed">計画の後に状態が変わった場合のメッセージ</param>
    public static McpException ToMcpException(WorkflowException ex, string planId, string planTool, string changed) => new(ex.Failure switch
    {
        WorkflowFailure.PlanExpired => $"計画 '{planId}' の有効期限（15 分）が切れました。{planTool} で計画を作り直してください。",
        WorkflowFailure.DataLossNotAcknowledged =>
            "この計画は既存の DB アプリを更新します。設定が変わると DB コンテナが再起動し、DB のデータは失われます。利用者が同意した場合だけ、" +
            "acknowledge_data_loss=true を指定して実行してください。",
        WorkflowFailure.OutOfScope => ex.Blockers[0],
        WorkflowFailure.Blocked => "実行の直前の確認で、実行できない状態になっていました:\n- " + string.Join("\n- ", ex.Blockers),
        WorkflowFailure.Changed => changed,
        WorkflowFailure.Conflict =>
            $"同じ対象（{ex.Target!.ResourceGroup} / {ex.Target.AppName}）のジョブ {ex.ConflictingJobId} が実行中です。" +
            $"get_job_status で完了を確認してから、{planTool} で計画を作り直してください。",
        _ => $"計画 '{planId}' が見つかりません（使用済みか、サーバーが再起動されました）。{planTool} で計画を作り直してください。",
    });
}
