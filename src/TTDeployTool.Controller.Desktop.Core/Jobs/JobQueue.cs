using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Core.Jobs;

/// <summary>
/// 登録したジョブ（デプロイ・削除）の順番待ち・予定・同時実行の上限（詳細設計 desktop-jobs.md の 4）。
/// 対象（サブスクリプション・リソースグループ・アプリ名）が違うジョブは、上限まで同時に実行する。同じ対象のジョブは登録させない（D2）。
/// 予定の時刻は、一定の間隔で確かめる。予定の時刻を過ぎていたら、動けるようになったときにすぐ実行する（D14）。
/// 状態の変更は、作った SynchronizationContext（画面のスレッド）で行う（ロックは、画面のスレッドが無いテストのため）。
/// </summary>
public sealed class JobQueue : ObservableObject, IDisposable
{
    public const int DefaultConcurrency = 2;

    /// <summary>同時に実行する数の選択肢。</summary>
    public static readonly IReadOnlyList<int> ConcurrencyChoices = [1, 2, 3, 4];

    /// <summary>予定より、この時間以上遅れて始めたら、その旨を残す。</summary>
    public static readonly TimeSpan LateThreshold = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private readonly IAppPreferences _preferences;
    private readonly ILogger _logger;
    private readonly SynchronizationContext? _context;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();

    // 変更の通知（ジョブの状態・一覧の増減）を 1 つずつ流すためのロック。受け手が _lock を取る（Summary など）ため、_lock より先に取り、
    // _lock を持ったまま取らない（状態は _lock の中で変え、通知は _lock の外で流す）。画面のアプリでは、どれも画面のスレッドで動くため重ならない
    private readonly object _notifyGate = new();
    private long _order;

    /// <param name="scheduleCheckInterval">予定の時刻を確かめる間隔</param>
    public JobQueue(TimeProvider time, TimeSpan pollInterval, TimeSpan scheduleCheckInterval, IAppPreferences preferences, ILogger logger)
    {
        _time = time;
        _pollInterval = pollInterval;
        _preferences = preferences;
        _logger = logger;
        _context = SynchronizationContext.Current;
        _timer = time.CreateTimer(_ => Post(Pump), null, scheduleCheckInterval, scheduleCheckInterval);
    }

    /// <summary>ジョブの内容のコピー先（無ければ、ジョブの「コピー」は押せない）。</summary>
    public IClipboardService? Clipboard { get; init; }

    /// <summary>ジョブが終わったとき（完了・失敗・停止。取り消しは含めない）。画面のスレッドで呼ぶ。</summary>
    public event Action<JobViewModel>? Finished;

    /// <summary>登録したジョブ（新しい順）。</summary>
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    /// <summary>同時に実行する数（1〜4。既定 2）。アプリのデータに保存する。</summary>
    public int MaxConcurrency
    {
        get => Math.Clamp(_preferences.MaxConcurrentJobs ?? DefaultConcurrency, ConcurrencyChoices[0], ConcurrencyChoices[^1]);
        set
        {
            var clamped = Math.Clamp(value, ConcurrencyChoices[0], ConcurrencyChoices[^1]);
            if (_preferences.MaxConcurrentJobs == clamped)
                return;
            _preferences.MaxConcurrentJobs = clamped;
            OnPropertyChanged(nameof(MaxConcurrency));
            Pump();
        }
    }

    /// <summary>終わっていないジョブがあるか。</summary>
    public bool HasActive => Snapshot().Any(j => j.IsActive);

    /// <summary>どの画面にも出す状況（例: 「実行中 1・待機中 1・予定 2」）。終わっていないジョブが無ければ空。</summary>
    public string Summary
    {
        get
        {
            var jobs = Snapshot();
            var parts = new (string Label, int Count)[]
            {
                ("実行中", jobs.Count(j => j.IsRunning)),
                ("待機中", jobs.Count(j => j.State == DesktopJobState.Waiting)),
                ("予定", jobs.Count(j => j.State == DesktopJobState.Scheduled)),
                ("一時停止", jobs.Count(j => j.State == DesktopJobState.Paused)),
            };
            return string.Join("・", parts.Where(p => p.Count > 0).Select(p => $"{p.Label} {p.Count}"));
        }
    }

    /// <summary>対象の、終わっていないジョブ（無ければ null）。</summary>
    public JobViewModel? ActiveFor(string subscription, string resourceGroup, string appName) =>
        Snapshot().FirstOrDefault(j => j.IsActive && j.IsFor(subscription, resourceGroup, appName));

