using System.Collections.ObjectModel;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;

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
    Time,
}

/// <summary>
/// ジョブの一覧（docs/design/desktop-wizard.md の 7、No.63）。このアプリのすべてのジョブ（まだ Azure に無い新しいデプロイの予定を含む）を
/// 表に 1 行ずつ出し、見出しで並べ替え・絞り込み、選んだ行に取り消し・一時停止・再開・予定の変更をまとめて行う。詳細は実行状況の画面で見る。
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
    private JobFilterOption _filter = Filters[0];
    private string _targetFilter = "";
    private JobSortKey _sortKey = JobSortKey.Registered;
    private bool _descending = true;
    private IReadOnlyList<JobViewModel> _selected = [];
    private string? _message;

    public JobListViewModel(JobQueue queue, TimeProvider time)
    {
        _queue = queue;
        Schedule = new ScheduleInput(time);
        CancelSelectedCommand = new AsyncCommand(() => Apply("取り消し", j => j.CancelCommand), () => _selected.Count > 0);
        PauseSelectedCommand = new AsyncCommand(() => Apply("一時停止", j => j.PauseCommand), () => _selected.Count > 0);
        ResumeSelectedCommand = new AsyncCommand(() => Apply("再開", j => j.ResumeCommand), () => _selected.Count > 0);
        RescheduleSelectedCommand = new AsyncCommand(RescheduleSelectedAsync, () => _selected.Count > 0);
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

    /// <summary>件数の表示。</summary>
    public string CountText => _queue.Jobs.Count == 0
        ? "ジョブはありません。"
        : Items.Count == _queue.Jobs.Count ? $"{Items.Count} 件" : $"{Items.Count} 件（全 {_queue.Jobs.Count} 件）";

    public string KindHeader => Header("種類", JobSortKey.Kind);

    public string TargetHeader => Header("リソースグループ / アプリ名", JobSortKey.Target);

    public string StateHeader => Header("状態", JobSortKey.State);

    public string TimeHeader => Header("時刻", JobSortKey.Time);

    /// <summary>見出しを押したときに並べ替える。今と同じ列なら逆順、違う列なら昇順。</summary>
    public void SortBy(JobSortKey key)
    {
        _descending = _sortKey == key && !_descending;
        _sortKey = key;
        foreach (var name in new[] { nameof(KindHeader), nameof(TargetHeader), nameof(StateHeader), nameof(TimeHeader) })
            OnPropertyChanged(name);
        Refresh();
    }

    /// <summary>選んだ行（表の複数の選択）。</summary>
    public IReadOnlyList<JobViewModel> Selected => _selected;

    /// <summary>1 行だけ選んだときの行（「詳細」で開く）。</summary>
    public JobViewModel? Current => _selected.Count == 1 ? _selected[0] : null;

    public bool HasCurrent => Current != null;

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
        foreach (var command in new[] { CancelSelectedCommand, PauseSelectedCommand, ResumeSelectedCommand, RescheduleSelectedCommand })
            command.RaiseCanExecuteChanged();
    }

    public AsyncCommand CancelSelectedCommand { get; }

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

    private static string Result(string name, int done, int total) =>
        done == total ? $"{done} 件の{name}をしました。" : $"{done} 件の{name}をしました（{total - done} 件は、今の状態ではできないため、そのままです）。";

    private bool Matches(JobViewModel job) =>
        (_targetFilter.Length == 0 || job.TargetText.Contains(_targetFilter, StringComparison.OrdinalIgnoreCase))
        && Filter.Mode switch
        {
            JobFilterMode.Active => job.IsActive,
            JobFilterMode.Scheduled => job.State == DesktopJobState.Scheduled,
            JobFilterMode.Finished => !job.IsActive,
            _ => true,
        };

    // 並べ替え（同じ値なら新しい順。予定の時刻の無いものは最後）
    private IEnumerable<JobViewModel> Sorted(IEnumerable<JobViewModel> jobs)
    {
        if (_sortKey == JobSortKey.Registered)
            return _descending ? jobs.OrderByDescending(j => j.Order) : jobs.OrderBy(j => j.Order);
        var keyed = jobs.Select(j => (Job: j, Key: _sortKey switch
        {
            JobSortKey.Kind => (IComparable?)j.KindText,
            JobSortKey.Target => j.TargetText,
            JobSortKey.State => j.State,
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
        }
        OnPropertyChanged(nameof(CountText));
    }
}
