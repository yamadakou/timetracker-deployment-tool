using TTDeployTool.Application.Permissions;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Application.Deployment;

public enum PlannedResourceKind
{
    ResourceGroup,
    Environment,
    ContainerApp,
    StorageAccount,
    FileShare,
    EnvironmentStorage,
}

public enum PlannedAction
{
    Create,
    UseExisting,
    Update,
}

/// <summary>デプロイで行う操作 1 件。<see cref="DataLoss"/> は、更新で DB のデータが失われる場合 true。</summary>
public sealed record PlannedOperation(PlannedResourceKind Kind, string Name, PlannedAction Action, bool DataLoss = false);

/// <summary>
/// デプロイの計画（読み取りだけで作る）。<see cref="Blockers"/> があれば実行できない。
/// <see cref="Options"/> はパスワードを含むため、結果やログに出さない。<see cref="Options"/> の PersistDb は、
/// 既存の DB アプリの状態から決めた実際の値（true / false）にしてある。<see cref="Expiry"/> は、記録する予定の利用可能期限
/// （実行するときに、その時刻と Azure の状態で決め直す）。
/// </summary>
public sealed record DeploymentPlan(
    DeployOptions Options,
    IReadOnlyList<PlannedOperation> Operations,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    PermissionCheckResult PermissionCheck,
    ExpiryDecision? Expiry = null)
{
    public bool CanRun => Blockers.Count == 0;

    /// <summary>DB のデータを Azure Files に置くか。</summary>
    public bool PersistDb => Options.PersistDb == true;

    /// <summary>DB アプリを更新するため、実行には利用者のデータ消失への同意が必要か。</summary>
    public bool RequiresDataLossAck => Operations.Any(o => o.DataLoss);

    /// <summary>Environment を新規作成するか（所要時間が長くなる）。</summary>
    public bool CreatesEnvironment => Operations.Any(o => o.Kind == PlannedResourceKind.Environment && o.Action == PlannedAction.Create);

    /// <summary>計画時と実行の直前で、行う操作が同じか（状態が変わっていないか）。</summary>
    public bool HasSameOperations(DeploymentPlan other) => Operations.SequenceEqual(other.Operations);
}