    /// <summary>ジョブを登録する。予定の時刻が無ければ、上限に空きがあればすぐ始め、無ければ待機中にする。</summary>
    /// <param name="starter">ジョブを始める処理（実行の直前の確認を含む。失敗すると、ジョブは始めずに失敗にする）</param>
    /// <exception cref="InvalidOperationException">同じ対象の、終わっていないジョブがある</exception>
    /// <param name="expiryDeletion">利用可能期限を過ぎたための削除か（題名に出す）</param>
    /// <param name="saved">アプリを終了しても残す削除の計画（docs/design/desktop-jobs.md の 6.6。無ければ残さない）</param>
    /// <param name="paused">一時停止にして登録する（前回の一時停止を戻すとき）</param>
    public JobViewModel Register(
        JobKind kind, JobTarget target, DateTimeOffset? scheduledAt, Func<Task<Job>> starter, bool expiryDeletion = false, SavedDeletion? saved = null, bool paused = false)
    {
        JobViewModel job;
        lock (_notifyGate)
        lock (_lock)
        {
            if (ActiveFor(target.Subscription, target.ResourceGroup, target.AppName) is { } active)
                throw new InvalidOperationException(ActiveMessage(active));
            job = new JobViewModel(kind, target, scheduledAt, this, starter) { Order = ++_order, IsExpiryDeletion = expiryDeletion, Saved = saved };
            if (paused)
                job.SetPending(DesktopJobState.Paused, scheduledAt, notify: false);
            // 終わったことを、ジョブの Finished が完了する前に知らせる（状態の変更の通知は、Finished の完了より先に呼ばれる）
            var notified = false;
            job.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(JobViewModel.State))
                    return;
                Changed();
                if (!notified && !job.IsActive && job.State != DesktopJobState.Canceled)
                {
                    notified = true;
                    Finished?.Invoke(job);
                }
            };
            Jobs.Insert(0, job);
        }
        Changed();
        Pump();
        // 始めたかどうかが分かるよう、順番に入れた後の状態を書く
        _logger.LogInformation("ジョブを登録しました: {Title}（{State}）", job.Title, job.StatusText);
        return job;
    }

    /// <summary>同じ対象のジョブがあるため、計画・登録できない理由。</summary>
    public static string ActiveMessage(JobViewModel active) =>
        $"この対象のジョブ（{active.Title}: {active.StatusText}）があるため、計画・実行できません。ジョブが終わるか、取り消してから計画してください。";

    /// <summary>終わったジョブを一覧から消す。</summary>
    public void ClearFinished()
    {
        lock (_notifyGate)
        lock (_lock)
        {
            foreach (var job in Jobs.Where(j => !j.IsActive).ToList())
                Jobs.Remove(job);
        }
        Changed();
    }

    /// <summary>
    /// テスト用: ジョブがすべて終わるまで待つ（予定・一時停止のものがあれば、それも待つ）。終わった直後のジョブは、状態が変わってから
    /// 終わったことの通知（<see cref="Finished"/>）までの間があるため、終わったジョブも、その通知の後に完了する Finished を待つ。
    /// </summary>
    public Task WhenIdleAsync() => Task.WhenAll(Snapshot().Select(j => j.Finished));

    internal void Cancel(JobViewModel job) => Change(job, DesktopJobState.Canceled, job.ScheduledAt, "取り消しました");

    internal void Pause(JobViewModel job) => Change(job, DesktopJobState.Paused, job.ScheduledAt, "一時停止しました");

    // 予定の時刻を過ぎていれば、待機中に戻す（すぐ順番に入る）
    internal void Resume(JobViewModel job) =>
        Change(job, job.ScheduledAt is { } at && at > _time.GetUtcNow() ? DesktopJobState.Scheduled : DesktopJobState.Waiting, job.ScheduledAt, "再開しました");

    // 一時停止中なら一時停止のまま、時刻だけを変える
    internal void Reschedule(JobViewModel job, DateTimeOffset scheduledAt) =>
        Change(job, job.State == DesktopJobState.Paused ? DesktopJobState.Paused : DesktopJobState.Scheduled, scheduledAt,
            $"予定を {scheduledAt.ToLocalTime():MM/dd HH:mm} に変えました");

    internal void Stop(JobViewModel job)
    {
        if (job.RequestStop())
            _logger.LogInformation("停止を受け付けました: {Title}", job.Title);
    }

    /// <summary>始められるジョブ（待機中と、予定の時刻を過ぎたもの）を、登録の順に、上限まで始める。</summary>
    public void Pump()
    {
        List<JobViewModel> next;
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var slots = MaxConcurrency - Jobs.Count(j => j.IsRunning);
            next = Jobs
                .Where(j => j.State == DesktopJobState.Waiting || (j.State == DesktopJobState.Scheduled && j.ScheduledAt!.Value <= now))
                .OrderBy(j => j.Order)
                .Take(Math.Max(slots, 0))
                .ToList();
            foreach (var job in next)
                job.SetPending(DesktopJobState.Starting, job.ScheduledAt, notify: false);
        }
        foreach (var job in next)
        {
            job.NotifyChanged();
            _ = RunAsync(job, now);
        }
    }

    public void Dispose() => _timer.Dispose();

    private async Task RunAsync(JobViewModel job, DateTimeOffset now)
    {
        if (job.ScheduledAt is { } at && now - at >= LateThreshold)
        {
            job.Note = $"予定（{at.ToLocalTime():MM/dd HH:mm}）より {(int)(now - at).TotalMinutes} 分遅れて実行しました（PC のスリープ・アプリの終了・同時に実行できる数に達していたなど）。";
            _logger.LogWarning("予定より遅れて始めます: {Title}（予定 {ScheduledAt:o}）", job.Title, at);
        }
        Job started;
        try
        {
            started = await job.Starter!();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ジョブを始められませんでした: {Title}", job.Title);
            job.FailToStart(DesktopMessages.ForAzure(ex));
            Pump();
            return;
        }
        job.Attach(started);
        await job.WatchAsync(_time, _pollInterval);
        _logger.LogInformation("ジョブが終わりました: {Title}（{State}）", job.Title, job.StatusText);
        Pump();
    }

    private void Change(JobViewModel job, DesktopJobState state, DateTimeOffset? scheduledAt, string what)
    {
        lock (_lock)
            job.SetPending(state, scheduledAt, notify: false);
        job.NotifyChanged();
        _logger.LogInformation("{What}: {Title}", what, job.Title);
        Pump();
    }

    private void Changed() => Notify(() =>
    {
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(Summary));
    });

    /// <summary>変更の通知を、ほかの通知と重ならないように流す（<see cref="_notifyGate"/>）。</summary>
    internal void Notify(Action raise)
    {
        lock (_notifyGate)
            raise();
    }

    private List<JobViewModel> Snapshot()
    {
        lock (_lock)
            return Jobs.ToList();
    }

    private void Post(Action action)
    {
        if (_context == null)
            action();
        else
            _context.Post(_ => action(), null);
    }
}
