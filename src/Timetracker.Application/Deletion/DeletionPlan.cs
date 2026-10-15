using Timetracker.Application.Permissions;
using Timetracker.Domain.Deployment;

namespace Timetracker.Application.Deletion;

public enum DeletionResourceKind
{
    ContainerApp,
    Environment,
}

/// <summary>削除計画の対象 1 件。<see cref="Reason"/> は削除しない場合の理由。</summary>
public sealed record DeletionTarget(string Name, DeletionResourceKind Kind, bool Exists, bool Managed, bool WillDelete, string? Reason);

/// <summary>
/// 削除計画。対象は削除する順（Timetracker → Redis → DB → Environment）に並ぶ。リソースグループは削除しない。
/// <see cref="IncludeUntagged"/> は管理タグの無いリソースも対象にするか（削除の直前の確認でも同じ規則を使う）。
/// </summary>
public sealed record DeletionPlan(
    string Subscription, string ResourceGroup, string AppName, IReadOnlyList<DeletionTarget> Targets, PermissionCheckResult PermissionCheck,
    bool IncludeUntagged = false)
{
    public bool HasDeletions => Targets.Any(t => t.WillDelete);

    /// <summary>DB アプリを削除するか（DB のデータが失われる）。</summary>
    public bool DeletesDb => Targets.Any(t => t.WillDelete && t.Name == ResourceNaming.DbApp(AppName));

    /// <summary>管理タグが無いため削除しない対象があるか（--include-untagged で対象にできる）。</summary>
    public bool HasUnmanaged => Targets.Any(t => t.Reason == DeletionPlanner.UnmanagedReason);
}
