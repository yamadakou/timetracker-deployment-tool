using System.Collections.ObjectModel;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core;
using TTDeployTool.Controller.Desktop.Core.Mvvm;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>画面に出すジョブの状態（詳細設計 desktop-jobs.md の 4.1）。</summary>
public enum DesktopJobState
{
    /// <summary>指定の時刻まで待つ。</summary>
    Scheduled,

    /// <summary>同時実行の上限に達しているため、順番を待つ。</summary>
    Waiting,

    /// <summary>利用者が止めた（予定・待機中から）。再開するまで実行しない。</summary>
    Paused,

    /// <summary>実行の直前の確認（計画し直して比べる）をしている。</summary>
    Starting,

    Running,

    /// <summary>停止を受け付け、今の手順が終わるのを待っている。</summary>
    Stopping,

    Succeeded,
    Failed,
    Canceled,
    Stopped,
}

/// <summary>
/// 登録したジョブ（デプロイ・削除）の状態・進み具合・ログ・結果と、取り消し・一時停止・再開・予定の変更・停止の操作（詳細設計 desktop-jobs.md の 4）。
/// 始めるまでは <see cref="JobQueue"/> が状態を持ち、始めた後は Application のジョブ（<see cref="Job"/>）の状態を表示する。
/// 実行中のジョブは、今の手順の後で止められる（Azure の操作を途中で止めると中途半端な状態が残るため、今の手順は止めない）。
/// </summary>
public sealed class JobViewModel : ObservableObject
{
    /// <summary>状態を読み直す間隔（ジョブが終われば待たずに読む）。</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly JobKind _kind;
    private readonly JobTarget _target;
    private readonly JobQueue? _queue;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Job? _job;
    private int _logOffset;
    private JobSnapshot? _snapshot;
    private DesktopJobState _pending;
    private DateTimeOffset? _scheduledAt;
    private string? _startError;
    private string? _note;
    private string? _copyMessage;
    private DateTimeOffset _editDate;
    private TimeSpan _editTime;

    /// <summary>始めたジョブを表示する。</summary>
    public JobViewModel(Job job)
        : this(job.Kind, job.Target, null, null, null)
    {
        Attach(job);
    }

    internal JobViewModel(JobKind kind, JobTarget target, DateTimeOffset? scheduledAt, JobQueue? queue, Func<Task<Job>>? starter)
    {
        _kind = kind;
        _target = target;
        _queue = queue;
        _scheduledAt = scheduledAt;
        _pending = scheduledAt == null ? DesktopJobState.Waiting : DesktopJobState.Scheduled;
        Starter = starter;
        var edit = (scheduledAt ?? DateTimeOffset.Now).ToLocalTime();
        _editDate = edit.Date;
        _editTime = new TimeSpan(edit.Hour, edit.Minute, 0);
        CancelCommand = new AsyncCommand(() => Run(q => q.Cancel(this)), () => _queue != null && State is DesktopJobState.Scheduled or DesktopJobState.Waiting or DesktopJobState.Paused);
        PauseCommand = new AsyncCommand(() => Run(q => q.Pause(this)), () => _queue != null && State is DesktopJobState.Scheduled or DesktopJobState.Waiting);
        ResumeCommand = new AsyncCommand(() => Run(q => q.Resume(this)), () => _queue != null && State == DesktopJobState.Paused);
        RescheduleCommand = new AsyncCommand(() => Run(q => q.Reschedule(this, EditScheduledAt)), () => _queue != null && State is DesktopJobState.Scheduled or DesktopJobState.Paused);
        StopCommand = new AsyncCommand(() => Run(q => q.Stop(this)), () => _queue != null && State == DesktopJobState.Running);
        CopyCommand = new AsyncCommand(() =>
        {
            _queue!.Clipboard!.CopyText(ToMarkdown());
            CopyMessage = "ジョブの内容を Markdown でコピーしました。";
            return Task.CompletedTask;
        }, () => _queue?.Clipboard != null);
    }

    /// <summary>ジョブの内容を Markdown でコピーする。</summary>
    public AsyncCommand CopyCommand { get; }

    /// <summary>コピーの結果。</summary>
    public string? CopyMessage
    {
        get => _copyMessage;
        private set => SetProperty(ref _copyMessage, value);
    }

    /// <summary>ジョブの内容（題名・状態・手順・結果・エラー・注意・ログ）の Markdown。</summary>
    public string ToMarkdown() => MarkdownText.Document($"TTDeployTool のジョブ: {Title}",
        (null, MarkdownText.Bullets([$"状態: {StatusText}", StepText, Endpoint?.ToString(), PersistenceSummary, ExpirySummary, DeletionSummary])),
        ("エラー", Error),
        ("注意", Note),
        ("ログ", Logs.Count == 0 ? null : "```\n" + string.Join("\n", Logs) + "\n```"));

    /// <summary>ジョブを始める処理（実行の直前の確認を含む）。</summary>
    internal Func<Task<Job>>? Starter { get; }

    /// <summary>登録した順（同じ状態なら、先に登録したものから始める）。</summary>
    internal long Order { get; init; }

