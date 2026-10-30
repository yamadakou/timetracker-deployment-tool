using System.Collections.ObjectModel;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>ジョブの一覧の並べ替えの列（<see cref="Registered"/> は登録した順で、既定は新しい順）。</summary>
public enum JobSortKey
{
    Registered,
    Kind,

    /// <summary>サブスクリプション（No.78）。</summary>
    Subscription,
    Target,
    State,

    /// <summary>予定日時。</summary>
    Time,

    /// <summary>登録日時（docs/design/desktop/desktop-jobs.md の 6.8）。</summary>
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
/// ジョブの一覧（docs/design/desktop/desktop-wizard.md の 7、No.63）。このアプリのすべてのジョブ（まだ Azure に無い新しいデプロイの予定を含む）を
/// 表に 1 行ずつ出し、見出しで並べ替え・絞り込み、選んだ行に取り消し・一時停止・再開・予定の変更・記録から削除をまとめて行う。詳細は実行状況の画面で見る。
/// 前回までのアプリで終わったジョブの記録も出す（docs/design/desktop/desktop-jobs.md の 6.8）。
/// </summary>
public sealed class JobListViewModel : ObservableObject
{
    public static readonly IReadOnlyList<int> PageSizes = DeploymentListViewModel.PageSizes;

    private readonly JobQueue _queue;
    private readonly IDialogService _dialogs;
    private JobSortKey _sortKey = JobSortKey.Registered;
    private bool _descending = true;
    private IReadOnlyList<JobViewModel> _selected = [];
    private string? _message;
    private int _pageSize = PageSizes[0];
    private int _pageIndex;
    private int _matchedCount;
    private readonly Func<string, string> _subscriptionDisplay;

