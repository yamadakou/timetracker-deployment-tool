using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// アプリの画面全体。CLI・MCP と同じアプリケーション層（計画ストア・ジョブ・ワークフロー）をプロセス内に持つ
/// （利用者は 1 人なので呼び出し元は固定。計画とジョブはアプリを終了すると消える）。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private JobViewModel? _currentJob;

    public MainViewModel(
        ISignInService signIn, IAzureClients clients, IDialogService dialogs, ISystemLauncher launcher, TimeProvider time, ILoggerFactory loggers,
        TimeSpan? pollInterval = null)
    {
        _dialogs = dialogs;
        _time = time;
        _pollInterval = pollInterval ?? JobViewModel.DefaultPollInterval;
        var logger = new DesktopDeploymentLogger(loggers.CreateLogger("TTDeployTool.Desktop"));
        var plans = new InMemoryPlanStore(time);
        var jobs = new JobManager(new InMemoryJobStore(), time, loggers.CreateLogger<JobManager>());
        var caller = new LocalCallerContext();

        Target = new TargetViewModel(signIn, clients);
        SignIn = new SignInViewModel(signIn, async signedIn =>
        {
            if (signedIn)
                await Target.LoadAsync();
            else
                Target.Clear();
        });
        Settings = new SettingsViewModel(Target);
        Deployment = new DeploymentViewModel(new DeploymentWorkflow(plans, jobs, caller, logger), Target, Settings, WatchAsync);
        Deletion = new DeletionViewModel(new DeletionWorkflow(plans, jobs, caller, logger), Target, WatchAsync);
        Status = new StatusViewModel(Target, logger);
        DryRun = new DryRunViewModel(Settings, dialogs, launcher);
        OpenEndpointCommand = new AsyncCommand(() =>
        {
            launcher.OpenUri(CurrentJob!.Endpoint!);
            return Task.CompletedTask;
        }, () => CurrentJob?.Endpoint != null);
    }

    public SignInViewModel SignIn { get; }

    public TargetViewModel Target { get; }

    public SettingsViewModel Settings { get; }

    public DeploymentViewModel Deployment { get; }

    public DeletionViewModel Deletion { get; }

    public StatusViewModel Status { get; }

    public DryRunViewModel DryRun { get; }

    /// <summary>デプロイが成功した後に、Timetracker を既定のブラウザーで開く。</summary>
    public AsyncCommand OpenEndpointCommand { get; }

    /// <summary>このアプリで実行したジョブ（新しい順）。</summary>
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    /// <summary>最後に始めたジョブ（実行の画面に表示する）。</summary>
    public JobViewModel? CurrentJob
    {
        get => _currentJob;
        private set => SetProperty(ref _currentJob, value);
    }

    public bool HasRunningJob => Jobs.Any(j => j.IsRunning);

    /// <summary>起動時の処理（前回のサインインを復元し、サブスクリプションを読む）。</summary>
    public Task InitializeAsync() => SignIn.RestoreAsync();

    /// <summary>ウィンドウを閉じてよいか。実行中のジョブがあれば確認する（閉じるとジョブは止まる）。</summary>
    public async Task<bool> ConfirmCloseAsync() =>
        !HasRunningJob || await _dialogs.ConfirmAsync(
            "実行中の処理があります",
            "デプロイまたは削除を実行中です。閉じると処理が止まり、途中まで作成・削除したリソースが残ります" +
            "（もう一度実行すると続きから処理します）。閉じますか？",
            "閉じる");

    private async Task WatchAsync(JobViewModel job)
    {
        CurrentJob = job;
        Jobs.Insert(0, job);
        OnPropertyChanged(nameof(HasRunningJob));
        await job.WatchAsync(_time, _pollInterval);
        OnPropertyChanged(nameof(HasRunningJob));
        OpenEndpointCommand.RaiseCanExecuteChanged();
    }
}
