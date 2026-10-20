using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Diagnostics;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// アプリの画面全体。CLI・MCP と同じアプリケーション層（計画ストア・ジョブ・ワークフロー）をプロセス内に持つ
/// （利用者は 1 人なので呼び出し元は固定。計画とジョブはアプリを終了すると消える）。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>予定の時刻を確かめる間隔。</summary>
    public static readonly TimeSpan ScheduleCheckInterval = TimeSpan.FromSeconds(15);

    private readonly IDialogService _dialogs;
    private JobViewModel? _currentJob;
    private JobViewModel? _currentDeployment;
    private JobViewModel? _currentDeletion;

    public MainViewModel(
        ISignInService signIn, IAzureClients clients, IDialogService dialogs, ISystemLauncher launcher, TimeProvider time, ILoggerFactory loggers,
        TimeSpan? pollInterval = null, IAppPreferences? preferences = null)
    {
        _dialogs = dialogs;
        Queue = new JobQueue(time, pollInterval ?? JobViewModel.DefaultPollInterval, ScheduleCheckInterval,
            preferences ?? new InMemoryAppPreferences(), loggers.CreateLogger("TTDeployTool.Desktop.Jobs"));
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.HasActive))
                OnPropertyChanged(nameof(HasRunningJob));
        };
        var logger = new DesktopDeploymentLogger(loggers.CreateLogger("TTDeployTool.Desktop"));
        var plans = new InMemoryPlanStore(time);
        var jobs = new JobManager(new InMemoryJobStore(), time, loggers.CreateLogger<JobManager>());
        var caller = new LocalCallerContext();

        Target = new TargetViewModel(signIn, clients, loggers.CreateLogger("TTDeployTool.Desktop.Target"));
        SignIn = new SignInViewModel(signIn, async signedIn =>
        {
            if (signedIn)
                await Target.LoadAsync();
            else
                Target.Clear();
        }, loggers.CreateLogger("TTDeployTool.Desktop.SignIn"));
        Settings = new SettingsViewModel(Target);
        Deployment = new DeploymentViewModel(new DeploymentWorkflow(plans, jobs, caller, logger), Target, Settings, Queue, Show, time);
        Deletion = new DeletionViewModel(new DeletionWorkflow(plans, jobs, caller, logger, time), Target, Queue, Show, time);
        Status = new StatusViewModel(Target, logger,
            () => Queue.ActiveFor(Target.Subscription, Target.ResourceGroup, Target.AppName.ToLowerInvariant())?.IsRunning == true);
        DryRun = new DryRunViewModel(Settings, dialogs, launcher);
        LogDirectory = (loggers as DiagnosticLog)?.Directory;
        OpenLogFolderCommand = new AsyncCommand(() =>
        {
            launcher.OpenFolder(LogDirectory!);
            return Task.CompletedTask;
        }, () => LogDirectory != null);
        OpenEndpointCommand = new AsyncCommand(() =>
        {
            launcher.OpenUri(CurrentDeployment!.Endpoint!);
            return Task.CompletedTask;
        }, () => CurrentDeployment?.Endpoint != null);
        ClearFinishedCommand = new AsyncCommand(() =>
        {
            Queue.ClearFinished();
            return Task.CompletedTask;
        });
    }

    public SignInViewModel SignIn { get; }

    public TargetViewModel Target { get; }

    public SettingsViewModel Settings { get; }

    public DeploymentViewModel Deployment { get; }

    public DeletionViewModel Deletion { get; }

    public StatusViewModel Status { get; }

    public DryRunViewModel DryRun { get; }

    /// <summary>診断用のログのフォルダー（ファイルに書かない場合は null）。</summary>
    public string? LogDirectory { get; }

    /// <summary>診断用のログのフォルダーを開く（問い合わせのときに、ファイルを添付してもらう）。</summary>
    public AsyncCommand OpenLogFolderCommand { get; }

    /// <summary>デプロイが成功した後に、Timetracker を既定のブラウザーで開く。</summary>
    public AsyncCommand OpenEndpointCommand { get; }

    /// <summary>終わったジョブを一覧から消す。</summary>
    public AsyncCommand ClearFinishedCommand { get; }

    /// <summary>ジョブの順番待ち・予定・同時実行の上限。</summary>
    public JobQueue Queue { get; }

    /// <summary>このアプリで登録したジョブ（新しい順）。</summary>
    public ObservableCollection<JobViewModel> Jobs => Queue.Jobs;

    /// <summary>最後に始めたジョブ。</summary>
    public JobViewModel? CurrentJob
    {
        get => _currentJob;
        private set => SetProperty(ref _currentJob, value);
    }

    /// <summary>最後に始めたデプロイのジョブ（デプロイの画面に表示する）。</summary>
    public JobViewModel? CurrentDeployment
    {
        get => _currentDeployment;
        private set => SetProperty(ref _currentDeployment, value);
    }

    /// <summary>最後に始めた削除のジョブ（削除の画面に表示する）。</summary>
    public JobViewModel? CurrentDeletion
    {
        get => _currentDeletion;
        private set => SetProperty(ref _currentDeletion, value);
    }

    /// <summary>終わっていないジョブ（予定・待機中・一時停止を含む）があるか。</summary>
    public bool HasRunningJob => Queue.HasActive;

    /// <summary>起動時の処理（前回のサインインを復元し、サブスクリプションを読む）。</summary>
    public Task InitializeAsync() => SignIn.RestoreAsync();

    /// <summary>ウィンドウを閉じてよいか。終わっていないジョブがあれば確認する（閉じると実行中のジョブは止まり、予定・待機中のジョブは消える）。</summary>
    public async Task<bool> ConfirmCloseAsync() =>
        !HasRunningJob || await _dialogs.ConfirmAsync(
            "終わっていないジョブがあります",
            $"ジョブ（{Queue.Summary}）があります。閉じると実行中の処理が止まり、途中まで作成・削除したリソースが残ります" +
            "（もう一度実行すると続きから処理します）。予定・待機中のジョブは消えます。閉じますか？",
            "閉じる");

    public void Dispose() => Queue.Dispose();

    // 登録したジョブを、デプロイ・削除の画面に表示する
    private void Show(JobViewModel job)
    {
        CurrentJob = job;
        if (job.IsDeployment)
        {
            CurrentDeployment = job;
        }
        else
        {
            CurrentDeletion = job;
            // 同じ対象の削除を始めたら、デプロイの結果（URL）は使えなくなるため、デプロイの画面から外す
            if (CurrentDeployment?.IsSameTarget(job) == true)
                CurrentDeployment = null;
        }
        // デプロイが終わって URL が分かったら、「Timetracker を開く」を押せるようにする
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobViewModel.Endpoint))
                OpenEndpointCommand.RaiseCanExecuteChanged();
        };
        OpenEndpointCommand.RaiseCanExecuteChanged();
    }
}
