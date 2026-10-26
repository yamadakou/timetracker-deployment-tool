using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Common;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.Jobs;

/// <summary>削除のジョブを、削除せずに終えた理由（期限が延ばされた・削除するものが無い）。期限での削除と、前回の削除の予定を戻したもので使う。</summary>
public sealed class DeletionSkippedException(string message) : InvalidOperationException(message);

/// <summary>
/// 利用可能期限の確認（docs/design/desktop-jobs.md の 6.2・6.5、D21）。サインインできたときにデプロイの一覧を読み、期限の 3 日前と前日に通知し、
/// 期限を過ぎたものは確認なしで削除のジョブを登録する。次の確認は、読んだ一覧の中で最も近い時刻（期限・3 日前・前日）か、
/// 外の変化（別の PC・CLI・MCP サーバーでのデプロイや期限の変更）を拾うための <see cref="RefreshInterval"/> ごとの読み直しの、早い方に合わせる。
/// 一覧の中身が変わったとき（このアプリでのデプロイ・削除・期限の変更）も合わせ直す。サインインしていない間は確認しない。
/// </summary>
public sealed class ExpiryWatcher : IDisposable
{
    /// <summary>外の変化を拾うため、一覧を読み直す間隔。</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    /// <summary>期限などの時刻に合わせた確認を、その時刻より少し後にする（タイマーが早めに来ても、時刻を過ぎてから確かめるため）。</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(1);

    /// <summary>期限での削除に失敗した対象を、登録し直すまでの時間（確認のたびに失敗と通知を繰り返さないため）。</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

    /// <summary>「3 日前」の通知を出す、期限までの時間。</summary>
    public static readonly TimeSpan SoonNotice = TimeSpan.FromDays(3);

    /// <summary>「前日」の通知を出す、期限までの時間。</summary>
    public static readonly TimeSpan LastDayNotice = TimeSpan.FromDays(1);

    private readonly DeploymentListViewModel _list;
    private readonly JobQueue _queue;
    private readonly TargetViewModel _target;
    private readonly DeletionWorkflow _workflow;
    private readonly IDeploymentLogger _deploymentLogger;
    private readonly IAppPreferences _preferences;
    private readonly INotificationService? _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<bool> _isSignedIn;
    private readonly SynchronizationContext? _context;
    private readonly Dictionary<string, DateTimeOffset> _failedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private Task _checking = Task.CompletedTask;
    private DateTimeOffset _refreshAt;

    public ExpiryWatcher(
        DeploymentListViewModel list, JobQueue queue, TargetViewModel target, DeletionWorkflow workflow, IDeploymentLogger deploymentLogger,
        IAppPreferences preferences, INotificationService? notifications, TimeProvider time, ILogger logger, Func<bool> isSignedIn)
    {
        _list = list;
        _queue = queue;
        _target = target;
        _workflow = workflow;
        _deploymentLogger = deploymentLogger;
        _preferences = preferences;
        _notifications = notifications;
        _time = time;
        _logger = logger;
        _isSignedIn = isSignedIn;
        _context = SynchronizationContext.Current;
        queue.Finished += OnJobFinished;
        list.EntriesChanged += Reschedule;
    }

    /// <summary>今の確認（テストで待つ）。</summary>
    internal Task Checking => _checking;

    /// <summary>次の確認の時刻（決まっていなければ null。テストで確かめる）。</summary>
    internal DateTimeOffset? NextCheckAt { get; private set; }

