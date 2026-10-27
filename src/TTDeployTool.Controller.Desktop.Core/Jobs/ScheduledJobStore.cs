using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.Jobs;

/// <summary>
/// アプリを終了しても予定を残す（docs/design/desktop-jobs.md の 6.6・6.7、D23〜D28）。まだ始めていない（予定・待機中・一時停止の）ジョブを、
/// 変わるたびにアプリのデータに保存する。次にサインインしたときに、削除と、パスワードを暗号化して保存したデプロイをジョブとして登録し直す。
/// 予定の時刻を過ぎたもの（待機中だったものを含む）は、一時停止で戻し、実行するか取り消すかを利用者に選ばせる。保存しなかったデプロイは、
/// 消えたことを通知する。違うアカウントで保存したものは、そのアカウントでサインインするまで残す。
/// </summary>
public sealed class ScheduledJobStore : ObservableObject
{
    private readonly JobQueue _queue;
    private readonly IAppPreferences _preferences;
    private readonly Func<string?> _account;
    private readonly Func<SavedJob, DeployOptions?, Func<Task<Job>>> _starter;
    private readonly ISecretProtector? _secrets;
    private readonly INotificationService? _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly List<SavedJob> _pending;

    /// <param name="account">今サインインしているアカウントの名前（していなければ null）</param>
    /// <param name="starter">保存した予定を始める処理（デプロイは復号した設定を受け取る。どちらも計画を作り直して確かめる）</param>
    /// <param name="secrets">保存したデプロイのパスワードの復号（無ければデプロイは戻さない）</param>
    public ScheduledJobStore(
        JobQueue queue, IAppPreferences preferences, Func<string?> account, Func<SavedJob, DeployOptions?, Func<Task<Job>>> starter, ISecretProtector? secrets,
        INotificationService? notifications, TimeProvider time, ILogger logger)
    {
        _queue = queue;
        _preferences = preferences;
        _account = account;
        _starter = starter;
        _secrets = secrets;
        _notifications = notifications;
        _time = time;
        _logger = logger;
        _pending = [.. preferences.SavedJobs];
        // 登録・消去と、状態・予定の時刻の変更（どれも Summary の変更を通知する）で保存する
        queue.Jobs.CollectionChanged += (_, _) => Save();
        queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.Summary))
                Save();
        };
    }

    /// <summary>前回のアプリで保存し、まだ戻していない予定の数（戻せるもの。違うアカウントのものを含む）。</summary>
    public int PendingJobs => _pending.Count(j => j.Deletion != null || j.Deployment != null);

    /// <summary>
    /// サインインしたとき。同じアカウント（か、アカウントの無い記録）の予定を戻し、戻せなかったデプロイの予定（保存しなかったもの・
    /// 復号できなかったもの）を通知する。
    /// </summary>
    public void Restore()
    {
        var account = _account();
        var mine = _pending.Where(j => j.Account == null || string.Equals(j.Account, account, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0)
            return;
        _pending.RemoveAll(mine.Contains);
        var now = _time.GetUtcNow();
        var restored = new List<SavedJob>();
        var lost = new List<SavedJob>();
        foreach (var saved in mine)
        {
            DeployOptions? options = null;
            if (saved.Deletion == null && (saved.Deployment == null || (options = Decrypt(saved)) == null))
            {
                lost.Add(saved);
                continue;
            }
            if (_queue.ActiveFor(saved.Subscription, saved.ResourceGroup, saved.AppName) != null)
            {
                _logger.LogWarning("同じ対象のジョブがあるため、前回の予定を戻しませんでした: {ResourceGroup} / {AppName}", saved.ResourceGroup, saved.AppName);
                continue;
            }
            // 予定の時刻を過ぎたもの（待機中だったものを含む）は、勝手に始めず、一時停止で戻す（D27・D28）
            var overdue = saved.ScheduledAt is not { } at || at <= now;
            _queue.Register(saved.Kind, saved.Target, saved.ScheduledAt, _starter(saved, options), saved: saved.Deletion, paused: saved.Paused || overdue,
                savedDeployment: saved.Deployment, registeredAt: saved.RegisteredAt, changedAt: saved.ChangedAt);
            restored.Add(saved with { Paused = saved.Paused || overdue });
        }
        _logger.LogInformation("前回の予定: {Restored} 件戻し（一時停止 {Paused} 件）、デプロイ {Lost} 件は戻せませんでした",
            restored.Count, restored.Count(j => j.Paused), lost.Count);
        if (restored.Count > 0)
        {
            var paused = restored.Count(j => j.Paused);
            _notifications?.Show(new AppNotice(
                "前回の予定を戻しました",
                $"{restored.Count} 件: {Join(restored)}。" +
                (paused > 0 ? $"予定の時刻を過ぎたものなど {paused} 件は一時停止で戻しました。ジョブの一覧で「再開」（実行）か「取り消し」を選んでください。" : "") +
                "始める前に計画を作り直し、確かめた計画と変わっていれば実行しません。",
                Page: Pages.Jobs));
        }
        if (lost.Count > 0)
            _notifications?.Show(new AppNotice(
                "前回のデプロイの予定は消えました",
                $"アプリが終了したため、デプロイの予定 {lost.Count} 件（{Join(lost)}）は消えました（予定を残さないと選んだか、保存したパスワードを復号できなかったため）。" +
                "もう一度登録してください。",
                Page: Pages.Wizard));
        OnPropertyChanged(nameof(PendingJobs));
        Save();
    }

    private static string Join(IEnumerable<SavedJob> jobs) => string.Join("、", jobs.Select(j => j.Text));

    // 保存したデプロイのパスワードを復号する（復号の仕組みが無い・復号できない場合は null。別の PC・Windows ユーザーで保存したものなど）
    private DeployOptions? Decrypt(SavedJob saved)
    {
        if (_secrets == null)
            return null;
        try
        {
            return saved.Deployment!.Restore(_secrets);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "保存したデプロイの予定を復号できませんでした: {ResourceGroup} / {AppName}", saved.ResourceGroup, saved.AppName);
            return null;
        }
    }

    // 今のまだ始めていないジョブ（削除は計画のあるもの。期限での削除は次の確認で登録し直すため残さない）と、まだ戻していない前回のものを保存する
    private void Save()
    {
        var account = _account();
        var current = _queue.Jobs
            .Where(j => j.State is DesktopJobState.Scheduled or DesktopJobState.Waiting or DesktopJobState.Paused && (j.IsDeployment || j.Saved != null))
            .OrderBy(j => j.Order)
            .Select(j => new SavedJob(j.IsDeployment ? JobKind.Deploy : JobKind.Delete, j.Target.Subscription, j.Target.ResourceGroup, j.Target.AppName,
                j.ScheduledAt, j.State == DesktopJobState.Paused, account, j.Saved, j.SavedDeployment, j.RegisteredAt, j.ChangedAt));
        _preferences.SavedJobs = [.. current, .. _pending];
    }
}
