using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Common;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>利用可能期限日のフィルタ。</summary>
public enum ExpiryFilterMode
{
    All,

    /// <summary>7 日以内（過ぎたものを含む）。</summary>
    Within7Days,

    /// <summary>期限なし。</summary>
    NoExpiry,

    /// <summary>日付の範囲。</summary>
    Range,
}

/// <summary>利用可能期限日のフィルタの選択肢。</summary>
public sealed record ExpiryFilterOption(ExpiryFilterMode Mode, string Label);

/// <summary>サブスクリプションの絞り込みの選択肢（<see cref="SubscriptionId"/> が null なら、すべて）。</summary>
public sealed record SubscriptionFilterOption(string? SubscriptionId, string Label);

/// <summary>並べ替えの列。</summary>
public enum DeploymentSortKey
{
    Subscription,
    ResourceGroup,
    AppName,
    DeployedAt,
    ExpiresAt,
}

/// <summary>並べ替えの選択肢。</summary>
public sealed record DeploymentSortOption(DeploymentSortKey Key, bool Descending, string Label);

/// <summary>
/// デプロイの一覧（docs/design/desktop-jobs.md の 6.3・6.4）。サインインしたアカウントが読めるサブスクリプションから、本ツールで作った
/// リソースを Resource Graph で読み、デプロイごとにまとめる。フィルタ・並べ替え・ページングは、読んだ結果に対してアプリの中で行う。
/// Resource Graph の反映が遅れるため、このアプリで直前に終えたデプロイ・削除と、変えた期限は、読んだ結果に重ねて表示する。
/// </summary>
public sealed class DeploymentListViewModel : ObservableObject
{
    /// <summary>このアプリで終えた操作を、読んだ結果に重ねる時間（Resource Graph の反映の遅れ）。</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(15);

    /// <summary>期限が近いとみなす日数（色を変える・「7 日以内」のフィルタ）。</summary>
    public const int SoonDays = 7;

    public static readonly IReadOnlyList<int> PageSizes = [20, 50, 100];

    public static readonly IReadOnlyList<ExpiryFilterOption> ExpiryFilters =
    [
        new(ExpiryFilterMode.All, "すべて"),
        new(ExpiryFilterMode.Within7Days, $"{SoonDays} 日以内（過ぎたものを含む）"),
        new(ExpiryFilterMode.NoExpiry, "期限なし"),
        new(ExpiryFilterMode.Range, "日付の範囲"),
    ];

    public static readonly IReadOnlyList<DeploymentSortOption> SortOptions =
    [
        new(DeploymentSortKey.Subscription, false, "サブスクリプション（昇順）"),
        new(DeploymentSortKey.Subscription, true, "サブスクリプション（降順）"),
        new(DeploymentSortKey.ResourceGroup, false, "リソースグループ（昇順）"),
        new(DeploymentSortKey.ResourceGroup, true, "リソースグループ（降順）"),
        new(DeploymentSortKey.DeployedAt, false, "デプロイ日（古い順）"),
        new(DeploymentSortKey.DeployedAt, true, "デプロイ日（新しい順）"),
        new(DeploymentSortKey.ExpiresAt, false, "利用可能期限日（近い順）"),
        new(DeploymentSortKey.ExpiresAt, true, "利用可能期限日（遠い順）"),
        new(DeploymentSortKey.AppName, false, "アプリ名（昇順）"),
        new(DeploymentSortKey.AppName, true, "アプリ名（降順）"),
    ];

