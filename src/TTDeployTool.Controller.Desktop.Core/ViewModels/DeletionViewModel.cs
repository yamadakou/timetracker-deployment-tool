using System.Collections.ObjectModel;
using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>削除の計画の対象 1 件（画面の表の 1 行）。</summary>
public sealed record DeletionRow(string Name, string Resource, string Decision, string? Reason, bool WillDelete);

/// <summary>
/// 削除の計画 → アプリ名の入力 → 実行（MCP の plan_deletion / start_deletion と同じ安全策。詳細設計 7）。
/// リソースグループは削除しない。計画のアプリ名と同じ名前を入力するまで実行できない。
/// </summary>
public sealed class DeletionViewModel : ObservableObject
{
    public const string DbDataLossWarning = "DB アプリを削除すると DB のデータはすべて失われます。";

    public const string UnmanagedWarning =
        "管理タグが無いため削除しないリソースがあります。本ツールで作ったものであれば、「管理タグの無いものも対象にする」で対象にできます。";

    public const string KeptReason = "「DB のデータを残す」の指定により残します";

    private readonly DeletionWorkflow _workflow;
    private readonly TargetViewModel _target;
    private readonly JobQueue _queue;
    private readonly Action<JobViewModel> _registered;
    private DeletionPlanTicket? _ticket;
    private bool _includeUntagged;
    private bool _keepDbData;
    private bool _deleteResourceGroup;
    private string _confirmAppName = "";
    private string? _error;
    private string? _note;
    private string? _message;
    private readonly IClipboardService? _clipboard;

    /// <param name="queue">ジョブの順番待ち（同じ対象のジョブがあれば、計画・登録しない）</param>
    /// <param name="registered">登録したジョブを画面に表示する</param>
    /// <param name="clipboard">計画とアプリ名のコピー先（省略時はコピーできない）</param>
    public DeletionViewModel(
        DeletionWorkflow workflow, TargetViewModel target, JobQueue queue, Action<JobViewModel> registered, TimeProvider time, IClipboardService? clipboard = null)
    {
        _clipboard = clipboard;
        CopyPlanCommand = new AsyncCommand(() => Copy(ToMarkdown()!, "削除の計画を Markdown でコピーしました。"), () => _clipboard != null && HasPlan);
        CopyAppNameCommand = new AsyncCommand(
            () => Copy(AppNameToConfirm!, $"アプリ名 {AppNameToConfirm} をコピーしました。"), () => _clipboard != null && AppNameToConfirm != null);
        _workflow = workflow;
        _target = target;
        _queue = queue;
        _registered = registered;
        Schedule = new ScheduleInput(time);
        // 予定の日時を変えたら、押せない理由を出し直す
        Schedule.PropertyChanged += (_, _) => RaiseBlocked();
        PlanCommand = new AsyncCommand(() => Busy.RunAsync("計画を作っています", PlanAsync));
        StartCommand = new AsyncCommand(() => RegisterAsync(null), () => CanStart);
        ScheduleCommand = new AsyncCommand(() => RegisterAsync(Schedule), () => CanStart);
        _target.Changed += (_, _) => Ticket = null;
    }

    public AsyncCommand PlanCommand { get; }

    /// <summary>計画を Markdown でコピーする。</summary>
    public AsyncCommand CopyPlanCommand { get; }

    /// <summary>確認に入力するアプリ名をコピーする（Azure portal の削除の確認と同じ）。</summary>
    public AsyncCommand CopyAppNameCommand { get; }

    /// <summary>コピーの結果。</summary>
    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>計画の Markdown（計画が無ければ null）。</summary>
    public string? ToMarkdown() => _ticket == null
        ? null
        : MarkdownText.Document($"TTDeployTool の削除の計画（{_ticket.Plan.ResourceGroup} / {_ticket.Plan.AppName}）",
            (null, MarkdownText.Table(["操作", "名前", "種類", "理由"], Targets.Select(t => new[] { t.Decision, t.Name, t.Resource, t.Reason }))),
            ("実行できない理由", MarkdownText.Bullets(Blockers)),
            ("警告", MarkdownText.Bullets(Warnings)),
            ("残すもの", KeptText),
            ("注意", Note));

