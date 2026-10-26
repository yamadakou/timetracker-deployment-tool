using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Diagnostics;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>ウィンドウを閉じるときの動き。</summary>
public enum CloseAction
{
    /// <summary>アプリを終了する。</summary>
    Exit,

    /// <summary>ウィンドウを隠し、通知領域に残る。</summary>
    Hide,
}

/// <summary>
/// アプリの画面全体。CLI・MCP と同じアプリケーション層（計画ストア・ジョブ・ワークフロー）をプロセス内に持つ
/// （利用者は 1 人なので呼び出し元は固定。計画とジョブはアプリを終了すると消える）。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>予定の時刻を確かめる間隔。</summary>
    public static readonly TimeSpan ScheduleCheckInterval = TimeSpan.FromSeconds(15);

    private readonly IDialogService _dialogs;
    private readonly INotificationService? _notifications;
    private bool _hiddenOnce;
    private JobViewModel? _currentJob;
    private JobViewModel? _currentDeployment;
    private JobViewModel? _currentDeletion;
    private JobViewModel? _statusJob;

    public MainViewModel(
        ISignInService signIn, IAzureClients clients, IDialogService dialogs, ISystemLauncher launcher, TimeProvider time, ILoggerFactory loggers,
        TimeSpan? pollInterval = null, IAppPreferences? preferences = null, INotificationService? notifications = null, IClipboardService? clipboard = null,
        IStartupTaskService? startupTask = null)
    {
        _dialogs = dialogs;
        _notifications = notifications;
        preferences ??= new InMemoryAppPreferences();
        Queue = new JobQueue(time, pollInterval ?? JobViewModel.DefaultPollInterval, ScheduleCheckInterval,
            preferences, loggers.CreateLogger("TTDeployTool.Desktop.Jobs"))
        {
            Clipboard = clipboard,
        };
        Queue.Finished += NotifyFinished;
        Queue.Finished += RefreshStatusWhenFinished;
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.HasActive))
                OnPropertyChanged(nameof(HasRunningJob));
        };
        var logger = new DesktopDeploymentLogger(loggers.CreateLogger("TTDeployTool.Desktop"));
        var plans = new InMemoryPlanStore(time);
        var jobs = new JobManager(new InMemoryJobStore(), time, loggers.CreateLogger<JobManager>());
        var caller = new LocalCallerContext();

        Target = new TargetViewModel(signIn, clients, loggers.CreateLogger("TTDeployTool.Desktop.Target"), time);
        Status = new StatusViewModel(Target, logger,
            () => Queue.ActiveFor(Target.Subscription, Target.ResourceGroup, Target.AppName.ToLowerInvariant())?.IsRunning == true, clipboard);
        // サインアウトしたときに一覧を消すため、サインインより先に作る
        Deployments = new DeploymentListViewModel(Target, Queue, logger, loggers.CreateLogger("TTDeployTool.Desktop.Deployments"), time,
            launcher, clipboard, OpenAsync);
        var deletions = new DeletionWorkflow(plans, jobs, caller, logger, time);
        Expiry = new ExpiryWatcher(Deployments, Queue, Target, deletions, logger, preferences, notifications, time,
            loggers.CreateLogger("TTDeployTool.Desktop.Expiry"), () => SignIn!.IsSignedIn);
        Startup = new StartupSettingViewModel(startupTask, preferences, dialogs, loggers.CreateLogger("TTDeployTool.Desktop.Startup"));
        SignIn = new SignInViewModel(signIn, async signedIn =>
        {
            if (signedIn)
            {
                await Target.LoadAsync();
                // サインインできたら、利用可能期限を確かめ、その後は最も近い期限の時刻か 6 時間ごとに確かめる（docs/design/desktop-jobs.md の 6.5、D21）
                await Expiry.StartAsync();
            }
            else
            {
                Target.Clear();
                Deployments.Clear();
            }
        }, loggers.CreateLogger("TTDeployTool.Desktop.SignIn"));
        Settings = new SettingsViewModel(Target, time, preferences, dialogs, clipboard);
        Deployment = new DeploymentViewModel(new DeploymentWorkflow(plans, jobs, caller, logger, time), Target, Settings, Queue, Show, time);
        Deletion = new DeletionViewModel(deletions, Target, Queue, Show, time, clipboard);
        DryRun = new DryRunViewModel(Settings, dialogs, launcher);
        Wizard = new DeployWizardViewModel(Settings, Deployment);
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

    /// <summary>利用可能期限の確認（期限の前の通知と、期限での削除）。</summary>
    public ExpiryWatcher Expiry { get; }

    /// <summary>Windows のサインイン時の起動（「ジョブ」の画面）。</summary>
    public StartupSettingViewModel Startup { get; }

    public TargetViewModel Target { get; }

    public SettingsViewModel Settings { get; }

    public DeploymentViewModel Deployment { get; }

    public DeletionViewModel Deletion { get; }

    public StatusViewModel Status { get; }

    public DryRunViewModel DryRun { get; }

    /// <summary>デプロイのウィザード（設定 → 確認と実行）。</summary>
    public DeployWizardViewModel Wizard { get; }

    /// <summary>実行状況の画面に出すジョブ（無ければ、Azure の状態だけを出す）。</summary>
    public JobViewModel? StatusJob
    {
        get => _statusJob;
        private set => SetProperty(ref _statusJob, value, new Dependent(nameof(HasStatusJob)));
    }

    public bool HasStatusJob => StatusJob != null;

    /// <summary>サインイン時の起動を尋ねた処理（テストで待つ）。</summary>
    internal Task StartupQuestion { get; private set; } = Task.CompletedTask;

    /// <summary>実行状況の画面で、ジョブが終わった後に Azure の状態を読み直した処理（テストで待つ）。</summary>
    internal Task StatusRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>デプロイの一覧（Azure から集める）。</summary>
    public DeploymentListViewModel Deployments { get; }

    /// <summary>画面を切り替える要求（デプロイの一覧の行の操作。名前は画面の項目のタグ: wizard・status・delete・jobs など）。</summary>
    public event Action<string>? NavigationRequested;

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
        private set => SetProperty(ref _currentDeployment, value, new Dependent(nameof(HasCurrentDeployment)));
    }

    /// <summary>
    /// デプロイの画面にジョブを表示するか。ジョブが無いのにジョブの表示の型だけが描かれ、「: 」と進み具合のバーが出ていたため
    /// （実機の確認、2026-10-21）、無ければ欄ごと隠す。
    /// </summary>
    public bool HasCurrentDeployment => CurrentDeployment != null;

    /// <summary>最後に始めた削除のジョブ（削除の画面に表示する）。</summary>
    public JobViewModel? CurrentDeletion
    {
        get => _currentDeletion;
        private set => SetProperty(ref _currentDeletion, value, new Dependent(nameof(HasCurrentDeletion)));
    }

    /// <summary>削除の画面にジョブを表示するか（<see cref="HasCurrentDeployment"/> と同じ）。</summary>
    public bool HasCurrentDeletion => CurrentDeletion != null;

    /// <summary>終わっていないジョブ（予定・待機中・一時停止を含む）があるか。</summary>
    public bool HasRunningJob => Queue.HasActive;

    /// <summary>起動時の処理（前回のサインインを復元し、サブスクリプションを読む。サインイン時の起動の状態も読む）。</summary>
    public async Task InitializeAsync()
    {
        await Startup.RefreshAsync();
        await SignIn.RestoreAsync();
    }

    /// <summary>
    /// Windows のサインイン時に起動されたとき（ウィンドウは出さない）。前回のサインインを対話なしで復元し、できなければ、サインインが要ることを通知して待つ（D19）。
    /// </summary>
    public async Task InitializeAtStartupAsync()
    {
        await InitializeAsync();
        if (!SignIn.IsSignedIn)
            _notifications?.Show(new AppNotice(
                "サインインが必要です",
                "利用可能期限の確認と、期限を過ぎたデプロイの削除ができません。この通知か通知領域のアイコンを押して、サインインしてください。",
                Page: "signin"));
    }

    /// <summary>
    /// ウィンドウを閉じるとき。終わっていないジョブがあれば、アプリを終了せずにウィンドウを隠して通知領域に残る（D3）。
    /// 初めて隠すときに、その旨を通知する。ジョブが無ければ終了する。
    /// </summary>
    public CloseAction RequestClose()
    {
        // ジョブが無くても、利用可能期限日のあるデプロイがあれば、期限を確かめるために残る（D18）
        var watching = SignIn.IsSignedIn && Deployments.HasExpiringDeployments;
        if (!HasRunningJob && !watching)
            return CloseAction.Exit;
        if (!_hiddenOnce)
        {
            _hiddenOnce = true;
            var reason = HasRunningJob
                ? $"ジョブ（{Queue.Summary}）があるため"
                : "利用可能期限のあるデプロイを確かめる（期限の前の通知と、期限での削除）ため";
            _notifications?.Show(new AppNotice(
                "バックグラウンドで実行を続けています",
                $"{reason}、通知領域に残ります。開くには通知領域のアイコンを押し、終了するにはそのメニューの「終了」を選んでください。"));
        }
        return CloseAction.Hide;
    }

    /// <summary>
    /// アプリを終了してよいか（通知領域の「終了」）。終わっていないジョブがあれば確認する（終了すると実行中のジョブは止まり、予定・待機中のジョブは消える）。
    /// </summary>
    public async Task<bool> ConfirmExitAsync() =>
        !HasRunningJob || await _dialogs.ConfirmAsync(
            "終わっていないジョブがあります",
            $"ジョブ（{Queue.Summary}）があります。終了すると実行中の処理が止まり、途中まで作成・削除したリソースが残ります" +
            "（もう一度実行すると続きから処理します）。予定・待機中のジョブは消えます。終了しますか？",
            "終了する");

    /// <summary>通知から開くジョブ（一覧から消した場合などは null）。</summary>
    public JobViewModel? FindJob(string id) => Jobs.FirstOrDefault(j => j.Id == id);

    public void Dispose()
    {
        Expiry.Dispose();
        Queue.Dispose();
    }

    // ジョブが終わったことを、ウィンドウを隠していても分かるように通知する（結果か失敗の理由と、予定より遅れた旨を添える）
    private void NotifyFinished(JobViewModel job)
    {
        var details = new[] { job.Error, job.Endpoint?.ToString(), job.DeletionSummary, job.Note }.OfType<string>().ToList();
        _notifications?.Show(new AppNotice(
            $"{job.Title}: {job.StatusText}", details.Count == 0 ? "ジョブが終わりました。" : string.Join("\n", details), job.Id));
    }

    /// <summary>
    /// 画面を開く（docs/design/desktop-wizard.md の 2）。wizard はウィザードの最初の画面、status は対象の最も新しいジョブと Azure の状態、
    /// delete は削除の画面（計画を作る）。そのほかは画面を切り替えるだけ。
    /// </summary>
    public async Task OpenAsync(string page)
    {
        switch (page)
        {
            case "wizard":
                await Wizard.StartOverAsync();
                NavigationRequested?.Invoke(page);
                break;
            case "status":
                StatusJob = Jobs.FirstOrDefault(j => j.IsFor(Target.Subscription, Target.ResourceGroup, Target.AppName.ToLowerInvariant()));
                NavigationRequested?.Invoke(page);
                await Status.RefreshCommand.ExecuteAsync();
                break;
            case "delete":
                NavigationRequested?.Invoke(page);
                await Deletion.PlanCommand.ExecuteAsync();
                break;
            default:
                NavigationRequested?.Invoke(page);
                break;
        }
    }

    // 実行状況の画面のジョブが終わったら、Azure の状態を読み直す
    private void RefreshStatusWhenFinished(JobViewModel job)
    {
        if (job == StatusJob)
            StatusRefresh = Status.RefreshCommand.ExecuteAsync();
    }

    // 登録したジョブを、デプロイ・削除の画面に表示する。今すぐ実行したものは実行状況の画面、予定に追加したものはジョブの画面を開く
    private void Show(JobViewModel job)
    {
        CurrentJob = job;
        if (job.ScheduledAt == null)
        {
            StatusJob = job;
            NavigationRequested?.Invoke("status");
            StatusRefresh = Status.RefreshCommand.ExecuteAsync();
        }
        else
        {
            NavigationRequested?.Invoke("jobs");
        }
        if (job.IsDeployment)
        {
            CurrentDeployment = job;
            // 期限日のあるデプロイを初めて登録したら、サインイン時の起動を一度だけ尋ねる（D16）
            if (Settings.Expiry.Mode != ExpiryMode.None)
                StartupQuestion = Startup.AskOnceAsync();
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