    private readonly TargetViewModel _target;
    private readonly JobQueue _queue;
    private readonly IDeploymentLogger _deploymentLogger;
    private readonly ILogger _logger;
    private readonly ISystemLauncher _launcher;
    private readonly IClipboardService? _clipboard;
    private readonly Func<string, Task> _open;
    private readonly Func<IReadOnlyList<DeploymentEntry>, Task> _openBatchDeletion;
    private readonly List<RecentChange> _recent = [];
    private IReadOnlyList<DeploymentEntry> _loaded = [];
    private IReadOnlyList<DeploymentEntry> _entries = [];
    private IReadOnlyList<DeploymentEntry> _filtered = [];
    private bool _hasLoaded;
    private string? _error;
    private SubscriptionFilterOption _subscriptionFilter = AllSubscriptions;
    // 利用者が選んだサブスクリプション（読み直しで一覧が一度空になっても、また現れれば選び直す）
    private string? _wantedSubscriptionId;
    private string _resourceGroupFilter = "";
    private string _appNameFilter = "";
    private IReadOnlyList<DeploymentRowViewModel> _selected = [];
    private bool _batchNoExpiry;
    private DateTimeOffset _batchExpiryDate;
    private string? _batchMessage;
    private DateTimeOffset? _deployedFrom;
    private DateTimeOffset? _deployedTo;
    private ExpiryFilterOption _expiryFilter = ExpiryFilters[0];
    private DateTimeOffset? _expiryFrom;
    private DateTimeOffset? _expiryTo;
    private DeploymentSortOption _sort = SortOptions[0];
    private int _pageSize = PageSizes[0];
    private int _pageIndex;
    private int _filteredCount;

    /// <summary>サブスクリプションの絞り込みの「すべて」。</summary>
    public static readonly SubscriptionFilterOption AllSubscriptions = new(null, "すべてのサブスクリプション");