    /// <summary>計画の処理中の表示。</summary>
    public BusyState Busy { get; } = new();

    /// <summary>今すぐ削除する（同時に実行できる数に達していれば、待機中になる）。</summary>
    public AsyncCommand StartCommand { get; }

    /// <summary>予定に追加する（<see cref="Schedule"/> の日時に削除する）。</summary>
    public AsyncCommand ScheduleCommand { get; }

    /// <summary>「予定に追加」の日時。</summary>
    public ScheduleInput Schedule { get; }

    public ObservableCollection<DeletionRow> Targets { get; } = [];

    public ObservableCollection<string> Blockers { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>管理タグの無いリソース（本ツールの以前の版で作ったものなど）も対象にするか。</summary>
    public bool IncludeUntagged
    {
        get => _includeUntagged;
        set
        {
            if (SetProperty(ref _includeUntagged, value))
                Ticket = null;
        }
    }

    /// <summary>DB のデータを置いたストレージアカウントを残すか（残すと料金がかかり続けるが、同じアプリ名で永続化してデプロイするとデータを引き継げる）。</summary>
    public bool KeepDbData
    {
        get => _keepDbData;
        set
        {
            if (SetProperty(ref _keepDbData, value))
                Ticket = null;
        }
    }

    /// <summary>リソースグループも削除するか（本ツールのリソースを削除した後に空になる場合だけ削除する）。</summary>
    public bool DeleteResourceGroup
    {
        get => _deleteResourceGroup;
        set
        {
            if (SetProperty(ref _deleteResourceGroup, value))
                Ticket = null;
        }
    }

    /// <summary>確認のためのアプリ名の入力。</summary>
    public string ConfirmAppName
    {
        get => _confirmAppName;
        set
        {
            if (SetProperty(ref _confirmAppName, value))
            {
                OnPropertyChanged(nameof(CanStart));
                StartCommand.RaiseCanExecuteChanged();
                ScheduleCommand.RaiseCanExecuteChanged();
                RaiseBlocked();
            }
        }
    }

    public bool HasPlan => _ticket != null;

    /// <summary>入力してもらうアプリ名（計画のアプリ名）。</summary>
    public string? AppNameToConfirm => _ticket?.CanRun == true ? _ticket.Plan.AppName : null;

    /// <summary>アプリ名の入力欄を表示するか。</summary>
    public bool NeedsAppNameConfirmation => AppNameToConfirm != null;

    public string? KeptText => _ticket switch
    {
        null => null,
        { Plan.KeepsDbData: true } t => $"{KeptResourceGroup(t.Plan)}\n{KeptDbDataNotice(t.Plan)}",
        { Plan.DeletesResourceGroup: true } => null,
        { } t => KeptResourceGroup(t.Plan),
    };

    /// <summary>
    /// 「今すぐ削除」「予定に追加」が押せない理由（押せれば null。No.74）。アプリ名が入っていなければその旨を、予定の日時が過去ならその旨を出す。
    /// </summary>
    public string? BlockedText => AppNameToConfirm == null
        ? null
        : !CanStart
            ? $"アプリ名「{AppNameToConfirm}」を入力すると、「今すぐ削除」と「予定に追加」が押せます。"
            : Schedule.Error is { } error ? $"「予定に追加」: {error}" : null;

    /// <summary>押せない理由を出すか。</summary>
    public bool IsBlocked => BlockedText != null;

    public bool CanStart => AppNameToConfirm != null && ConfirmAppName == AppNameToConfirm;

    /// <summary>削除するものが無い場合などの案内。</summary>
    public string? Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value, new Dependent(nameof(HasError)));
    }

    /// <summary>エラーを表示するか（画面の表示の切り替えに使う）。</summary>
    public bool HasError => Error != null;

    private DeletionPlanTicket? Ticket
    {
        set
        {
            if (ReferenceEquals(_ticket, value))
                return;
            _ticket = value;
            ConfirmAppName = "";
            Targets.Clear();
            Blockers.Clear();
            Warnings.Clear();
            Note = null;
            if (value != null)
            {
                var plan = value.Plan;
                foreach (var t in plan.Targets)
                    Targets.Add(new DeletionRow(
                        t.Name,
                        t.Kind switch
                        {
                            DeletionResourceKind.ContainerApp => "Container App",
                            DeletionResourceKind.Environment => "Container Apps Environment",
                            _ => "ストレージアカウント（DB のデータ）",
                        },
                        t.WillDelete ? "削除する" : "削除しない",
                        t.Reason == DeletionPlanner.KeptReason ? KeptReason : t.Reason,
                        t.WillDelete));
                if (plan.ResourceGroupTarget is { } rg)
                    Targets.Add(new DeletionRow(plan.ResourceGroup, "リソースグループ", rg.WillDelete ? "削除する" : "削除しない", rg.Reason, rg.WillDelete));
                foreach (var blocker in value.Blockers)
                    Blockers.Add(blocker.Replace("リソースグループの削除の指定を外して", "「リソースグループも削除する」のチェックを外して"));
                if (plan.DeletesResourceGroup)
                    Warnings.Add($"リソースグループ {plan.ResourceGroup} も削除します。元に戻せません。");
                if (plan.LosesDbData)
                    Warnings.Add(DbDataLossWarning);
                if (plan.DeletesDbData)
                    Warnings.Add($"ストレージアカウント {plan.StorageAccountName}（Azure Files に永続化した DB のデータ）も削除します。元に戻せません（残すには「DB のデータを残す」にチェックを入れてください）。");
                if (plan.HasUnmanaged)
                    Warnings.Add(UnmanagedWarning);
                if (plan.PermissionCheck.Status == PermissionCheckStatus.NotChecked)
                    Warnings.Add($"権限を事前に確認できませんでした: {plan.PermissionCheck.Note}");
                if (!plan.HasDeletions)
                    Note = "削除するリソースはありません。";
            }
            Message = null;
            CopyPlanCommand.RaiseCanExecuteChanged();
            CopyAppNameCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(AppNameToConfirm));
            OnPropertyChanged(nameof(NeedsAppNameConfirmation));
            OnPropertyChanged(nameof(KeptText));
            OnPropertyChanged(nameof(CanStart));
            StartCommand.RaiseCanExecuteChanged();
            ScheduleCommand.RaiseCanExecuteChanged();
            RaiseBlocked();
        }
    }

    private static string KeptResourceGroup(DeletionPlan plan) =>
        $"リソースグループ {plan.ResourceGroup} は削除しません（不要であれば「リソースグループも削除する」にチェックを入れて計画し直してください）。";

    // 完了のログに添える案内（メソッドのグループを何度も渡すと、そのたびに委譲の作成の確かめが入るため、1 つにする）
    internal static readonly Func<DeletionPlan, string> KeptNotice = KeptDbDataNotice;

    private static string KeptDbDataNotice(DeletionPlan plan) =>
        $"ストレージアカウント {plan.StorageAccountName}（DB のデータ）は残します。料金がかかり続けます。同じアプリ名で「Azure Files に永続化する」を選んで" +
        "デプロイすると、データを引き継ぎます。不要になったら「DB のデータを残す」のチェックを外して削除してください。";

    // 読み取りだけで計画を作る。対象に誤りがあれば Azure は読まない
    private async Task PlanAsync()
    {
        Error = null;
        Ticket = null;
        if (_target.TargetError is { } targetError)
        {
            Error = targetError;
            return;
        }
        if (_queue.ActiveFor(_target.Subscription, _target.ResourceGroup, _target.AppName.ToLowerInvariant()) is { } active)
        {
            Error = JobQueue.ActiveMessage(active);
            return;
        }
        try
        {
            Ticket = await _workflow.PlanAsync(
                _target.CreateSession(Busy), _target.Subscription, _target.ResourceGroup, _target.AppName.ToLowerInvariant(), IncludeUntagged, KeepDbData,
                deleteResourceGroup: DeleteResourceGroup);
        }
        catch (Exception ex)
        {
            Error = DesktopMessages.ForAzure(ex);
        }
    }

    // アプリ名を確かめた計画を登録する。始めるのは JobQueue（今すぐ、または予定の時刻）
    private Task RegisterAsync(ScheduleInput? schedule)
    {
        Error = null;
        if (schedule?.Error is { } scheduleError)
        {
            Error = scheduleError;
            return Task.CompletedTask;
        }
        var ticket = _ticket!;
        var confirmed = ConfirmAppName;
        var session = _target.CreateJobSession();
        var plan = ticket.Plan;
        try
        {
            _registered(_queue.Register(JobKind.Delete, new JobTarget(plan.Subscription, plan.ResourceGroup, plan.AppName), schedule?.At,
                () => StartPlannedAsync(ticket, confirmed, session), saved: SavedDeletion.From(plan, confirmed)));
            // 登録した計画は使ったものとして、画面から外す（次は計画からやり直す）
            Ticket = null;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 確かめた計画で削除のジョブを始める。計画の有効期限（15 分）が切れていれば計画し直し、対象ごとの「削除するか」が確かめた計画と
    /// 同じ場合だけ始める（予定・待機中のジョブのため。詳細設計 desktop-jobs.md の 4.2）。
    /// </summary>
    internal async Task<Job> StartPlannedAsync(DeletionPlanTicket ticket, string confirmedAppName, IAzureSession session)
    {
        try
        {
            return await _workflow.StartAsync(ticket.Saved!.PlanId, confirmedAppName, _ => session, KeptNotice);
        }
        catch (WorkflowException ex) when (ex.Failure == WorkflowFailure.PlanExpired)
        {
            var plan = ticket.Plan;
            var fresh = await _workflow.PlanAsync(session, plan.Subscription, plan.ResourceGroup, plan.AppName, plan.IncludeUntagged, plan.KeepDbData,
                deleteResourceGroup: plan.DeleteResourceGroupRequested);
            if (!fresh.CanRun)
                throw new WorkflowException(WorkflowFailure.Blocked, fresh.Blockers);
            if (!plan.HasSameDeletions(fresh.Plan))
                throw new WorkflowException(WorkflowFailure.Changed);
            return await _workflow.StartAsync(fresh.Saved!.PlanId, confirmedAppName, _ => session, KeptNotice);
        }
    }

    /// <summary>
    /// 前回のアプリで保存した削除の予定を始める（docs/design/desktop-jobs.md の 6.6）。計画を作り直し、確かめた計画と同じものを削除する場合だけ始める。
    /// </summary>
    internal async Task<Job> StartSavedAsync(JobTarget target, SavedDeletion saved, IAzureSession session)
    {
        var fresh = await _workflow.PlanAsync(session, target.Subscription, target.ResourceGroup, target.AppName, saved.IncludeUntagged, saved.KeepDbData,
            deleteResourceGroup: saved.DeleteResourceGroup);
        if (fresh.Blockers.Count > 0)
            throw new WorkflowException(WorkflowFailure.Blocked, fresh.Blockers);
        if (!fresh.CanRun)
            throw new DeletionSkippedException("削除するものはありませんでした（既に削除されています）。");
        if (!saved.Matches(fresh.Plan))
            throw new WorkflowException(WorkflowFailure.Changed);
        return await _workflow.StartAsync(fresh.Saved!.PlanId, saved.ConfirmedAppName, _ => session, KeptNotice);
    }

    private Task Copy(string text, string message)
    {
        _clipboard!.CopyText(text);
        Message = message;
        return Task.CompletedTask;
    }

    private void RaiseBlocked()
    {
        OnPropertyChanged(nameof(BlockedText));
        OnPropertyChanged(nameof(IsBlocked));
    }
}
