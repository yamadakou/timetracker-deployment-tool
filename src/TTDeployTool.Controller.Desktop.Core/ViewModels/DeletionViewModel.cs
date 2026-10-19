using System.Collections.ObjectModel;
using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Mvvm;

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
    private readonly Func<JobViewModel, Task> _watch;
    private DeletionPlanTicket? _ticket;
    private bool _includeUntagged;
    private bool _keepDbData;
    private string _confirmAppName = "";
    private string? _error;
    private string? _note;

    /// <param name="watch">始めたジョブを画面に表示し、終わるまで状態を読み直す</param>
    public DeletionViewModel(DeletionWorkflow workflow, TargetViewModel target, Func<JobViewModel, Task> watch)
    {
        _workflow = workflow;
        _target = target;
        _watch = watch;
        PlanCommand = new AsyncCommand(PlanAsync);
        StartCommand = new AsyncCommand(StartAsync, () => CanStart);
        _target.Changed += (_, _) => Ticket = null;
    }

    public AsyncCommand PlanCommand { get; }

    public AsyncCommand StartCommand { get; }

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
            }
        }
    }

    public bool HasPlan => _ticket != null;

    /// <summary>入力してもらうアプリ名（計画のアプリ名）。</summary>
    public string? AppNameToConfirm => _ticket?.CanRun == true ? _ticket.Plan.AppName : null;

    public string? KeptText => _ticket switch
    {
        null => null,
        { Plan.KeepsDbData: true } t => $"{KeptResourceGroup(t.Plan)}\n{KeptDbDataNotice(t.Plan)}",
        { } t => KeptResourceGroup(t.Plan),
    };

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
        private set => SetProperty(ref _error, value);
    }

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
                foreach (var blocker in value.Blockers)
                    Blockers.Add(blocker);
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
            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(AppNameToConfirm));
            OnPropertyChanged(nameof(KeptText));
            OnPropertyChanged(nameof(CanStart));
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    private static string KeptResourceGroup(DeletionPlan plan) =>
        $"リソースグループ {plan.ResourceGroup} は削除しません（不要であれば Azure ポータルや az group delete で削除してください）。";

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
        try
        {
            Ticket = await _workflow.PlanAsync(
                _target.CreateSession(), _target.Subscription, _target.ResourceGroup, _target.AppName.ToLowerInvariant(), IncludeUntagged, KeepDbData);
        }
        catch (Exception ex)
        {
            Error = DesktopMessages.ForAzure(ex);
        }
    }

    private async Task StartAsync()
    {
        Error = null;
        JobViewModel job;
        try
        {
            job = new JobViewModel(await _workflow.StartAsync(_ticket!.Saved!.PlanId, ConfirmAppName, _ => _target.CreateSession(), KeptDbDataNotice));
        }
        catch (Exception ex)
        {
            Error = DesktopMessages.ForAzure(ex);
            return;
        }
        finally
        {
            // 実行しなかった場合も計画は使用済みになるため、計画からやり直してもらう（アプリ名の不一致は、一致するまで押せないので起きない）
            Ticket = null;
        }
        await _watch(job);
    }
}