    /// <param name="subscriptionDisplay">サブスクリプションの表示（名前と ID）。無ければ ID</param>
    public JobListViewModel(JobQueue queue, TimeProvider time, IDialogService dialogs, Func<string, string>? subscriptionDisplay = null)
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
        _subscriptionDisplay = subscriptionDisplay ?? (id => id);
        var display = _subscriptionDisplay;
        // 表の列（▽ の値・表の上のフィルタで探す文字・日付。docs/design/desktop/desktop-tables.md の 10.4）。日付は、行の表示と同じく、この PC のタイムゾーン
        static string Day(DateTimeOffset? at) => at is { } value ? value.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "（なし）";
        Filters = new TableFilters<JobViewModel>(
        [
            new(nameof(JobSortKey.Kind), "種類", j => j.KindText),
            new(nameof(JobSortKey.Subscription), "サブスクリプション", j => display(j.Target.Subscription)),
            new(nameof(JobSortKey.Target), "リソースグループ / アプリ名", j => j.TargetText),
            new(nameof(JobSortKey.State), "状態", j => j.StateLabel, j => j.StatusText),
            new(nameof(JobSortKey.Time), "予定日時", j => j.ScheduledAt == null ? "すぐ実行" : Day(j.ScheduledAt), j => j.TimeText, j => j.ScheduledAt),
            new(nameof(JobSortKey.RegisteredAt), "登録日時", j => Day(j.RegisteredAt), j => j.RegisteredText, j => j.RegisteredAt),
            new(nameof(JobSortKey.ChangedAt), "変更日時", j => Day(j.ChangedAt), j => j.ChangedText, j => j.ChangedAt),
            new(nameof(JobSortKey.StartedAt), "実行開始日時", j => Day(j.StartedAt), j => j.StartedText, j => j.StartedAt),
            new(nameof(JobSortKey.Finished), "実行終了日時", j => Day(j.FinishedAt), j => j.FinishedText, j => j.FinishedAt),
            new(nameof(JobSortKey.Duration), "処理時間", j => j.DurationText ?? "（なし）", j => j.DurationText),
            new(ResultColumn, "結果", j => j.ResultText ?? "（なし）", j => j.ResultText),
        ], () => _queue.Jobs);
        Filters.Changed += () => Refresh(resetPage: true);
        PreviousPageCommand = new AsyncCommand(() => GoTo(_pageIndex - 1), () => _pageIndex > 0);
        NextPageCommand = new AsyncCommand(() => GoTo(_pageIndex + 1), () => _pageIndex < PageCount - 1);
        // ジョブの登録・消去と、状態の変更で並べ直す
        queue.Jobs.CollectionChanged += (_, _) => Refresh(resetPage: false);
        queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.Summary))
                Refresh(resetPage: false);
        };
    }

    /// <summary>絞り込んで並べたジョブのうち、今のページの行。</summary>
    public ObservableCollection<JobViewModel> Items { get; } = [];

    /// <summary>結果の列の名前（並べ替えはしない）。</summary>
    public const string ResultColumn = "Result";

    /// <summary>見出しで並べ替えられる列か（<see cref="JobSortKey"/> にある列。結果の列は並べ替えない）。</summary>
    public static bool CanSortBy(string column) => Enum.TryParse<JobSortKey>(column, out _);

    /// <summary>表の絞り込み（見出しの ▽・表の上の文字と日付のフィルタ。docs/design/desktop/desktop-tables.md の 10.4）。</summary>
    public TableFilters<JobViewModel> Filters { get; }

    /// <summary>件数とページの表示（「絞り込んだ件数 / 全件数」と、終わっていないジョブの内訳。No.75・No.78）。</summary>
    public string CountText => _queue.Jobs.Count == 0
        ? "ジョブはありません。"
        : _matchedCount == 0
            ? $"表示するジョブはありません（全 {_queue.Jobs.Count} 件）。"
            : $"{_pageIndex * PageSize + 1}〜{Math.Min((_pageIndex + 1) * PageSize, _matchedCount)} 件目を表示（" +
              (_matchedCount == _queue.Jobs.Count ? $"全 {_matchedCount} 件" : $"絞り込んだ {_matchedCount} 件 / 全 {_queue.Jobs.Count} 件") +
              $"）・{_pageIndex + 1} / {PageCount} ページ" +
              (_queue.Summary is { Length: > 0 } summary ? $"（終わっていないもの: {summary}）" : "");

    /// <summary>1 ページの行の数（20・50・100。デプロイの一覧と同じ）。</summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (SetProperty(ref _pageSize, value))
                Refresh(resetPage: true);
        }
    }

    public int PageCount => Math.Max(1, (_matchedCount + PageSize - 1) / PageSize);

    /// <summary>今のページ（0 から）。</summary>
    public int PageIndex => _pageIndex;

    public AsyncCommand PreviousPageCommand { get; }

    public AsyncCommand NextPageCommand { get; }

    /// <summary>並べ替えている列の名前（既定の登録した順なら null）。画面の見出しの矢印に使う。</summary>
    public string? SortColumn => _sortKey == JobSortKey.Registered ? null : _sortKey.ToString();

    /// <summary>逆順（降順）に並べているか。</summary>
    public bool SortDescending => _descending;

    /// <summary>見出しを押したときに並べ替える。今と同じ列なら逆順、違う列なら昇順。</summary>
    public void SortBy(JobSortKey key) => Sort(key, _sortKey == key && !_descending);

    /// <summary>見出しの Tag（列の名前。<see cref="JobSortKey"/> の名前）で並べ替える（画面の側から）。</summary>
    public void SortBy(string column) => SortBy(Enum.Parse<JobSortKey>(column));

    /// <summary>▽ の「昇順」「降順」で並べ替える（画面の側から）。</summary>
    public void SortBy(string column, bool descending) => Sort(Enum.Parse<JobSortKey>(column), descending);

    /// <summary>▽ の「並べ替えを解除」で、既定の順（登録の新しい順）に戻す。</summary>
    public void ClearSort() => Sort(JobSortKey.Registered, descending: true);

    private void Sort(JobSortKey key, bool descending)
    {
        _descending = descending;
        _sortKey = key;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        Refresh(resetPage: true);
    }

    /// <summary>表の選択と、この一覧の選択を合わせる。</summary>
    public SelectionSync<JobViewModel> TableSelection { get; }

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

    // 並べ替え（同じ値なら新しい順。値の無いもの〔予定の無いもの・まだ始めていないものなど〕は最後）
    private IEnumerable<JobViewModel> Sorted(IEnumerable<JobViewModel> jobs)
    {
        if (_sortKey == JobSortKey.Registered)
            return _descending ? jobs.OrderByDescending(j => j.Order) : jobs.OrderBy(j => j.Order);
        var keyed = jobs.Select(j => (Job: j, Key: _sortKey switch
        {
            JobSortKey.Kind => (IComparable?)j.KindText,
            JobSortKey.Subscription => Filters.Column(nameof(JobSortKey.Subscription)).ValueOf(j),
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

    private Task GoTo(int page)
    {
        _pageIndex = page;
        Refresh(resetPage: false);
        return Task.CompletedTask;
    }

    // 絞り込んで並べ、今のページの行を出す（並べ替え・絞り込み・ページの大きさを変えたら 1 ページ目に戻る）
    private void Refresh(bool resetPage)
    {
        var matched = Sorted(_queue.Jobs.Where(Filters.Matches)).ToList();
        _matchedCount = matched.Count;
        if (resetPage || _pageIndex >= PageCount)
            _pageIndex = 0;
        var page = matched.Skip(_pageIndex * PageSize).Take(PageSize).ToList();
        foreach (var job in page)
            job.SubscriptionText = _subscriptionDisplay(job.Target.Subscription);
        if (!page.SequenceEqual(Items))
        {
            // 並べ直しても、残っている行の選択は保つ（行を消す間に画面の表が選択を外すため、先に覚えておく）
            var selected = _selected.ToList();
            Items.Clear();
            foreach (var job in page)
                Items.Add(job);
            SetSelection(selected.Where(page.Contains));
        }
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageIndex));
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        DeleteSelectedRecordsCommand.RaiseCanExecuteChanged();
        DeleteFinishedRecordsCommand.RaiseCanExecuteChanged();
    }
}
