using System.Collections.ObjectModel;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>計画した操作 1 件（画面の表の 1 行）。</summary>
public sealed record OperationRow(string Resource, string Name, string Action, bool DataLoss);

/// <summary>
/// デプロイの計画 → 確認 → 実行（MCP の plan_deployment / start_deployment と同じ安全策。詳細設計 7）。
/// ブロッカーがあれば実行できず、DB を更新する計画ではデータ消失への同意のチェックが無いと実行できない。
/// </summary>
public sealed class DeploymentViewModel : ObservableObject
{
    private readonly TimeProvider _time;
    private readonly DeploymentWorkflow _workflow;
    private readonly TargetViewModel _target;
    private readonly SettingsViewModel _settings;
    private readonly JobQueue _queue;
    private readonly Action<JobViewModel> _registered;
    private DeploymentPlanTicket? _ticket;
    private bool _dataLossAcknowledged;
    private string? _error;

    /// <param name="queue">ジョブの順番待ち（同じ対象のジョブがあれば、計画・登録しない）</param>
    /// <param name="registered">登録したジョブを画面に表示する</param>
    public DeploymentViewModel(
        DeploymentWorkflow workflow, TargetViewModel target, SettingsViewModel settings, JobQueue queue, Action<JobViewModel> registered, TimeProvider time)
    {
        _time = time;
        _workflow = workflow;
        _target = target;
        _settings = settings;
        _queue = queue;
        _registered = registered;
        Schedule = new ScheduleInput(time);
        PlanCommand = new AsyncCommand(() => Busy.RunAsync("計画を作っています", PlanAsync));
        StartCommand = new AsyncCommand(() => RegisterAsync(null), () => CanStart);
        ScheduleCommand = new AsyncCommand(() => RegisterAsync(Schedule), () => CanStart);
        // 対象や設定を変えたら、前の計画は使わない（計画と違う内容で実行しないように）
        _target.Changed += (_, _) => Ticket = null;
        _settings.PropertyChanged += (_, _) => Ticket = null;
    }

    public AsyncCommand PlanCommand { get; }

    /// <summary>計画の処理中の表示。</summary>
    public BusyState Busy { get; } = new();

    /// <summary>今すぐ実行する（同時に実行できる数に達していれば、待機中になる）。</summary>
    public AsyncCommand StartCommand { get; }

    /// <summary>予定に追加する（<see cref="Schedule"/> の日時に実行する）。</summary>
    public AsyncCommand ScheduleCommand { get; }

    /// <summary>「予定に追加」の日時。</summary>
    public ScheduleInput Schedule { get; }

    public ObservableCollection<OperationRow> Operations { get; } = [];

    public ObservableCollection<string> Blockers { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public bool HasPlan => _ticket != null;

    public string? PermissionText => _ticket?.Plan.PermissionCheck switch
    {
        null => null,
        { Status: PermissionCheckStatus.Ok } => "権限: 確認しました（不足はありません）",
        { Status: PermissionCheckStatus.Missing } p => $"権限: 不足しています（{string.Join(", ", p.MissingActions)}）",
        { } p => $"権限: 確認できませんでした（{p.Note}）",
    };

    public string? EstimatedDuration => _ticket?.EstimatedDuration;

    /// <summary>DB のデータを永続化するか（「今の状態を引き継ぐ」を選んだ場合は、既存の DB アプリから決めた結果）。</summary>
    public string? PersistDbText => _ticket?.Plan switch
    {
        null => null,
        { PersistDb: true } p => $"DB のデータ: Azure Files に永続化する（ストレージアカウント {ResourceNaming.StorageAccount(p.Options.Subscription, p.Options.ResourceGroup, p.Options.AppName)}）",
        _ => "DB のデータ: 永続化しない（DB コンテナの再起動でデータが失われる）",
    };

    public DateTimeOffset? ExpiresAt => _ticket?.Saved?.ExpiresAt;

    /// <summary>記録する利用可能期限（期限を過ぎると自動で削除する旨を添える）。</summary>
    public string? ExpiryText => _ticket?.Plan.Expiry switch
    {
        null => null,
        { ExpiresAt: null } e => $"利用可能期限: {e.Describe(_time.LocalTimeZone)}",
        { } e => $"利用可能期限: {e.Describe(_time.LocalTimeZone)}。期限の後に自動で削除します（DB のデータも消えます）。",
    };

    public bool RequiresDataLossAck => _ticket?.Plan.RequiresDataLossAck == true;

    /// <summary>「DB のデータが失われることを理解しました」のチェック。</summary>
    public bool DataLossAcknowledged
    {
        get => _dataLossAcknowledged;
        set
        {
            if (!SetProperty(ref _dataLossAcknowledged, value))
                return;
            StartCommand.RaiseCanExecuteChanged();
            ScheduleCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanStart => _ticket?.Saved != null && (!RequiresDataLossAck || DataLossAcknowledged);

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value, new Dependent(nameof(HasError)));
    }

    /// <summary>エラーを表示するか（画面の表示の切り替えに使う）。</summary>
    public bool HasError => Error != null;

    private DeploymentPlanTicket? Ticket
    {
        set
        {
            if (ReferenceEquals(_ticket, value))
                return;
            _ticket = value;
            DataLossAcknowledged = false;
            Operations.Clear();
            Blockers.Clear();
            Warnings.Clear();
            if (value != null)
            {
                foreach (var op in value.Plan.Operations)
                    Operations.Add(ToRow(op));
                foreach (var blocker in value.Plan.Blockers)
                    Blockers.Add(blocker);
                foreach (var warning in DeploymentAdvisor.GetWarnings(value.Plan.Options, ValidationTarget.Azure).Concat(value.Plan.Warnings))
                    Warnings.Add(warning);
            }
            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(PermissionText));
            OnPropertyChanged(nameof(EstimatedDuration));
            OnPropertyChanged(nameof(PersistDbText));
            OnPropertyChanged(nameof(ExpiresAt));
            OnPropertyChanged(nameof(ExpiryText));
            OnPropertyChanged(nameof(RequiresDataLossAck));
            OnPropertyChanged(nameof(CanStart));
            StartCommand.RaiseCanExecuteChanged();
            ScheduleCommand.RaiseCanExecuteChanged();
        }
    }

