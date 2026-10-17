namespace Timetracker.Controller.Mcp.Tools;

// デプロイの計画・実行・ジョブ・状態確認のツールの結果（プロパティ名は snake_case で返す）

/// <summary>
/// plan_deployment の結果。<see cref="PlanId"/> は、誤りやブロッカーがあれば null（実行できない計画は発行しない）。
/// </summary>
public sealed record DeploymentPlanResult(
    string? PlanId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Blockers,
    IdentityInfo Identity,
    PermissionCheckInfo PermissionCheck,
    IReadOnlyList<PlannedActionInfo> Actions,
    IReadOnlyList<string> Warnings,
    bool RequiresDataLossAck,
    string? EstimatedDuration,
    ResolvedSettings Resolved,
    ResourceNames ResourceNames);

/// <summary>Azure を操作する ID（stdio ではサーバーの認証モードの資格情報）。</summary>
public sealed record IdentityInfo(string AuthMode, string Display);

/// <summary>権限の事前確認。<see cref="Status"/> は ok / missing / not_checked。</summary>
public sealed record PermissionCheckInfo(string Status, IReadOnlyList<string> Missing, string? Note);

/// <summary>
/// 計画した操作。<see cref="Resource"/> は resource_group / environment / container_app、
/// <see cref="Action"/> は create / use_existing / update。
/// </summary>
public sealed record PlannedActionInfo(string Resource, string Name, string Action, bool DataLoss);

public sealed record StartJobResult(string JobId, string Kind, string Status, string PollHint);

public sealed record JobTargetInfo(string Subscription, string ResourceGroup, string AppName);

public sealed record JobStepInfo(int Index, int Total, string Name);

public sealed record JobLogInfo(string Level, string Message, DateTimeOffset At);

/// <summary>デプロイのジョブが成功したときの結果。<see cref="Endpoint"/> は Timetracker の URL（取得できなければ null）。</summary>
public sealed record DeploymentJobResult(string ResourceGroup, string Environment, string Tt, string Db, string Redis, string? Endpoint);

public sealed record JobStatusResult(
    string JobId,
    string Kind,
    string Status,
    JobTargetInfo Target,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    JobStepInfo? CurrentStep,
    IReadOnlyList<JobLogInfo> Logs,
    int NextLogOffset,
    DeploymentJobResult? Result,
    DeletionJobResult? DeletionResult,
    string? Error,
    string? DeviceCodeMessage);

public sealed record JobSummary(string JobId, string Kind, string Status, JobTargetInfo Target, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record JobListResult(IReadOnlyList<JobSummary> Jobs);

/// <summary>get_deployment_info の結果。存在しないリソースは null。</summary>
public sealed record DeploymentInfoResult(
    bool Exists,
    string Subscription,
    string ResourceGroup,
    string AppName,
    ResourceGroupState? ResourceGroupInfo,
    EnvironmentState? Environment,
    ContainerAppState? Tt,
    ContainerAppState? Db,
    ContainerAppState? Redis,
    string? Endpoint);

public sealed record ResourceGroupState(string Name, string Location);

/// <summary><see cref="Managed"/> は本ツールの管理タグが付いているか（delete の対象になるか）。</summary>
public sealed record EnvironmentState(string Name, string Location, string? ProvisioningState, bool Managed);

public sealed record ContainerAppState(
    string Name, string? ProvisioningState, string? RunningStatus, string? Image, double? Cpu, string? Memory,
    string? LatestRevision, string? Fqdn, bool Managed);

/// <summary>
/// plan_deletion の結果。<see cref="PlanId"/> は、ブロッカーがある場合や削除するものが無い場合は null（理由は <see cref="Note"/>）。
/// </summary>
public sealed record DeletionPlanResult(
    string? PlanId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Blockers,
    IdentityInfo Identity,
    PermissionCheckInfo PermissionCheck,
    IReadOnlyList<DeletionTargetInfo> Targets,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Warnings,
    bool DeletesDb,
    string? ConfirmationRequired,
    string? Note);

/// <summary>削除計画の対象。<see cref="Resource"/> は container_app / environment。<see cref="Reason"/> は削除しない理由。</summary>
public sealed record DeletionTargetInfo(string Name, string Resource, bool Exists, bool Managed, bool WillDelete, string? Reason);

/// <summary>削除のジョブが成功したときの結果。<see cref="AlreadyDeleted"/> は計画の後に既に無かったもの。</summary>
public sealed record DeletionJobResult(string ResourceGroup, IReadOnlyList<string> Deleted, IReadOnlyList<string> AlreadyDeleted, string KeptResourceGroup);
