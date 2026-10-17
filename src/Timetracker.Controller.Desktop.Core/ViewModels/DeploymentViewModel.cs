using System.Collections.ObjectModel;
using Timetracker.Application.Deployment;
using Timetracker.Application.Permissions;
using Timetracker.Application.Workflows;
using Timetracker.Controller.Desktop.Core.Mvvm;
using Timetracker.Domain.Deployment;

namespace Timetracker.Controller.Desktop.Core.ViewModels;

/// <summary>計画した操作 1 件（画面の表の 1 行）。</summary>
public sealed record OperationRow(string Resource, string Name, string Action, bool DataLoss);

/// <summary>
/// デプロイの計画 → 確認 → 実行（MCP の plan_deployment / start_deployment と同じ安全策。詳細設計 7）。
/// ブロッカーがあれば実行できず、DB を更新する計画ではデータ消失への同意のチェックが無いと実行できない。
/// </summary>
public sealed class DeploymentViewModel : ObservableObject
{
    private readonly DeploymentWorkflow _workflow;
    private readonly TargetViewModel _target;
    private readonly SettingsViewModel _settings;
    private readonly Func<JobViewModel, Task> _watch;
    private DeploymentPlanTicket? _ticket;
    private bool _dataLossAcknowledged;
    private string? _error;

    /// <param name="watch">始めたジョブを画面に表示し、終わるまで状態を読み直す</param>
    public DeploymentViewModel(DeploymentWorkflow workflow, TargetViewModel target, SettingsViewModel settings, Func<JobViewModel, Task> watch)
    {
        _workflow = workflow;
        _target = target;
        _settings = settings;
        _watch = watch;
        PlanCommand = new AsyncCommand(PlanAsync);
        StartCommand = new AsyncCommand(StartAsync, () => CanStart);
        // 対象や設定を変えたら、前の計画は使わない（計画と違う内容で実行しないように）
        _target.Changed += (_, _) => Ticket = null;
        _settings.PropertyChanged += (_, _) => Ticket = null;
    }

    public AsyncCommand PlanCommand { get; }

    public AsyncCommand StartCommand { get; }

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

    public DateTimeOffset? ExpiresAt => _ticket?.Saved?.ExpiresAt;

    public bool RequiresDataLossAck => _ticket?.Plan.RequiresDataLossAck == true;

    /// <summary>「DB のデータが失われることを理解しました」のチェック。</summary>
    public bool DataLossAcknowledged
    {
        get => _dataLossAcknowledged;
        set
        {
            if (SetProperty(ref _dataLossAcknowledged, value))
                StartCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanStart => _ticket?.Saved != null && (!RequiresDataLossAck || DataLossAcknowledged);

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

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
            OnPropertyChanged(nameof(ExpiresAt));
            OnPropertyChanged(nameof(RequiresDataLossAck));
            OnPropertyChanged(nameof(CanStart));
            StartCommand.RaiseCanExecuteChanged();
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
        try
        {
            Ticket = await _workflow.PlanAsync(_target.CreateSession(), input.Options);
        }
        catch (Exception ex)
        {
            Error = DesktopMessages.ForAzure(ex);
        }
    }

    private async Task StartAsync()
    {
        Error = null;
        var planId = _ticket!.Saved!.PlanId;
        JobViewModel job;
        try
        {
            job = new JobViewModel(await _workflow.StartAsync(planId, DataLossAcknowledged, _ => _target.CreateSession()));
        }
        catch (Exception ex)
        {
            Error = DesktopMessages.ForAzure(ex);
            return;
        }
        finally
        {
            // 実行しなかった場合も計画は使用済みになるため、計画からやり直してもらう（同意の不足は、チェックが無いと押せないので起きない）
            Ticket = null;
        }
        await _watch(job);
    }

    private static OperationRow ToRow(PlannedOperation op) => new(
        op.Kind switch
        {
            PlannedResourceKind.ResourceGroup => "リソースグループ",
            PlannedResourceKind.Environment => "Container Apps Environment",
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
