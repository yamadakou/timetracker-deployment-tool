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
/// 削除計画。対象は削除する順（Timetracker → Redis → DB → Environment → ストレージアカウント）に並ぶ。リソースグループは削除しない。
/// <see cref="IncludeUntagged"/> は管理タグの無いリソースも対象にするか、<see cref="KeepDbData"/> は DB のデータを置いた
/// ストレージアカウントを残すか（削除の直前の確認でも同じ規則を使う）。
/// </summary>
public sealed record DeletionPlan(
    string Subscription, string ResourceGroup, string AppName, IReadOnlyList<DeletionTarget> Targets, PermissionCheckResult PermissionCheck,
    bool IncludeUntagged = false, bool KeepDbData = false)
{
    public bool HasDeletions => Targets.Any(t => t.WillDelete);

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