    /// <summary>今すぐ確かめ、その後は次の確認の時刻に確かめる（サインインできたとき）。確かめている間は重ねない。</summary>
    public Task StartAsync()
    {
        _timer ??= _time.CreateTimer(_ => Post(() => _ = CheckAsync()), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return CheckAsync();
    }

    /// <summary>
    /// 確かめる（前の確認が終わっていなければ、それを待つだけ）。画面のスレッドが無いテストでは、タイマーとテストから同時に呼ばれるため、
    /// 始めるかの判断を重ねない（重なると、同じ通知を 2 回出す）。
    /// </summary>
    public Task CheckAsync()
    {
        TaskCompletionSource started;
        lock (_gate)
        {
            if (!_checking.IsCompleted)
                return _checking;
            started = new TaskCompletionSource();
            _checking = started.Task;
        }
        _ = RunCheckAsync().ContinueWith(started.SetFromTask, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return started.Task;
    }

    public void Dispose() => _timer?.Dispose();

    private async Task RunCheckAsync()
    {
        if (!_isSignedIn())
            return;
        var loaded = await _list.ReloadAsync();
        _refreshAt = _time.GetUtcNow() + RefreshInterval;
        Reschedule();
        if (!loaded)
            return;
        var now = _time.GetUtcNow();
        var notices = _preferences.ExpiryNotices.Where(n => !IsPast(n, now)).ToList();
        foreach (var entry in _list.Entries.Where(e => e.ExpiryKind == ExpiryKind.Date))
        {
            var left = entry.ExpiresAt!.Value - now;
            if (left <= TimeSpan.Zero)
                RegisterDeletion(entry, now);
            else if (left <= SoonNotice)
                Notify(entry, left <= LastDayNotice ? "1d" : "3d", notices);
        }
        if (!notices.SequenceEqual(_preferences.ExpiryNotices))
            _preferences.ExpiryNotices = notices;
    }

    // 次の確認を、読み直しの時刻か、一覧の中で最も近い時刻（期限・3 日前・前日）の早い方に合わせる
    private void Reschedule()
    {
        // 始める前と、初めての読み込みの途中（読み直しの時刻がまだ無い）は合わせない
        if (_timer == null || _refreshAt == default)
            return;
        var now = _time.GetUtcNow();
        var next = _list.Entries
            .Where(e => e.ExpiryKind == ExpiryKind.Date)
            .SelectMany(e => new[] { e.ExpiresAt!.Value - SoonNotice, e.ExpiresAt.Value - LastDayNotice, e.ExpiresAt.Value })
            .Where(at => at > now && at < _refreshAt)
            .Select(at => at + Margin)
            .DefaultIfEmpty(_refreshAt)
            .Min();
        NextCheckAt = next;
        // 読み直しの時刻を過ぎていれば（サインアウトの後など）、すぐ確かめる
        _timer.Change(TimeSpan.FromTicks(Math.Max(0, (next - now).Ticks)), Timeout.InfiniteTimeSpan);
    }

    // 出した通知の記録: サブスクリプション/リソースグループ/アプリ名/種類/そのときの期限（UTC）。期限が変われば別のものになる
    private static string NoticeKey(DeploymentEntry entry, string kind) =>
        $"{entry.SubscriptionId}/{entry.ResourceGroup}/{entry.AppName}/{kind}/{DeploymentExpiry.FormatTag(entry.ExpiresAt!.Value)}".ToLowerInvariant();

    // 期限を過ぎた記録は消す（読めない記録も消す）
    private static bool IsPast(string notice, DateTimeOffset now) =>
        !DeploymentExpiry.TryParseTag(notice[(notice.LastIndexOf('/') + 1)..].ToUpperInvariant(), out var at) || at <= now;

    private void Notify(DeploymentEntry entry, string kind, List<string> notices)
    {
        var key = NoticeKey(entry, kind);
        if (notices.Contains(key))
            return;
        notices.Add(key);
        var date = TimeZoneInfo.ConvertTime(entry.ExpiresAt!.Value, _time.LocalTimeZone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        _logger.LogInformation("利用可能期限が近いことを通知します: {ResourceGroup} / {AppName}（{Kind}）", entry.ResourceGroup, entry.AppName, kind);
        _notifications?.Show(new AppNotice(
            kind == "1d" ? "利用可能期限は明日までです" : "利用可能期限が近づいています",
            $"{entry.AppName}（{entry.ResourceGroup}）の利用可能期限は {date} です。期限を過ぎると、このアプリが DB のデータも含めて削除します。" +
            "延ばすには、デプロイの一覧で期限を変えてください。",
            Page: Pages.Deployments));
    }

    private void RegisterDeletion(DeploymentEntry entry, DateTimeOffset now)
    {
        var key = $"{entry.SubscriptionId}/{entry.ResourceGroup}/{entry.AppName}";
        if (_queue.ActiveFor(entry.SubscriptionId, entry.ResourceGroup, entry.AppName) != null
            || (_failedAt.TryGetValue(key, out var failed) && now - failed < RetryAfterFailure))
            return;
        var session = _target.CreateJobSessionFor(entry.SubscriptionId);
        var job = _queue.Register(JobKind.Delete, new JobTarget(entry.SubscriptionId, entry.ResourceGroup, entry.AppName), null,
            () => StartAsync(entry, session), expiryDeletion: true);
        _logger.LogWarning("利用可能期限を過ぎたため、削除のジョブを登録しました: {ResourceGroup} / {AppName}", entry.ResourceGroup, entry.AppName);
        _notifications?.Show(new AppNotice(
            "利用可能期限を過ぎたため削除します",
            $"{entry.AppName}（{entry.ResourceGroup}）は利用可能期限を過ぎたため、DB のデータも含めて削除します。進み具合はジョブの画面で確かめられます。",
            job.Id));
    }

    // 始める直前に、期限を Azure から読み直してから計画する（別の PC や一覧で延ばされていれば削除しない）
    private async Task<Job> StartAsync(DeploymentEntry entry, IAzureSession session)
    {
        var expiry = await new DeploymentMaintenance(session.CreateGateway(_deploymentLogger), _time)
            .ReadExpiryAsync(entry.SubscriptionId, entry.ResourceGroup, entry.AppName);
        if (!DeploymentExpiry.TryParseTag(expiry, out var at) || at > _time.GetUtcNow())
            throw new DeletionSkippedException(
                $"利用可能期限が延ばされていたため、削除しませんでした（今の期限: {Describe(expiry)}）。");
        var resourceGroup = await session.CreateGateway(_deploymentLogger).GetResourceGroupAsync(entry.SubscriptionId, entry.ResourceGroup);
        // 管理タグのあるものだけを、DB のデータも含めて消す。リソースグループは本ツールが作ったものだけ、空になれば消す
        var ticket = await _workflow.PlanAsync(session, entry.SubscriptionId, entry.ResourceGroup, entry.AppName, includeUntagged: false, keepDbData: false,
            deleteResourceGroup: resourceGroup?.CreatedByTool == true);
        if (ticket.Blockers.Count > 0)
            throw new WorkflowException(WorkflowFailure.Blocked, ticket.Blockers);
        if (ticket.Saved == null)
            throw new DeletionSkippedException("削除するものはありませんでした（既に削除されています）。");
        return await _workflow.StartAsync(ticket.Saved.PlanId, ticket.Plan.AppName, _ => session, DeletionViewModel.KeptNotice);
    }

    private string Describe(string? tag) =>
        DeploymentExpiry.TryParseTag(tag, out var at)
            ? TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : tag == DeploymentExpiry.None ? "期限なし" : "記録なし";

    // 失敗した対象は、しばらく登録し直さない（始められずに終わると、登録の中で呼ばれる）
    private void OnJobFinished(JobViewModel job)
    {
        if (!job.IsExpiryDeletion || job.State != DesktopJobState.Failed)
            return;
        var t = job.Target;
        _failedAt[$"{t.Subscription}/{t.ResourceGroup}/{t.AppName}"] = _time.GetUtcNow();
    }

    private void Post(Action action)
    {
        if (_context == null)
            action();
        else
            _context.Post(_ => action(), null);
    }
}