    /// <summary>このアプリの中でジョブを指す ID（通知から開くときに使う）。</summary>
    public string Id => $"job-{Order}";

    /// <summary>終わったとき（完了・失敗・取り消し・停止）に完了する。</summary>
    internal Task Finished => _finished.Task;

    public AsyncCommand CancelCommand { get; }

    public AsyncCommand PauseCommand { get; }

    public AsyncCommand ResumeCommand { get; }

    public AsyncCommand RescheduleCommand { get; }

    /// <summary>今の手順の後で止める。</summary>
    public AsyncCommand StopCommand { get; }

    public string Title => _kind == JobKind.Deploy
        ? $"デプロイ（{_target.ResourceGroup} / {_target.AppName}）"
        : $"{(IsExpiryDeletion ? "期限での削除" : "削除")}（{_target.ResourceGroup} / {_target.AppName}）";

    /// <summary>利用可能期限を過ぎたための、確認なしの削除か（docs/design/desktop-jobs.md の 6.5）。</summary>
    public bool IsExpiryDeletion { get; init; }

    public ObservableCollection<string> Logs { get; } = [];

    public DesktopJobState State => _snapshot == null
        ? _pending
        : _snapshot.Status == JobStatus.Running && _snapshot.StopRequested ? DesktopJobState.Stopping : FromJob[_snapshot.Status];

    // 始めたジョブの状態（停止の要求は State で扱う）
    private static readonly IReadOnlyDictionary<JobStatus, DesktopJobState> FromJob = new Dictionary<JobStatus, DesktopJobState>
    {
        [JobStatus.Running] = DesktopJobState.Running,
        [JobStatus.Succeeded] = DesktopJobState.Succeeded,
        [JobStatus.Failed] = DesktopJobState.Failed,
        [JobStatus.Stopped] = DesktopJobState.Stopped,
    };

    // 予定（時刻を添える）以外の状態の表示
    private static readonly IReadOnlyDictionary<DesktopJobState, string> Labels = new Dictionary<DesktopJobState, string>
    {
        [DesktopJobState.Waiting] = "待機中（同時に実行できる数に達しています）",
        [DesktopJobState.Paused] = "一時停止",
        [DesktopJobState.Starting] = "実行の準備中",
        [DesktopJobState.Running] = "実行中",
        [DesktopJobState.Stopping] = "停止中（今の手順が終わったら止めます）",
        [DesktopJobState.Succeeded] = "完了",
        [DesktopJobState.Failed] = "失敗",
        [DesktopJobState.Canceled] = "取り消し",
        [DesktopJobState.Stopped] = "停止",
    };

    /// <summary>Azure を操作しているか（実行の直前の確認・停止を待っている間を含む）。同時実行の数に数える。</summary>
    public bool IsRunning => State is DesktopJobState.Starting or DesktopJobState.Running or DesktopJobState.Stopping;

    /// <summary>まだ終わっていないか（予定・待機中・一時停止を含む）。同じ対象の計画と登録を止める。</summary>
    public bool IsActive => State is not (DesktopJobState.Succeeded or DesktopJobState.Failed or DesktopJobState.Canceled or DesktopJobState.Stopped);

    /// <summary>予定の時刻（すぐ実行するジョブは null）。</summary>
    public DateTimeOffset? ScheduledAt => _scheduledAt;

    public string StatusText => State == DesktopJobState.Scheduled
        ? $"予定（{_scheduledAt!.Value.ToLocalTime():MM/dd HH:mm}）"
        : Labels[State];

    public string? StepText => State == DesktopJobState.Starting
        ? "実行の直前の確認をしています（Azure を読み直して、計画と比べています）"
        : _snapshot?.CurrentStep is { } step ? $"{step.Index} / {step.Total}: {step.Name}" : null;

    /// <summary>失敗・停止した場合のメッセージと、次の手順。</summary>
    public string? Error => _startError is { } startError
        ? $"実行しませんでした: {startError}"
        : _snapshot?.Error is { } error
            ? $"{error}\n途中まで作成・削除したリソースは、もう一度実行すると続きから処理します（状態は「状態の確認」で見られます）。"
            : null;

    /// <summary>予定より遅れて実行した場合などの注意。</summary>
    public string? Note
    {
        get => _note;
        internal set => SetProperty(ref _note, value);
    }

    /// <summary>デプロイが成功した場合の Timetracker の URL。</summary>
    public Uri? Endpoint => _snapshot?.Result is DeploymentResult { TimetrackerUrl: { } url } ? new Uri(url) : null;

    public bool HasEndpoint => Endpoint != null;

    /// <summary>デプロイのジョブか（false なら削除）。</summary>
    public bool IsDeployment => _kind == JobKind.Deploy;

    /// <summary>ジョブの対象。</summary>
    public JobTarget Target => _target;

    /// <summary>ジョブの一覧の種類（docs/design/desktop-wizard.md の 7）。</summary>
    public string KindText => _kind == JobKind.Deploy ? "デプロイ" : IsExpiryDeletion ? "期限での削除" : "削除";

