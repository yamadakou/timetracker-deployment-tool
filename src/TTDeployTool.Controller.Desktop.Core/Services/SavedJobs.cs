using System.Text.Json.Serialization;
using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Jobs;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>
/// アプリを終了しても残す予定のジョブ（docs/design/desktop-jobs.md の 6.6、D23・D24）。削除は <see cref="Deletion"/> を持ち、次の起動で戻す。
/// デプロイはパスワードを持つため中身を保存せず、対象と時刻だけを記録して、消えたことを次の起動で知らせる。
/// </summary>
/// <param name="ScheduledAt">予定の時刻（待機中なら null）</param>
/// <param name="Account">保存したときにサインインしていたアカウントの名前（同じアカウントでサインインしたときに戻す。無ければ誰でも）</param>
public sealed record SavedJob(
    JobKind Kind, string Subscription, string ResourceGroup, string AppName, DateTimeOffset? ScheduledAt, bool Paused, string? Account, SavedDeletion? Deletion)
{
    [JsonIgnore]
    public JobTarget Target => new(Subscription, ResourceGroup, AppName);

    /// <summary>通知に出す内容（例: 「rg / demo 10/27 09:00」）。</summary>
    [JsonIgnore]
    public string Text => $"{ResourceGroup} / {AppName} {(ScheduledAt is { } at ? $"{at.ToLocalTime():MM/dd HH:mm}" : "すぐ実行")}";
}

/// <summary>確かめた削除の計画（選択・確かめたアプリ名・対象ごとに削除するか）。パスワード・トークンは含まない。</summary>
public sealed record SavedDeletion(
    bool IncludeUntagged, bool KeepDbData, bool DeleteResourceGroup, string ConfirmedAppName, IReadOnlyList<SavedDeletionTarget> Targets, bool DeletesResourceGroup)
{
    public static SavedDeletion From(DeletionPlan plan, string confirmedAppName) => new(
        plan.IncludeUntagged, plan.KeepDbData, plan.DeleteResourceGroupRequested, confirmedAppName,
        plan.Targets.Select(t => new SavedDeletionTarget(t.Name, t.WillDelete)).ToList(), plan.DeletesResourceGroup);

    /// <summary>作り直した計画が、確かめた計画と同じものを削除するか（<see cref="DeletionPlan.HasSameDeletions"/> と同じ）。</summary>
    public bool Matches(DeletionPlan plan) =>
        Targets.SequenceEqual(plan.Targets.Select(t => new SavedDeletionTarget(t.Name, t.WillDelete))) && DeletesResourceGroup == plan.DeletesResourceGroup;
}

public sealed record SavedDeletionTarget(string Name, bool WillDelete);

/// <summary>前回の Windows のサインイン時の起動（そのとき対話なしでサインインできたか）。</summary>
public sealed record StartupLaunch(DateTimeOffset At, bool SignedIn);

/// <summary>Windows の設定での、このアプリの通知の許可（<c>Microsoft.Windows.AppNotifications.AppNotificationSetting</c> と、確かめられない場合）。</summary>
public enum NotificationPermission
{
    Enabled,

    /// <summary>このアプリの通知を、利用者が Windows の設定で止めた。</summary>
    DisabledForApplication,

    /// <summary>すべてのアプリの通知を、利用者が止めた。</summary>
    DisabledForUser,

    DisabledByGroupPolicy,

    DisabledByManifest,

    /// <summary>この PC・起動の方法では確かめられない。</summary>
    Unavailable,
}

/// <summary>Windows の AppNotificationSetting の読み替え（画面の側が名前で渡す。<see cref="StartupTaskStates"/> と同じく単体テストで確かめるため）。</summary>
public static class NotificationPermissions
{
    public static NotificationPermission FromWindowsSetting(string name) =>
        Enum.TryParse<NotificationPermission>(name, out var permission) && Enum.IsDefined(permission) ? permission : NotificationPermission.Unavailable;
}
