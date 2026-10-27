using System.Collections.ObjectModel;
using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// まとめての削除の 1 件（計画の要約。<see cref="Ticket"/> が null か実行できなければ、削除から外す）。
/// </summary>
public sealed record BatchDeletionItem(DeploymentEntry Entry, DeletionPlanTicket? Ticket, string Summary, string? Excluded)
{
    public string Title => $"{Entry.AppName}（{Entry.ResourceGroup}）";

    public bool WillRun => Excluded == null;

    public bool IsExcluded => Excluded != null;
}

/// <summary>
/// デプロイの一覧で 2 件以上を選んだときの、まとめての削除（docs/design/desktop-wizard.md の 7、Q10）。行ごとに計画を作って並べ、
/// 計画に誤りがあるもの・ジョブがあるものは外す。削除する件数を入力するまで実行できない。実行すると行ごとに削除のジョブを登録する。
/// </summary>
public sealed class BatchDeletionViewModel : ObservableObject
{
    private readonly DeletionWorkflow _workflow;
    private readonly TargetViewModel _target;
    private readonly JobQueue _queue;
    private readonly Func<DeletionPlanTicket, string, IAzureSession, Task<Job>> _start;
    private readonly Action<IReadOnlyList<JobViewModel>> _registered;
    private IReadOnlyList<DeploymentEntry> _entries = [];
    private bool _keepDbData;
    private bool _deleteResourceGroup;
    private string _confirmCount = "";
    private string? _error;

    /// <param name="start">確かめた計画で削除のジョブを始める（計画の有効期限が切れていれば計画し直す。<see cref="DeletionViewModel.StartPlannedAsync"/>）</param>
    /// <param name="registered">登録したジョブを画面に表示する（ジョブの一覧を開く）</param>
    public BatchDeletionViewModel(
        DeletionWorkflow workflow, TargetViewModel target, JobQueue queue, Func<DeletionPlanTicket, string, IAzureSession, Task<Job>> start,
        Action<IReadOnlyList<JobViewModel>> registered, TimeProvider time)
    {
        _workflow = workflow;
        _target = target;
        _queue = queue;
        _start = start;
        _registered = registered;
        Schedule = new ScheduleInput(time);
        // 予定の日時を変えたら、押せない理由を出し直す
        Schedule.PropertyChanged += (_, _) => RaiseBlocked();
        PlanCommand = new AsyncCommand(() => Busy.RunAsync("削除の計画を作っています", PlanAsync), () => _entries.Count > 0);
        StartCommand = new AsyncCommand(() => RegisterAsync(null), () => CanStart);
        ScheduleCommand = new AsyncCommand(() => RegisterAsync(Schedule), () => CanStart);
    }

    /// <summary>選んだデプロイで計画し直す。</summary>
    public AsyncCommand PlanCommand { get; }

    public AsyncCommand StartCommand { get; }

    public AsyncCommand ScheduleCommand { get; }

    public ScheduleInput Schedule { get; }

    public BusyState Busy { get; } = new();

    public ObservableCollection<BatchDeletionItem> Items { get; } = [];

    /// <summary>削除する件数（外したものを除く）。</summary>
    public int RunnableCount => Items.Count(i => i.WillRun);

    /// <summary>全部に当てる: DB のデータ（ストレージアカウント）を残すか。</summary>
    public bool KeepDbData
    {
        get => _keepDbData;
        set
        {
            if (SetProperty(ref _keepDbData, value))
                ClearPlans();
        }
    }

    /// <summary>全部に当てる: 本ツールのリソースを削除した後に空になるなら、リソースグループも削除するか。</summary>
    public bool DeleteResourceGroup
    {
        get => _deleteResourceGroup;
        set
        {
            if (SetProperty(ref _deleteResourceGroup, value))
                ClearPlans();
        }
    }

    /// <summary>確認の入力（削除する件数）。</summary>
    public string ConfirmCount
    {
        get => _confirmCount;
        set
        {
            if (!SetProperty(ref _confirmCount, value))
                return;
            OnPropertyChanged(nameof(CanStart));
            StartCommand.RaiseCanExecuteChanged();
            ScheduleCommand.RaiseCanExecuteChanged();
            RaiseBlocked();
        }
    }

    /// <summary>確認の案内。</summary>
    public string? ConfirmText => RunnableCount == 0
        ? null
        : $"{RunnableCount} 件を削除します（元に戻せません）。確かめたら、削除する件数（{RunnableCount}）を入力してください。";

    /// <summary>「今すぐ削除」「予定に追加」が押せない理由（押せれば null。No.74）。</summary>
    public string? BlockedText => RunnableCount == 0
        ? null
        : !CanStart
            ? $"削除する件数（{RunnableCount}）を入力すると、「今すぐ削除」と「予定に追加」が押せます。"
            : Schedule.Error is { } error ? $"「予定に追加」: {error}" : null;

    /// <summary>押せない理由を出すか。</summary>
    public bool IsBlocked => BlockedText != null;