    // 読み取りだけで計画を作る。設定に誤りがあれば Azure は読まない
    private async Task PlanAsync()
    {
        Error = null;
        Ticket = null;
        var input = _settings.Build(ValidationTarget.Azure);
        if (input.Options == null)
        {
            Error = "設定に誤りがあります:\n- " + string.Join("\n- ", input.Errors);
            return;
        }
        if (_queue.ActiveFor(input.Options.Subscription, input.Options.ResourceGroup, input.Options.AppName) is { } active)
        {
            Error = JobQueue.ActiveMessage(active);
            return;
        }
        try
        {
            Ticket = await _workflow.PlanAsync(_target.CreateSession(Busy), input.Options);
        }
        catch (Exception ex)
        {
            Error = DesktopMessages.ForAzure(ex);
        }
    }

    // 計画を確かめたジョブを登録する。始めるのは JobQueue（今すぐ、または予定の時刻）
    private Task RegisterAsync(ScheduleInput? schedule)
    {
        Error = null;
        if (schedule?.Error is { } scheduleError)
        {
            Error = scheduleError;
            return Task.CompletedTask;
        }
        var ticket = _ticket!;
        var acknowledged = DataLossAcknowledged;
        var session = _target.CreateJobSession();
        var options = ticket.Plan.Options;
        try
        {
            _registered(_queue.Register(JobKind.Deploy, new JobTarget(options.Subscription, options.ResourceGroup, options.AppName), schedule?.At,
                () => StartPlannedAsync(ticket, acknowledged, session)));
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
    /// 確かめた計画でジョブを始める。計画の有効期限（15 分）が切れていれば計画し直し、確かめた計画と同じ操作で、データ消失の同意が
    /// 新たに要らない場合だけ始める（予定・待機中のジョブのため。詳細設計 desktop-jobs.md の 4.2）。
    /// </summary>
    internal async Task<Job> StartPlannedAsync(DeploymentPlanTicket ticket, bool acknowledged, IAzureSession session)
    {
        try
        {
            return await _workflow.StartAsync(ticket.Saved!.PlanId, acknowledged, _ => session);
        }
        catch (WorkflowException ex) when (ex.Failure == WorkflowFailure.PlanExpired)
        {
            var fresh = await _workflow.PlanAsync(session, ticket.Plan.Options);
            if (!fresh.Plan.CanRun)
                throw new WorkflowException(WorkflowFailure.Blocked, fresh.Plan.Blockers);
            if (!ticket.Plan.HasSameOperations(fresh.Plan) || (fresh.Plan.RequiresDataLossAck && !acknowledged))
                throw new WorkflowException(WorkflowFailure.Changed);
            return await _workflow.StartAsync(fresh.Saved!.PlanId, acknowledged, _ => session);
        }
    }

    internal static OperationRow ToRow(PlannedOperation op) => new(
        op.Kind switch
        {
            PlannedResourceKind.ResourceGroup => "リソースグループ",
            PlannedResourceKind.Environment => "Container Apps Environment",
            PlannedResourceKind.StorageAccount => "ストレージアカウント（DB のデータ）",
            PlannedResourceKind.FileShare => "ファイル共有（DB のデータ）",
            PlannedResourceKind.EnvironmentStorage => "Environment のストレージ",
            _ => "Container App",
        },
        op.Name,
        op.Action switch
        {
            PlannedAction.Create => "作成する",
            PlannedAction.UseExisting => "既存のものを使う（変更しない）",
            _ => op.DataLoss ? "更新する（DB のデータが失われるおそれがあります）" : "更新する",
        },
        op.DataLoss);
}
