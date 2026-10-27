using System.Text.Json;
using System.Text.Json.Serialization;
using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>
/// アプリを終了しても残す予定のジョブ（docs/design/desktop-jobs.md の 6.6・6.7、D23〜D28）。削除は <see cref="Deletion"/>、
/// デプロイは <see cref="Deployment"/>（パスワードは暗号化）を持ち、次の起動で戻す。どちらも無いデプロイ（保存しないと選んだもの）は、
/// 対象と時刻だけを記録して、消えたことを次の起動で知らせる。
/// </summary>
/// <param name="ScheduledAt">予定の時刻（待機中なら null）</param>
/// <param name="Account">保存したときにサインインしていたアカウントの名前（同じアカウントでサインインしたときに戻す。無ければ誰でも）</param>
public sealed record SavedJob(
    JobKind Kind, string Subscription, string ResourceGroup, string AppName, DateTimeOffset? ScheduledAt, bool Paused, string? Account, SavedDeletion? Deletion,
    SavedDeployment? Deployment = null)
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

/// <summary>
/// 確かめたデプロイの計画（docs/design/desktop-jobs.md の 6.7、D25）。<see cref="Options"/> のパスワードは空にし、
/// <see cref="ISecretProtector"/> で暗号化したものを別に持つ。<see cref="Operations"/> は確かめた計画の操作（始める前に作り直した計画と比べる）。
/// </summary>
public sealed record SavedDeployment(
    DeployOptions Options, string ProtectedDbPassword, string ProtectedTrackerPassword, bool DataLossAcknowledged, IReadOnlyList<PlannedOperation> Operations)
{
    public static SavedDeployment From(DeploymentPlan plan, bool dataLossAcknowledged, ISecretProtector protector)
    {
        var options = Copy(plan.Options);
        var saved = new SavedDeployment(options, protector.Protect(options.DbPassword), protector.Protect(options.TrackerPassword), dataLossAcknowledged,
            [.. plan.Operations]);
        options.DbPassword = "";
        options.TrackerPassword = "";
        return saved;
    }

    /// <summary>パスワードを復号した設定（復号できなければ例外）。</summary>
    public DeployOptions Restore(ISecretProtector protector)
    {
        var options = Copy(Options);
        options.DbPassword = protector.Unprotect(ProtectedDbPassword);
        options.TrackerPassword = protector.Unprotect(ProtectedTrackerPassword);
        return options;
    }

    /// <summary>作り直した計画が、確かめた計画と同じ操作か（<see cref="DeploymentPlan.HasSameOperations"/> と同じ）。</summary>
    public bool Matches(DeploymentPlan plan) => Operations.SequenceEqual(plan.Operations);

    private static DeployOptions Copy(DeployOptions options) => JsonSerializer.Deserialize<DeployOptions>(JsonSerializer.Serialize(options))!;
}

/// <summary>
/// 保存するパスワードの暗号化（画面の側で Windows の DPAPI で実装する。同じ PC の同じ Windows ユーザーだけが復号できる。6.7・D25）。
/// </summary>
public interface ISecretProtector
{
    string Protect(string value);

    /// <summary>復号する（できなければ例外）。</summary>
    string Unprotect(string protectedValue);
}

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
