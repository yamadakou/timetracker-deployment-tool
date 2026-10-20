using TTDeployTool.Application.Permissions;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Application.Deletion;

public enum DeletionResourceKind
{
    ContainerApp,
    Environment,
    StorageAccount,
}

/// <summary>削除計画の対象 1 件。<see cref="Reason"/> は削除しない場合の理由。</summary>
public sealed record DeletionTarget(string Name, DeletionResourceKind Kind, bool Exists, bool Managed, bool WillDelete, string? Reason);

/// <summary>
/// リソースグループを削除するかの判定（利用者が削除を選んだ場合だけ作る）。<see cref="Remaining"/> は、本ツールのリソースを削除した後も残るリソース
/// （「名前（種類）」。1 つでもあれば削除しない）。
/// </summary>
public sealed record ResourceGroupDeletion(bool Exists, bool WillDelete, string? Reason, IReadOnlyList<string> Remaining);

/// <summary>
/// 削除計画。対象は削除する順（Timetracker → Redis → DB → Environment → ストレージアカウント）に並ぶ。
/// <see cref="IncludeUntagged"/> は管理タグの無いリソースも対象にするか、<see cref="KeepDbData"/> は DB のデータを置いた
/// ストレージアカウントを残すか、<see cref="ResourceGroupTarget"/> はリソースグループも削除するか（null なら削除しない。
/// 削除の直前の確認でも同じ規則を使う）。リソースグループは、本ツールのリソースを削除した後に空になる場合だけ、最後に削除する。
/// </summary>
public sealed record DeletionPlan(
    string Subscription, string ResourceGroup, string AppName, IReadOnlyList<DeletionTarget> Targets, PermissionCheckResult PermissionCheck,
    bool IncludeUntagged = false, bool KeepDbData = false, ResourceGroupDeletion? ResourceGroupTarget = null)
{
    public bool HasDeletions => Targets.Any(t => t.WillDelete) || DeletesResourceGroup;

    /// <summary>利用者がリソースグループの削除を選んだか。</summary>
    public bool DeleteResourceGroupRequested => ResourceGroupTarget != null;

    /// <summary>最後にリソースグループを削除するか。</summary>
    public bool DeletesResourceGroup => ResourceGroupTarget?.WillDelete == true;

    /// <summary>
    /// リソースグループの削除を選んだのに削除できない理由（ほかのリソースが残るなど。リソースグループが無い場合は null）。
    /// 選んだ利用者が、リソースグループが残ることに気付かないまま本ツールのリソースだけを消さないよう、実行させない。
    /// </summary>
    public string? ResourceGroupBlocker => ResourceGroupTarget is { Exists: true, WillDelete: false, Reason: var reason }
        ? $"リソースグループ {ResourceGroup} は削除できません（{reason}）。リソースグループを残して削除する場合は、リソースグループの削除の指定を外してください。"
        : null;

    /// <summary>対象ごとの「削除するか」がほかの計画と同じか（削除しない理由の文言の違いは問わない。実行の直前の確認に使う）。</summary>
    public bool HasSameDeletions(DeletionPlan other) =>
        Targets.Select(t => (t.Name, t.WillDelete)).SequenceEqual(other.Targets.Select(t => (t.Name, t.WillDelete)))
        && DeletesResourceGroup == other.DeletesResourceGroup;

    /// <summary>DB アプリを削除するか。</summary>
    public bool DeletesDb => Targets.Any(t => t.WillDelete && t.Name == ResourceNaming.DbApp(AppName));

    /// <summary>DB のデータを置いたストレージアカウントを削除するか（Azure Files に永続化した DB のデータが失われる）。</summary>
    public bool DeletesDbData => Targets.Any(t => t.WillDelete && t.Kind == DeletionResourceKind.StorageAccount);

    /// <summary>DB のデータを置いたストレージアカウントがあり、--keep-db-data で残すか。</summary>
    public bool KeepsDbData => Targets.Any(t => t.Kind == DeletionResourceKind.StorageAccount && t.Reason == DeletionPlanner.KeptReason);

    /// <summary>DB のデータがすべて失われるか（DB アプリを削除し、ストレージアカウントを残さない）。</summary>
    public bool LosesDbData => DeletesDb && !KeepsDbData;

    /// <summary>DB のデータを置くストレージアカウントの名前（存在しなくても名前は決まっている）。</summary>
    public string StorageAccountName => ResourceNaming.StorageAccount(Subscription, ResourceGroup, AppName);

    /// <summary>管理タグが無いため削除しない対象があるか（--include-untagged で対象にできる）。</summary>
    public bool HasUnmanaged => Targets.Any(t => t.Reason == DeletionPlanner.UnmanagedReason);
}
