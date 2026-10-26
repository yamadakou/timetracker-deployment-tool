using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Core.Jobs;

/// <summary>
/// アプリを終了しても予定を残す（docs/design/desktop-jobs.md の 6.6、D23・D24）。まだ始めていない（予定・待機中・一時停止の）ジョブを、
/// 変わるたびにアプリのデータに保存する。削除は次にサインインしたときにジョブとして登録し直し、デプロイ（パスワードを持つため中身は保存しない）は
/// 消えたことを通知する。違うアカウントで保存したものは、そのアカウントでサインインするまで残す。
/// </summary>
public sealed class ScheduledJobStore : ObservableObject
{
    private readonly JobQueue _queue;
    private readonly IAppPreferences _preferences;
    private readonly Func<string?> _account;
    private readonly Func<SavedJob, Func<Task<Job>>> _starter;
    private readonly INotificationService? _notifications;
    private readonly ILogger _logger;
    private readonly List<SavedJob> _pending;

    /// <param name="account">今サインインしているアカウントの名前（していなければ null）</param>
    /// <param name="starter">保存した削除の予定を始める処理（計画を作り直して確かめる）</param>
    public ScheduledJobStore(
        JobQueue queue, IAppPreferences preferences, Func<string?> account, Func<SavedJob, Func<Task<Job>>> starter, INotificationService? notifications, ILogger logger)
    {
        _queue = queue;
        _preferences = preferences;
        _account = account;
        _starter = starter;
        _notifications = notifications;
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

    /// <summary>前回のアプリで保存し、まだ戻していない削除の予定の数（違うアカウントのものを含む）。</summary>
    public int PendingDeletions => _pending.Count(j => j.Deletion != null);

    /// <summary>サインインしたとき。同じアカウント（か、アカウントの無い記録）の削除の予定を戻し、デプロイの予定が消えたことを通知する。</summary>
    public void Restore()
    {
        var account = _account();
        var mine = _pending.Where(j => j.Account == null || string.Equals(j.Account, account, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0)
            return;
        _pending.RemoveAll(mine.Contains);
        var restored = new List<SavedJob>();
        foreach (var saved in mine.Where(j => j.Deletion != null))
        {
            if (_queue.ActiveFor(saved.Subscription, saved.ResourceGroup, saved.AppName) != null)
            {
                _logger.LogWarning("同じ対象のジョブがあるため、前回の削除の予定を戻しませんでした: {ResourceGroup} / {AppName}", saved.ResourceGroup, saved.AppName);
                continue;
            }
            _queue.Register(JobKind.Delete, saved.Target, saved.ScheduledAt, _starter(saved), saved: saved.Deletion, paused: saved.Paused);
            restored.Add(saved);
        }
        var lost = mine.Where(j => j.Deletion == null).ToList();
        _logger.LogInformation("前回の予定: 削除を {Restored} 件戻し、デプロイ {Lost} 件は消えていました", restored.Count, lost.Count);
        if (restored.Count > 0)
            _notifications?.Show(new AppNotice(
                "前回の削除の予定を戻しました",
                $"{restored.Count} 件: {Join(restored)}。予定の時刻を過ぎたものは、すぐ実行します。始める前に計画を作り直し、確かめた計画と変わっていれば削除しません。",
                Page: Pages.Jobs));
        if (lost.Count > 0)
            _notifications?.Show(new AppNotice(
                "前回のデプロイの予定は消えました",
                $"アプリが終了したため、デプロイの予定 {lost.Count} 件（{Join(lost)}）は消えました（パスワードを保存しないため）。もう一度登録してください。",
                Page: Pages.Wizard));
        OnPropertyChanged(nameof(PendingDeletions));
        Save();
    }

    private static string Join(IEnumerable<SavedJob> jobs) => string.Join("、", jobs.Select(j => j.Text));

    // 今のまだ始めていないジョブ（削除は計画のあるもの。期限での削除は次の確認で登録し直すため残さない）と、まだ戻していない前回のものを保存する
    private void Save()
    {
        var account = _account();
        var current = _queue.Jobs
            .Where(j => j.State is DesktopJobState.Scheduled or DesktopJobState.Waiting or DesktopJobState.Paused && (j.IsDeployment || j.Saved != null))
            .OrderBy(j => j.Order)
            .Select(j => new SavedJob(j.IsDeployment ? JobKind.Deploy : JobKind.Delete, j.Target.Subscription, j.Target.ResourceGroup, j.Target.AppName,
                j.ScheduledAt, j.State == DesktopJobState.Paused, account, j.Saved));
        _preferences.SavedJobs = [.. current, .. _pending];
    }
}