    public bool CanStart => RunnableCount > 0 && ConfirmCount.Trim() == RunnableCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value, new Dependent(nameof(HasError)));
    }

    public bool HasError => Error != null;

    /// <summary>まとめての削除の画面を開いたとき: 選んだデプロイを入れて計画する。</summary>
    public async Task OpenAsync(IReadOnlyList<DeploymentEntry> entries)
    {
        _entries = entries;
        PlanCommand.RaiseCanExecuteChanged();
        await PlanCommand.ExecuteAsync();
    }

    // 行ごとに計画する。ジョブがあるもの・計画に誤りがあるもの・削除するものが無いものは外す
    private async Task PlanAsync()
    {
        Error = null;
        ClearPlans();
        foreach (var entry in _entries)
        {
            Busy.Report($"{entry.AppName}（{entry.ResourceGroup}）の削除の計画を作っています");
            Items.Add(await PlanOneAsync(entry));
        }
        Changed();
    }

    private async Task<BatchDeletionItem> PlanOneAsync(DeploymentEntry entry)
    {
        if (_queue.ActiveFor(entry.SubscriptionId, entry.ResourceGroup, entry.AppName) is { } active)
            return new BatchDeletionItem(entry, null, "", $"ジョブ（{active.Title}: {active.StatusText}）があるため外します。");
        try
        {
            var ticket = await _workflow.PlanAsync(_target.CreateSessionFor(entry.SubscriptionId, Busy), entry.SubscriptionId, entry.ResourceGroup, entry.AppName,
                includeUntagged: false, KeepDbData, deleteResourceGroup: DeleteResourceGroup);
            var summary = Summary(ticket.Plan);
            if (ticket.Blockers.Count > 0)
                return new BatchDeletionItem(entry, ticket, summary, "実行できないため外します: " + string.Join(" ", ticket.Blockers));
            return ticket.CanRun
                ? new BatchDeletionItem(entry, ticket, summary, null)
                : new BatchDeletionItem(entry, ticket, summary, "削除するものが無いため外します（既に削除されています）。");
        }
        catch (Exception ex)
        {
            return new BatchDeletionItem(entry, null, "", "計画を作れなかったため外します: " + DesktopMessages.ForAzure(ex));
        }
    }

    // 削除するもの・残すもの・DB のデータ・リソースグループの要約
    private static string Summary(DeletionPlan plan)
    {
        var deletes = plan.Targets.Where(t => t.WillDelete).Select(t => t.Name).ToList();
        var parts = new List<string> { deletes.Count == 0 ? "削除するものはありません。" : $"削除する: {string.Join("、", deletes)}。" };
        var kept = plan.Targets.Where(t => !t.WillDelete && t.Exists).Select(t => t.Name).ToList();
        if (kept.Count > 0)
            parts.Add($"残す: {string.Join("、", kept)}。");
        if (plan.DeletesDb)
            parts.Add(DeletionViewModel.DbDataLossWarning);
        parts.Add(plan.DeletesResourceGroup ? $"リソースグループ {plan.ResourceGroup} も削除します（元に戻せません）。" : $"リソースグループ {plan.ResourceGroup} は残します。");
        return string.Join(" ", parts);
    }

    // 外していないものを、行ごとに削除のジョブとして登録する。始めるのは JobQueue（今すぐ、または予定の時刻）
    private Task RegisterAsync(ScheduleInput? schedule)
    {
        Error = null;
        if (schedule?.Error is { } scheduleError)
        {
            Error = scheduleError;
            return Task.CompletedTask;
        }
        var jobs = new List<JobViewModel>();
        var failed = new List<string>();
        foreach (var item in Items.Where(i => i.WillRun).ToList())
        {
            var ticket = item.Ticket!;
            var plan = ticket.Plan;
            var session = _target.CreateJobSessionFor(plan.Subscription);
            try
            {
                jobs.Add(_queue.Register(JobKind.Delete, new JobTarget(plan.Subscription, plan.ResourceGroup, plan.AppName), schedule?.At,
                    () => _start(ticket, plan.AppName, session), saved: SavedDeletion.From(plan, plan.AppName)));
            }
            catch (InvalidOperationException ex)
            {
                failed.Add($"{item.Title}: {ex.Message}");
            }
        }
        if (failed.Count > 0)
            Error = "登録できなかったもの:\n- " + string.Join("\n- ", failed);
        // 登録した計画は使ったものとして、画面から外す（次は計画からやり直す）
        _entries = [];
        ClearPlans();
        PlanCommand.RaiseCanExecuteChanged();
        _registered(jobs);
        return Task.CompletedTask;
    }

    private void ClearPlans()
    {
        Items.Clear();
        ConfirmCount = "";
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(RunnableCount));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.RaiseCanExecuteChanged();
        ScheduleCommand.RaiseCanExecuteChanged();
        RaiseBlocked();
    }

    private void RaiseBlocked()
    {
        OnPropertyChanged(nameof(BlockedText));
        OnPropertyChanged(nameof(IsBlocked));
    }
}