    /// <param name="open">対象に入れた後に、画面を開く（ウィザード・実行状況・削除の名前: wizard・status・delete）</param>
    /// <param name="openBatchDeletion">2 件以上を選んで削除するときに、まとめての削除の画面を開いて計画する（docs/design/desktop-wizard.md の 7）</param>
    public DeploymentListViewModel(
        TargetViewModel target, JobQueue queue, IDeploymentLogger deploymentLogger, ILogger logger, TimeProvider time,
        ISystemLauncher launcher, IClipboardService? clipboard, Func<string, Task> open, Func<IReadOnlyList<DeploymentEntry>, Task> openBatchDeletion)
    {
        _target = target;
        _deploymentLogger = deploymentLogger;
        _logger = logger;
        Time = time;
        _launcher = launcher;
        _clipboard = clipboard;
        _open = open;
        _openBatchDeletion = openBatchDeletion;
        _batchExpiryDate = DefaultExpiryDate(time);
        ChangeSelectedExpiryCommand = new AsyncCommand(() => BatchBusy.RunAsync("選んだデプロイの利用可能期限を変えています", ChangeSelectedExpiryAsync),
            () => _selected.Count > 0);
        DeleteSelectedCommand = new AsyncCommand(DeleteSelectedAsync, () => _selected.Count > 0);
        // 絞り込みの選択肢は、読んだサブスクリプション（一覧はすべてのサブスクリプションを読む）
        SubscriptionFilters.Add(AllSubscriptions);
        _target.Subscriptions.CollectionChanged += (_, _) => ResetSubscriptionFilters();
        LoadCommand = new AsyncCommand(() => Busy.RunAsync("デプロイの一覧を読んでいます", () => LoadAsync(resetPage: true)));
        PreviousPageCommand = new AsyncCommand(() => GoTo(_pageIndex - 1), () => _pageIndex > 0);
        NextPageCommand = new AsyncCommand(() => GoTo(_pageIndex + 1), () => _pageIndex < PageCount - 1);
        _queue = queue;
        queue.Finished += OnJobFinished;
        // ジョブの登録・状態の変更で、行のジョブの表示を変える
        queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.Summary))
                ShowJobs();
        };
    }

    internal TimeProvider Time { get; }

    public AsyncCommand LoadCommand { get; }

    public AsyncCommand PreviousPageCommand { get; }

    public AsyncCommand NextPageCommand { get; }

    /// <summary>一覧の読み込みの処理中の表示。</summary>
    public BusyState Busy { get; } = new();

    /// <summary>今のページの行。</summary>
    public ObservableCollection<DeploymentRowViewModel> Rows { get; } = [];

    /// <summary>読めなかったテナントなど、一覧についての注意。</summary>
    public ObservableCollection<string> Notices { get; } = [];

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value, new Dependent(nameof(HasError)));
    }

    public bool HasError => Error != null;

    /// <summary>読んだデプロイの数（フィルタの前）。</summary>
    public int TotalCount => _entries.Count;

    /// <summary>フィルタに合うデプロイの数。</summary>
    public int FilteredCount => _filteredCount;

    public int PageCount => Math.Max(1, (FilteredCount + PageSize - 1) / PageSize);

    /// <summary>今のページ（0 から）。</summary>
    public int PageIndex => _pageIndex;

    /// <summary>件数とページの表示。</summary>
    public string PageText => !_hasLoaded
        ? "「読み込む」を押すと、サインインしたアカウントで読めるサブスクリプションから、本ツールでデプロイした環境を読みます。"
        : FilteredCount == 0
            ? $"表示するデプロイはありません（全 {TotalCount} 件）。"
            : $"{_pageIndex * PageSize + 1}〜{Math.Min((_pageIndex + 1) * PageSize, FilteredCount)} 件目 / {FilteredCount} 件" +
              (FilteredCount == TotalCount ? "" : $"（全 {TotalCount} 件）") + $"　{_pageIndex + 1} / {PageCount} ページ";

    /// <summary>サブスクリプションの絞り込みの選択肢（「すべて」と、読んだサブスクリプション）。</summary>
    public ObservableCollection<SubscriptionFilterOption> SubscriptionFilters { get; } = [];

    /// <summary>サブスクリプションの絞り込み（既定はすべて）。</summary>
    public SubscriptionFilterOption SubscriptionFilter
    {
        get => _subscriptionFilter;
        set
        {
            _wantedSubscriptionId = value.SubscriptionId;
            SetFilter(ref _subscriptionFilter, value);
        }
    }

    /// <summary>アプリ名（中間一致）。</summary>
    public string AppNameFilter { get => _appNameFilter; set => SetFilter(ref _appNameFilter, value.Trim()); }

    /// <summary>見出しを押したときに並べ替える。今と同じ列なら逆順、違う列なら昇順（期限日は近い順。docs/design/desktop-wizard.md の 7）。</summary>
    public void SortBy(DeploymentSortKey key) =>
        Sort = SortOptions.First(o => o.Key == key && o.Descending == (Sort.Key == key && !Sort.Descending));

    public string SubscriptionHeader => Header("サブスクリプション", DeploymentSortKey.Subscription);

    public string ResourceGroupHeader => Header("リソースグループ", DeploymentSortKey.ResourceGroup);

    public string AppNameHeader => Header("アプリ名", DeploymentSortKey.AppName);

    public string ExpiryHeader => Header("利用可能期限日", DeploymentSortKey.ExpiresAt);

    public string DeployedHeader => Header("デプロイ日", DeploymentSortKey.DeployedAt);

    private string Header(string label, DeploymentSortKey key) => Sort.Key != key ? label : label + (Sort.Descending ? " ▼" : " ▲");

    /// <summary>選んだ行（表の複数の選択）。</summary>
    public IReadOnlyList<DeploymentRowViewModel> Selected => _selected;

    /// <summary>1 行だけ選んだときの行（表の下に詳細を出す）。</summary>
    public DeploymentRowViewModel? Current => _selected.Count == 1 ? _selected[0] : null;

    public bool HasCurrent => Current != null;

    public string SelectionText => _selected.Count == 0 ? "行を選ぶと、まとめて操作できます。" : $"{_selected.Count} 件を選んでいます。";

    /// <summary>表で選んだ行を伝える（画面の側から）。</summary>
    public void SetSelection(IEnumerable<DeploymentRowViewModel> rows)
    {
        var selected = rows.ToList();
        if (selected.SequenceEqual(_selected))
            return;
        _selected = selected;
        _logger.LogInformation("デプロイの一覧の選択: {Count} 行", selected.Count);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(SelectionText));
        ChangeSelectedExpiryCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>選んだ行の利用可能期限を変える。</summary>
    public AsyncCommand ChangeSelectedExpiryCommand { get; }

    /// <summary>選んだ行を削除する（1 行なら削除の画面、2 行以上ならまとめての削除の画面）。</summary>
    public AsyncCommand DeleteSelectedCommand { get; }

    /// <summary>まとめての操作の処理中の表示。</summary>
    public BusyState BatchBusy { get; } = new();

    /// <summary>まとめて変える期限: 期限なしにするか（false なら <see cref="BatchExpiryDate"/>）。</summary>
    public bool BatchNoExpiry
    {
        get => _batchNoExpiry;
        set => SetProperty(ref _batchNoExpiry, value);
    }

    public DateTimeOffset BatchExpiryDate
    {
        get => _batchExpiryDate;
        set => SetProperty(ref _batchExpiryDate, value);
    }

    /// <summary>まとめての操作の結果。</summary>
    public string? BatchMessage
    {
        get => _batchMessage;
        private set => SetProperty(ref _batchMessage, value);
    }

    /// <summary>リソースグループ名（中間一致）。</summary>
    public string ResourceGroupFilter { get => _resourceGroupFilter; set => SetFilter(ref _resourceGroupFilter, value.Trim()); }

    /// <summary>デプロイ日の範囲（日付だけを見る。空なら制限しない）。</summary>
    public DateTimeOffset? DeployedFrom { get => _deployedFrom; set => SetFilter(ref _deployedFrom, value); }

    public DateTimeOffset? DeployedTo { get => _deployedTo; set => SetFilter(ref _deployedTo, value); }

    public ExpiryFilterOption ExpiryFilter
    {
        get => _expiryFilter;
        set => SetFilter(ref _expiryFilter, value, nameof(ExpiryFilter), nameof(IsExpiryRange));
    }

    /// <summary>利用可能期限日の範囲の入力を表示するか。</summary>
    public bool IsExpiryRange => ExpiryFilter.Mode == ExpiryFilterMode.Range;

    public DateTimeOffset? ExpiryFrom { get => _expiryFrom; set => SetFilter(ref _expiryFrom, value); }

    public DateTimeOffset? ExpiryTo { get => _expiryTo; set => SetFilter(ref _expiryTo, value); }

    public DeploymentSortOption Sort
    {
        get => _sort;
        set
        {
            SetFilter(ref _sort, value);
            foreach (var name in new[] { nameof(SubscriptionHeader), nameof(ResourceGroupHeader), nameof(AppNameHeader), nameof(ExpiryHeader), nameof(DeployedHeader) })
                OnPropertyChanged(name);
        }
    }

    public int PageSize { get => _pageSize; set => SetFilter(ref _pageSize, value); }

    /// <summary>サインアウトしたとき、一覧（とパスワード）を消す。</summary>
    public void Clear()
    {
        _loaded = [];
        _recent.Clear();
        Notices.Clear();
        Error = null;
        _hasLoaded = false;
        Refresh(resetPage: true, keepRows: false);
    }

    /// <summary>サブスクリプションの表示（名前と ID）。</summary>
    internal string SubscriptionDisplay(string subscriptionId) =>
        FindSubscription(subscriptionId) is { } s ? $"{s.DisplayName}（{s.SubscriptionId}）" : subscriptionId;

    internal RowSession GatewayFor(string subscriptionId, IProgress<string> progress) =>
        new(_target.CreateSessionFor(subscriptionId, progress), _deploymentLogger);

    internal ILogger Logger => _logger;

    internal void Open(Uri uri) => _launcher.OpenUri(uri);

    internal bool CanCopy => _clipboard != null;

    internal void Copy(string text) => _clipboard!.CopyText(text);

    /// <summary>行の操作: 対象に入れて、画面を開く（<paramref name="page"/> は wizard・status・delete）。</summary>
    internal async Task UseAsTargetAsync(DeploymentEntry entry, string page)
    {
        if (!_target.Select(entry.SubscriptionId, entry.ResourceGroup, entry.AppName))
        {
            Error = $"サブスクリプション {entry.SubscriptionId} が「サインインと対象」の一覧にありません。サブスクリプションを読み直してください。";
            return;
        }
        Error = null;
        await _open(page);
    }

    /// <summary>行で期限を変えたとき（Resource Graph に反映されるまで、読んだ結果に重ねる）。</summary>
    internal void ExpiryChanged(DeploymentEntry entry, ExpiryDecision decision)
    {
        Remember(entry.SubscriptionId, entry.ResourceGroup, entry.AppName, e => e == null ? null : e with
        {
            ExpiryKind = decision.ExpiresAt == null ? ExpiryKind.None : ExpiryKind.Date,
            ExpiresAt = decision.ExpiresAt,
            ExpiryDiffers = false,
        });
    }

    // サブスクリプションを読み直したら、絞り込みの選択肢を作り直す（選んでいたものが無くなれば、すべてに戻す）
    private void ResetSubscriptionFilters()
    {
        var wanted = _wantedSubscriptionId;
        SubscriptionFilters.Clear();
        SubscriptionFilters.Add(AllSubscriptions);
        foreach (var s in _target.Subscriptions)
            SubscriptionFilters.Add(new SubscriptionFilterOption(s.SubscriptionId, s.Display));
        SetFilter(ref _subscriptionFilter,
            SubscriptionFilters.FirstOrDefault(f => string.Equals(f.SubscriptionId, wanted, StringComparison.OrdinalIgnoreCase)) ?? AllSubscriptions,
            nameof(SubscriptionFilter));
    }

    private SubscriptionChoice? FindSubscription(string subscriptionId) =>
        _target.Subscriptions.FirstOrDefault(c => string.Equals(c.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 期限の確認のために読み直す（ページ・フィルタ・行は保つ。docs/design/desktop-jobs.md の 6.5）。読めれば true（読めないテナントがあっても、
    /// ほかのテナントを読めれば true）。
    /// </summary>
    internal async Task<bool> ReloadAsync()
    {
        await Busy.RunAsync("期限を確かめるため、デプロイの一覧を読み直しています", () => LoadAsync(resetPage: false));
        return Error == null;
    }

    /// <summary>読んだデプロイが変わったとき（読み直し・このアプリでのデプロイ・削除・期限の変更。期限の確認の時刻を合わせ直す）。</summary>
    internal event Action EntriesChanged = () => { };

    /// <summary>読んだすべてのデプロイ（フィルタ・ページによらない。このアプリで終えた操作を重ねた後）。</summary>
    internal IReadOnlyList<DeploymentEntry> Entries => _entries;

    /// <summary>利用可能期限日のあるデプロイを読んだか（閉じたときに常駐するか。D18）。</summary>
    public bool HasExpiringDeployments => _entries.Any(e => e.ExpiryKind == ExpiryKind.Date);

    // テナントごとに、そのテナントのサブスクリプションを Resource Graph で読む（読めないテナントがあっても、ほかは続ける）
    private async Task LoadAsync(bool resetPage)
    {
        Error = null;
        Notices.Clear();
        var groups = _target.Subscriptions.GroupBy(s => s.TenantId).ToList();
        if (groups.Count == 0)
        {
            Error = "サブスクリプションがありません。「サインインと対象」でサインインし、サブスクリプションを読んでください。";
            return;
        }
        var records = new List<TTDeployTool.Infrastructure.Azure.ManagedResourceRecord>();
        var failed = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var name = group.First().TenantName ?? group.Key ?? "既定のテナント";
            Busy.Report($"テナント {i + 1} / {groups.Count}（{name}）のデプロイを読んでいます");
            try
            {
                records.AddRange(await _target.CreateDirectory(group.Key).ListAsync(group.Select(s => s.SubscriptionId).ToList()));
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "テナント {TenantId} のデプロイの一覧を読めませんでした", group.Key);
                Notices.Add($"テナント {name} のデプロイを読めませんでした: {DesktopMessages.ForAzure(ex)}");
            }
        }
        if (failed == groups.Count)
        {
            Error = "デプロイの一覧を読めませんでした。注意の内容を確認してください。";
            return;
        }
        _loaded = DeploymentCatalog.Build(records);
        _hasLoaded = true;
        _logger.LogInformation("デプロイの一覧: {Count} 件（リソース {Records} 件）", _loaded.Count, records.Count);
        Notices.Add("Azure に反映されるまで数分かかることがあります。このアプリで直前に行ったデプロイ・削除・期限の変更は、一覧に反映済みとして表示します。");
        Refresh(resetPage, keepRows: !resetPage);
    }

    // このアプリで終えたデプロイ・削除を、Resource Graph に反映されるまで一覧に重ねる
    private void OnJobFinished(JobViewModel job)
    {
        var t = job.Target;
        switch (job.Result)
        {
            case DeploymentResult r:
                var at = Time.GetUtcNow();
                // デプロイの実行は、必ず期限を記録する（DeploymentRunner）
                var expiry = r.Expiry!;
                Remember(t.Subscription, t.ResourceGroup, t.AppName, e => Deployed(e, Placeholder(t.Subscription, t.ResourceGroup, t.AppName, r)) with
                {
                    Url = r.TimetrackerUrl ?? e?.Url,
                    DeployedAt = at,
                    ExpiryKind = expiry.ExpiresAt == null ? ExpiryKind.None : ExpiryKind.Date,
                    ExpiresAt = expiry.ExpiresAt,
                });
                break;
            case DeletionOutcome d:
                var gone = d.Deleted.Concat(d.AlreadyDeleted).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Remember(t.Subscription, t.ResourceGroup, t.AppName, e => Without(e, gone, d.ResourceGroupDeleted));
                break;
        }
    }

    private static DeploymentEntry Placeholder(string subscriptionId, string resourceGroup, string appName, DeploymentResult r) => new(
        subscriptionId, resourceGroup, appName, null, null, null, ExpiryKind.Missing, null,
        new AppEntry(r.TimetrackerAppName, null, null, null, null, null), new AppEntry(r.DbAppName, null, null, null, null, null),
        new AppEntry(r.RedisAppName, null, null, null, null, null), r.EnvironmentName, r.StorageAccountName, r.StorageAccountName != null);

    // デプロイしたものを重ねる。Resource Graph に一部だけ反映されている場合（デプロイの直後に Timetracker のアプリが無い、など）は、無いものを
    // デプロイの結果で補う（「パスワードを表示」が押せなかった。Desktop System Test、2026-10-23）
    private static DeploymentEntry Deployed(DeploymentEntry? e, DeploymentEntry placeholder) => e == null
        ? placeholder
        : e with
        {
            Timetracker = e.Timetracker ?? placeholder.Timetracker,
            Db = e.Db ?? placeholder.Db,
            Redis = e.Redis ?? placeholder.Redis,
            EnvironmentName = e.EnvironmentName ?? placeholder.EnvironmentName,
            StorageAccountName = e.StorageAccountName ?? placeholder.StorageAccountName,
            DbPersisted = e.DbPersisted || placeholder.DbPersisted,
        };

    // 削除したものを除く（何も残らなければ一覧から消す）
    private static DeploymentEntry? Without(DeploymentEntry? e, HashSet<string> gone, bool resourceGroupDeleted)
    {
        if (e == null || resourceGroupDeleted)
            return null;
        AppEntry? Keep(AppEntry? app) => app != null && !gone.Contains(app.Name) ? app : null;
        string? KeepName(string? name) => name != null && !gone.Contains(name) ? name : null;
        var rest = e with
        {
            Timetracker = Keep(e.Timetracker), Db = Keep(e.Db), Redis = Keep(e.Redis),
            EnvironmentName = KeepName(e.EnvironmentName), StorageAccountName = KeepName(e.StorageAccountName),
        };
        return rest.Missing.Count == 4 && rest.StorageAccountName == null ? null : rest;
    }

    private void Remember(string subscriptionId, string resourceGroup, string appName, Func<DeploymentEntry?, DeploymentEntry?> apply)
    {
        _recent.Add(new RecentChange(Time.GetUtcNow(), subscriptionId, resourceGroup, appName, apply));
        Refresh(resetPage: false, keepRows: true);
    }

    // 読んだ結果に、最近の変更を順に重ねる
    private IReadOnlyList<DeploymentEntry> Apply()
    {
        var now = Time.GetUtcNow();
        _recent.RemoveAll(c => now - c.At > RecentWindow);
        var entries = _loaded.ToList();
        foreach (var change in _recent)
        {
            var index = entries.FindIndex(e => e.IsFor(change.SubscriptionId, change.ResourceGroup, change.AppName));
            var updated = change.Apply(index < 0 ? null : entries[index]);
            if (index >= 0)
                entries.RemoveAt(index);
            if (updated != null)
                entries.Insert(index < 0 ? entries.Count : index, updated);
        }
        return entries;
    }

    private bool Matches(DeploymentEntry e)
    {
        var zone = Time.LocalTimeZone;
        DateOnly Date(DateTimeOffset value) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
        static DateOnly Picked(DateTimeOffset value) => DateOnly.FromDateTime(value.DateTime);
        bool InRange(DateTimeOffset? value, DateTimeOffset? from, DateTimeOffset? to) =>
            (from == null && to == null)
            || (value is { } v && (from == null || Date(v) >= Picked(from.Value)) && (to == null || Date(v) <= Picked(to.Value)));

        if (SubscriptionFilter.SubscriptionId is { } subscriptionId && !string.Equals(e.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (ResourceGroupFilter.Length > 0 && !e.ResourceGroup.Contains(ResourceGroupFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (AppNameFilter.Length > 0 && !e.AppName.Contains(AppNameFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!InRange(e.DeployedAt, DeployedFrom, DeployedTo))
            return false;
        return ExpiryFilter.Mode switch
        {
            ExpiryFilterMode.Within7Days => e.ExpiryKind == ExpiryKind.Date && e.ExpiresAt <= Time.GetUtcNow().AddDays(SoonDays),
            ExpiryFilterMode.NoExpiry => e.ExpiryKind == ExpiryKind.None,
            ExpiryFilterMode.Range => e.ExpiryKind == ExpiryKind.Date && InRange(e.ExpiresAt, ExpiryFrom, ExpiryTo),
            _ => true,
        };
    }

    // 並べ替え（値の無いものは最後。同じ値ならサブスクリプション・リソースグループ・アプリ名の順）
    private IEnumerable<DeploymentEntry> Sorted(IEnumerable<DeploymentEntry> entries)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var keyed = entries.Select(e => (Entry: e, Key: Sort.Key switch
        {
            DeploymentSortKey.Subscription => (object?)SubscriptionDisplay(e.SubscriptionId),
            DeploymentSortKey.ResourceGroup => e.ResourceGroup,
            DeploymentSortKey.AppName => e.AppName,
            DeploymentSortKey.DeployedAt => e.DeployedAt,
            _ => e.ExpiryKind == ExpiryKind.Date ? e.ExpiresAt : null,
        }));
        var withValue = keyed.Where(x => x.Key != null);
        var ordered = Sort.Descending
            ? withValue.OrderByDescending(x => x.Key, Comparer<object?>.Create((a, b) => Compare(a, b, comparer)))
            : withValue.OrderBy(x => x.Key, Comparer<object?>.Create((a, b) => Compare(a, b, comparer)));
        return ordered.ThenBy(x => x.Entry.SubscriptionId, comparer).ThenBy(x => x.Entry.ResourceGroup, comparer).ThenBy(x => x.Entry.AppName, comparer)
            .Concat(keyed.Where(x => x.Key == null))
            .Select(x => x.Entry);
    }

    private static int Compare(object? a, object? b, StringComparer comparer) => a is string s ? comparer.Compare(s, (string)b!) : ((DateTimeOffset)a!).CompareTo((DateTimeOffset)b!);

    private Task GoTo(int page)
    {
        _pageIndex = page;
        ShowPage(keepRows: false);
        return Task.CompletedTask;
    }

    // keepRows: 同じデプロイの行を使い続ける（読んだパスワードと操作の結果を残す）。読み直し・ページの切り替えでは作り直す
    private void Refresh(bool resetPage, bool keepRows)
    {
        _entries = Apply();
        EntriesChanged.Invoke();
        _filtered = Sorted(_entries.Where(Matches)).ToList();
        _filteredCount = _filtered.Count;
        if (resetPage || _pageIndex >= PageCount)
            _pageIndex = 0;
        ShowPage(keepRows);
    }

    private void ShowPage(bool keepRows)
    {
        var previous = keepRows ? Rows.ToList() : [];
        // 選んでいたデプロイがまた出れば、選んだままにする（読み直し・期限の確認の読み直しでも外さない。行を消す間に画面の表が選択を外すため、先に覚えておく）
        var selected = _selected.Select(r => r.Entry).ToList();
        Rows.Clear();
        foreach (var entry in _filtered.Skip(_pageIndex * PageSize).Take(PageSize))
        {
            var row = previous.FirstOrDefault(r => r.Entry.IsFor(entry.SubscriptionId, entry.ResourceGroup, entry.AppName));
            if (row == null)
                row = new DeploymentRowViewModel(entry, this);
            else
                row.Update(entry);
            Rows.Add(row);
        }
        ShowJobs();
        // 行を作り直したら、選択もやり直す。使い続けた行は選んだままにする（画面の表は Selected の変更で選び直す）
        SetSelection(Rows.Where(r => selected.Any(e => r.Entry.IsFor(e.SubscriptionId, e.ResourceGroup, e.AppName))));
        _logger.LogInformation("デプロイの一覧を表示し直しました: {Count} 行（選択 {Selected} 行、行を使い続ける {KeepRows}）", Rows.Count, _selected.Count, keepRows);
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageIndex));
        OnPropertyChanged(nameof(PageText));
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
    }

    // 期限の入力の初期値（今日から 32 日後）
    private static DateTimeOffset DefaultExpiryDate(TimeProvider time)
    {
        var zone = time.LocalTimeZone;
        var local = DeploymentExpiry.Today(time.GetUtcNow(), zone).AddDays(DeploymentExpiry.DefaultDays).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    // 選んだ行の期限を変える。ジョブのある行は変えない。行ごとの結果をまとめて出す
    private async Task ChangeSelectedExpiryAsync()
    {
        var input = BatchNoExpiry ? DeploymentExpiry.None : BatchExpiryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var changed = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        foreach (var row in _selected.ToList())
        {
            if (row.HasActiveJob)
            {
                skipped.Add(row.AppName);
                continue;
            }
            if (await row.ApplyExpiryAsync(input) is { } error)
                failed.Add($"{row.AppName}（{error}）");
            else
                changed.Add(row.AppName);
        }
        BatchMessage = string.Join(" ", new[]
        {
            changed.Count > 0 ? $"{changed.Count} 件の利用可能期限を変えました（{string.Join("、", changed)}）。" : null,
            skipped.Count > 0 ? $"ジョブがあるため変えなかったもの: {string.Join("、", skipped)}。" : null,
            failed.Count > 0 ? $"変えられなかったもの: {string.Join("、", failed)}" : null,
        }.OfType<string>());
    }

    // 1 行なら今の削除の画面、2 行以上ならまとめての削除の画面を開く
    private async Task DeleteSelectedAsync()
    {
        BatchMessage = null;
        if (_selected.Count == 1)
        {
            if (_selected[0].HasActiveJob)
                BatchMessage = $"{_selected[0].AppName} にはジョブがあるため、削除できません。ジョブが終わるか、取り消してから削除してください。";
            else
                await UseAsTargetAsync(_selected[0].Entry, "delete");
            return;
        }
        await _openBatchDeletion(_selected.Select(r => r.Entry).ToList());
    }

    // 今のページの行に、同じ対象の終わっていないジョブを入れる
    private void ShowJobs()
    {
        foreach (var row in Rows)
            row.SetActiveJob(_queue.ActiveFor(row.Entry.SubscriptionId, row.Entry.ResourceGroup, row.AppName));
    }

    private void SetFilter<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = "", string? dependent = null)
    {
        if (!SetProperty(ref field, value, propertyName))
            return;
        if (dependent != null)
            OnPropertyChanged(dependent);
        Refresh(resetPage: true, keepRows: true);
    }

    private sealed record RecentChange(
        DateTimeOffset At, string SubscriptionId, string ResourceGroup, string AppName, Func<DeploymentEntry?, DeploymentEntry?> Apply);
}

/// <summary>行の操作で Azure を使う接続（その行のサブスクリプションのテナント）。</summary>
internal sealed record RowSession(IAzureSession Session, IDeploymentLogger Logger)
{
    public DeploymentMaintenance Maintenance(TimeProvider time) => new(Session.CreateGateway(Logger), time);
}