    /// <summary>ジョブの一覧の対象。</summary>
    public string TargetText => $"{_target.ResourceGroup} / {_target.AppName}";

    /// <summary>ジョブの一覧の時刻（予定の時刻。すぐ実行したものは「すぐ実行」）。</summary>
    public string TimeText => _scheduledAt is { } at ? $"{at.ToLocalTime():MM/dd HH:mm}" : "すぐ実行";

    /// <summary>ジョブの一覧の結果（エラー・削除したもの・URL。まだ無ければ null）。</summary>
    public string? ResultText => Error ?? DeletionSummary ?? Endpoint?.ToString();

    /// <summary>成功したジョブの結果（デプロイは <see cref="DeploymentResult"/>、削除は <see cref="DeletionOutcome"/>。終わっていなければ null）。</summary>
    public object? Result => _snapshot?.Result;

    /// <summary>予定の変更の日付（画面の入力）。</summary>
    public DateTimeOffset EditDate
    {
        get => _editDate;
        set => SetProperty(ref _editDate, value);
    }

    /// <summary>予定の変更の時刻（画面の入力）。</summary>
    public TimeSpan EditTime
    {
        get => _editTime;
        set => SetProperty(ref _editTime, value);
    }

    internal DateTimeOffset EditScheduledAt => ScheduleTime.Combine(EditDate, EditTime);

    /// <summary>このジョブの対象か。</summary>
    public bool IsFor(string subscription, string resourceGroup, string appName) =>
        string.Equals(Key(_target.Subscription, _target.ResourceGroup, _target.AppName), Key(subscription, resourceGroup, appName), StringComparison.OrdinalIgnoreCase);

    /// <summary>ほかのジョブと同じ対象か。</summary>
    public bool IsSameTarget(JobViewModel other) => IsFor(other._target.Subscription, other._target.ResourceGroup, other._target.AppName);

    // Application のジョブの管理と同じく、大文字と小文字を区別しない
    private static string Key(string subscription, string resourceGroup, string appName) => $"{subscription}/{resourceGroup}/{appName}";

    /// <summary>デプロイが成功し、DB のデータを永続化した場合の保存先。</summary>
    public string? PersistenceSummary => _snapshot?.Result is DeploymentResult { StorageAccountName: { } account }
        ? $"DB のデータはストレージアカウント {account}（Azure Files）に保存しています。"
        : null;

    /// <summary>デプロイが成功した場合の、記録した利用可能期限。</summary>
    public string? ExpirySummary => _snapshot?.Result is DeploymentResult { Expiry: { } expiry }
        ? $"利用可能期限: {expiry.Describe(TimeZoneInfo.Local)}"
        : null;

    /// <summary>削除が成功した場合の結果。</summary>
    public string? DeletionSummary => _snapshot?.Result is DeletionOutcome d
        ? $"削除したもの: {Join(d.Deleted)}。既に無かったもの: {Join(d.AlreadyDeleted)}。" +
          (d.ResourceGroupDeleted ? $"リソースグループ {d.ResourceGroup} も削除しました。" : $"リソースグループ {d.ResourceGroup} は残しています。")
        : null;

    /// <summary>ジョブが終わるまで状態を読み直す（呼んだスレッドで画面の項目を更新する）。始めたジョブだけで使う。</summary>
    public async Task WatchAsync(TimeProvider time, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        while (_snapshot!.Status == JobStatus.Running)
        {
            await Task.WhenAny(_job!.Completion, Task.Delay(interval, time, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            Refresh();
        }
    }

    /// <summary>始める前の状態を変える（JobQueue だけが呼ぶ）。</summary>
    internal void SetPending(DesktopJobState state, DateTimeOffset? scheduledAt)
    {
        _pending = state;
        _scheduledAt = scheduledAt;
        Changed();
    }

    internal void Attach(Job job)
    {
        _job = job;
        Refresh();
    }

    internal void FailToStart(string message)
    {
        _startError = message;
        SetPending(DesktopJobState.Failed, _scheduledAt);
    }

    internal bool RequestStop()
    {
        var accepted = _job!.RequestStop();
        Refresh();
        return accepted;
    }

    private Task Run(Action<JobQueue> action)
    {
        action(_queue!);
        return Task.CompletedTask;
    }

    // 前回の続きのログだけを足す
    private void Refresh()
    {
        _snapshot = _job!.Snapshot(_logOffset);
        foreach (var log in _snapshot.Logs)
            Logs.Add($"{log.At.ToLocalTime():HH:mm:ss} {log.Message}");
        _logOffset = _snapshot.NextLogOffset;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ScheduledAt));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(HasEndpoint));
        OnPropertyChanged(nameof(PersistenceSummary));
        OnPropertyChanged(nameof(ExpirySummary));
        OnPropertyChanged(nameof(DeletionSummary));
        CancelCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        RescheduleCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        if (!IsActive)
            _finished.TrySetResult();
    }

    private static string Join(IReadOnlyList<string> names) => names.Count == 0 ? "なし" : string.Join(", ", names);
}
