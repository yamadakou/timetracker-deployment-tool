using System.Collections.ObjectModel;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>ジョブの一覧の絞り込み。</summary>
public enum JobFilterMode
{
    All,

    /// <summary>終わっていないもの（予定・待機中・一時停止・実行中を含む）。</summary>
    Active,

    /// <summary>予定（時刻を決めて、まだ始めていないもの）。</summary>
    Scheduled,

    Finished,
}

public sealed record JobFilterOption(JobFilterMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>ジョブの一覧の並べ替えの列（<see cref="Registered"/> は登録した順で、既定は新しい順）。</summary>
public enum JobSortKey
{
    Registered,
    Kind,
    Target,
    State,

    /// <summary>予定日時。</summary>
    Time,

    /// <summary>登録日時（docs/design/desktop-jobs.md の 6.8）。</summary>
    RegisteredAt,

    /// <summary>変更日時（予定の時刻を変えた日時）。</summary>
    ChangedAt,

    /// <summary>実行開始日時。</summary>
    StartedAt,

    /// <summary>実行終了日時。</summary>
    Finished,

    /// <summary>処理時間（実行開始から実行終了まで）。</summary>
    Duration,
}

/// <summary>
/// ジョブの一覧（docs/design/desktop-wizard.md の 7、No.63）。このアプリのすべてのジョブ（まだ Azure に無い新しいデプロイの予定を含む）を
/// 表に 1 行ずつ出し、見出しで並べ替え・絞り込み、選んだ行に取り消し・一時停止・再開・予定の変更・記録から削除をまとめて行う。詳細は実行状況の画面で見る。
/// 前回までのアプリで終わったジョブの記録も出す（docs/design/desktop-jobs.md の 6.8）。
/// </summary>
public sealed class JobListViewModel : ObservableObject
{
    public static readonly IReadOnlyList<JobFilterOption> Filters =
    [
        new(JobFilterMode.All, "すべて"),
        new(JobFilterMode.Active, "終わっていないもの"),
        new(JobFilterMode.Scheduled, "予定"),
        new(JobFilterMode.Finished, "終わったもの"),
    ];

    private readonly JobQueue _queue;
    private readonly IDialogService _dialogs;
    private JobFilterOption _filter = Filters[0];
    private string _targetFilter = "";
    private JobSortKey _sortKey = JobSortKey.Registered;
    private bool _descending = true;
    private IReadOnlyList<JobViewModel> _selected = [];
    private string? _message;

    public JobListViewModel(JobQueue queue, TimeProvider time, IDialogService dialogs)
    {
        _queue = queue;
        _dialogs = dialogs;
        Schedule = new ScheduleInput(time);
        CancelSelectedCommand = new AsyncCommand(() => Apply("取り消し", j => j.CancelCommand), () => _selected.Count > 0);
        PauseSelectedCommand = new AsyncCommand(() => Apply("一時停止", j => j.PauseCommand), () => _selected.Count > 0);
        ResumeSelectedCommand = new AsyncCommand(() => Apply("再開", j => j.ResumeCommand), () => _selected.Count > 0);
        RescheduleSelectedCommand = new AsyncCommand(RescheduleSelectedAsync, () => _selected.Count > 0);
        TableSelection = new SelectionSync<JobViewModel>(() => _selected, SetSelection);
        DeleteSelectedRecordsCommand = new AsyncCommand(() => DeleteRecordsAsync(_selected.ToList(), "選んだジョブのうち終わったもの"), () => _selected.Any(j => !j.IsActive));
        DeleteFinishedRecordsCommand = new AsyncCommand(() => DeleteRecordsAsync(_queue.Jobs.Where(j => !j.IsActive).ToList(), "終わったジョブすべて"),
            () => _queue.Jobs.Any(j => !j.IsActive));
        ClearSelectionCommand = new AsyncCommand(() =>
        {
            SetSelection([]);
            return Task.CompletedTask;
        }, () => _selected.Count > 0);
        foreach (var filter in new[] { ScheduledFilter, RegisteredFilter, ChangedFilter, StartedFilter, FinishedFilter })
            filter.Changed += Refresh;
        // ジョブの登録・消去と、状態の変更で並べ直す
        queue.Jobs.CollectionChanged += (_, _) => Refresh();
        queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.Summary))
                Refresh();
        };
    }

    /// <summary>絞り込んで並べたジョブ。</summary>
    public ObservableCollection<JobViewModel> Items { get; } = [];

    /// <summary>見出しの「状態」の絞り込み。</summary>
    public JobFilterOption Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
                Refresh();
        }
    }

    /// <summary>見出しの「対象」の絞り込み（リソースグループ / アプリ名の中間一致）。</summary>
    public string TargetFilter
    {
        get => _targetFilter;
        set
        {
            if (SetProperty(ref _targetFilter, value.Trim()))
                Refresh();
        }
    }

    /// <summary>件数の表示（「絞り込んだ件数 / 全件数」と、終わっていないジョブの内訳。No.75）。</summary>
    public string CountText => _queue.Jobs.Count == 0
        ? "ジョブはありません。"
        : (Items.Count == _queue.Jobs.Count ? $"全 {Items.Count} 件" : $"絞り込んだ {Items.Count} 件 / 全 {_queue.Jobs.Count} 件") +
          (_queue.Summary is { Length: > 0 } summary ? $"（終わっていないもの: {summary}）" : "");

    public string KindHeader => Header("種類", JobSortKey.Kind);

    public string TargetHeader => Header("リソースグループ / アプリ名", JobSortKey.Target);

    public string StateHeader => Header("状態", JobSortKey.State);

    public string TimeHeader => Header("予定日時", JobSortKey.Time);

    public string RegisteredHeader => Header("登録日時", JobSortKey.RegisteredAt);

    public string ChangedHeader => Header("変更日時", JobSortKey.ChangedAt);

    public string StartedHeader => Header("実行開始日時", JobSortKey.StartedAt);

    public string FinishedHeader => Header("実行終了日時", JobSortKey.Finished);

    public string DurationHeader => Header("処理時間", JobSortKey.Duration);

    /// <summary>見出しの「予定日時」の絞り込み。</summary>
    public DateRangeFilter ScheduledFilter { get; } = new();

    /// <summary>見出しの「登録日時」の絞り込み。</summary>
    public DateRangeFilter RegisteredFilter { get; } = new();

    /// <summary>見出しの「変更日時」の絞り込み。</summary>
    public DateRangeFilter ChangedFilter { get; } = new();

    /// <summary>見出しの「実行開始日時」の絞り込み。</summary>
    public DateRangeFilter StartedFilter { get; } = new();

    /// <summary>見出しの「実行終了日時」の絞り込み。</summary>
    public DateRangeFilter FinishedFilter { get; } = new();

    /// <summary>見出しを押したときに並べ替える。今と同じ列なら逆順、違う列なら昇順。</summary>
    public void SortBy(JobSortKey key)
    {
        _descending = _sortKey == key && !_descending;
        _sortKey = key;
        foreach (var name in new[] { nameof(KindHeader), nameof(TargetHeader), nameof(StateHeader), nameof(TimeHeader), nameof(RegisteredHeader), nameof(ChangedHeader), nameof(StartedHeader),
                     nameof(FinishedHeader), nameof(DurationHeader) })
            OnPropertyChanged(name);
        Refresh();
    }

    /// <summary>見出しの Tag（列の名前。<see cref="JobSortKey"/> の名前）で並べ替える（画面の側から）。</summary>
    public void SortBy(string column) => SortBy(Enum.Parse<JobSortKey>(column));

    /// <summary>表の選択と、この一覧の選択を合わせる。</summary>
    public SelectionSync<JobViewModel> TableSelection { get; }

    /// <summary>選んだ行（表の複数の選択）。</summary>
    public IReadOnlyList<JobViewModel> Selected => _selected;

    /// <summary>1 行だけ選んだときの行（「詳細」で開く）。</summary>
    public JobViewModel? Current => _selected.Count == 1 ? _selected[0] : null;

    public bool HasCurrent => Current != null;

    /// <summary>
    /// 見出しの「すべて」のチェックボックス（D32）。表の行をすべて選んでいれば true、1 つも選んでいなければ false、一部だけなら null。
    /// true にすると表の行をすべて選び、false にするとすべて外す。
    /// </summary>
    public bool? AllSelected
    {
        get => _selected.Count == 0 ? false : _selected.Count == Items.Count ? true : null;
        set
        {
            if (value is { } all)
                SetSelection(all ? Items : []);
        }
    }

    public string SelectionText => _selected.Count == 0 ? "行を選ぶと、まとめて操作できます。" : $"{_selected.Count} 件を選んでいます。";

    /// <summary>表で選んだ行を伝える（画面の側から）。</summary>
    public void SetSelection(IEnumerable<JobViewModel> jobs)
    {
        var selected = jobs.ToList();
        if (selected.SequenceEqual(_selected))
            return;
        _selected = selected;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AllSelected));
        foreach (var command in new[] { CancelSelectedCommand, PauseSelectedCommand, ResumeSelectedCommand, RescheduleSelectedCommand, ClearSelectionCommand, DeleteSelectedRecordsCommand })
            command.RaiseCanExecuteChanged();
        TableSelection.ViewModelChanged();
    }

    /// <summary>選択をすべて外す。</summary>
    public AsyncCommand ClearSelectionCommand { get; }

    public AsyncCommand CancelSelectedCommand { get; }

    /// <summary>選んだ行のうち、終わったジョブの記録を消す（「記録から削除」。確かめてから。Azure のリソースは消さない）。</summary>
    public AsyncCommand DeleteSelectedRecordsCommand { get; }

    /// <summary>終わったジョブの記録をすべて消す（「終わったジョブを記録から削除」。確かめてから）。</summary>
    public AsyncCommand DeleteFinishedRecordsCommand { get; }

    public AsyncCommand PauseSelectedCommand { get; }

    public AsyncCommand ResumeSelectedCommand { get; }

    /// <summary>選んだ行の予定を <see cref="Schedule"/> の日時に変える（全部に同じ時刻）。</summary>
    public AsyncCommand RescheduleSelectedCommand { get; }

    /// <summary>「予定を変える」の日時。</summary>
    public ScheduleInput Schedule { get; }

    /// <summary>まとめての操作の結果。</summary>
    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    private string Header(string label, JobSortKey key) => _sortKey != key ? label : label + (_descending ? " ▼" : " ▲");

    // 選んだ行のうち、その操作ができるものにだけ行い、できなかった数を出す
    private async Task Apply(string name, Func<JobViewModel, AsyncCommand> command)
    {
        var done = 0;
        var selected = _selected.ToList();
        foreach (var job in selected)
        {
            var c = command(job);
            if (!c.CanExecute(null))
                continue;
            await c.ExecuteAsync();
            done++;
        }
        Message = Result(name, done, selected.Count);
    }

    private async Task RescheduleSelectedAsync()
    {
        if (Schedule.Error is { } error)
        {
            Message = error;
            return;
        }
        foreach (var job in _selected)
        {
            job.EditDate = Schedule.Date;
            job.EditTime = Schedule.TimeOfDay;
        }
        await Apply($"予定の変更（{Schedule.At.ToLocalTime():MM/dd HH:mm}）", j => j.RescheduleCommand);
    }

    // 終わったものの記録だけを消し、終わっていないために残したものの数を出す
    private async Task DeleteRecordsAsync(IReadOnlyList<JobViewModel> jobs, string what)
    {
        var finished = jobs.Where(j => !j.IsActive).ToList();
        if (!await _dialogs.ConfirmAsync("ジョブの記録を削除しますか？",
                $"{what}の記録 {finished.Count} 件を、ジョブの一覧とこの PC から削除します。元に戻せません（Azure のリソースは削除しません）。", "記録から削除"))
            return;
        var removed = _queue.RemoveFinished(finished);
        Message = removed == jobs.Count
            ? $"{removed} 件の記録を削除しました。"
            : $"{removed} 件の記録を削除しました（{jobs.Count - removed} 件は、終わっていないため、そのままです）。";
    }

    private static string Result(string name, int done, int total) =>
        done == total ? $"{done} 件の{name}をしました。" : $"{done} 件の{name}をしました（{total - done} 件は、今の状態ではできないため、そのままです）。";

    private bool Matches(JobViewModel job) =>
        (_targetFilter.Length == 0 || job.TargetText.Contains(_targetFilter, StringComparison.OrdinalIgnoreCase))
        && ScheduledFilter.Matches(job.ScheduledAt) && RegisteredFilter.Matches(job.RegisteredAt) && ChangedFilter.Matches(job.ChangedAt)
        && StartedFilter.Matches(job.StartedAt) && FinishedFilter.Matches(job.FinishedAt)
        && Filter.Mode switch
        {
            JobFilterMode.Active => job.IsActive,
            JobFilterMode.Scheduled => job.State == DesktopJobState.Scheduled,
            JobFilterMode.Finished => !job.IsActive,
            _ => true,
        };

    // 並べ替え（同じ値なら新しい順。値の無いもの〔予定の無いもの・まだ始めていないものなど〕は最後）
    private IEnumerable<JobViewModel> Sorted(IEnumerable<JobViewModel> jobs)
    {
        if (_sortKey == JobSortKey.Registered)
            return _descending ? jobs.OrderByDescending(j => j.Order) : jobs.OrderBy(j => j.Order);
        var keyed = jobs.Select(j => (Job: j, Key: _sortKey switch
        {
            JobSortKey.Kind => (IComparable?)j.KindText,
            JobSortKey.Target => j.TargetText,
            JobSortKey.State => j.State,
            JobSortKey.RegisteredAt => j.RegisteredAt,
            JobSortKey.ChangedAt => j.ChangedAt,
            JobSortKey.StartedAt => j.StartedAt,
            JobSortKey.Finished => j.FinishedAt,
            JobSortKey.Duration => j.Duration,
            _ => j.ScheduledAt,
        })).ToList();
        var withValue = keyed.Where(x => x.Key != null);
        var ordered = _descending ? withValue.OrderByDescending(x => x.Key) : withValue.OrderBy(x => x.Key);
        return ordered.ThenByDescending(x => x.Job.Order).Concat(keyed.Where(x => x.Key == null).OrderByDescending(x => x.Job.Order)).Select(x => x.Job);
    }

    private void Refresh()
    {
        var matched = Sorted(_queue.Jobs.Where(Matches)).ToList();
        if (!matched.SequenceEqual(Items))
        {
            // 並べ直しても、残っている行の選択は保つ（行を消す間に画面の表が選択を外すため、先に覚えておく）
            var selected = _selected.ToList();
            Items.Clear();
            foreach (var job in matched)
                Items.Add(job);
            SetSelection(selected.Where(matched.Contains));
            OnPropertyChanged(nameof(AllSelected));
        }
        OnPropertyChanged(nameof(CountText));
        DeleteSelectedRecordsCommand.RaiseCanExecuteChanged();
        DeleteFinishedRecordsCommand.RaiseCanExecuteChanged();
    }
}
