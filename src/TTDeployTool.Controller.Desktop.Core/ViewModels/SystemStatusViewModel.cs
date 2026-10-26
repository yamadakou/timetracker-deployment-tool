using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// 「設定」の画面の「Windows との連携」（docs/design/desktop-jobs.md の 6.6、D22）。サインイン時の起動の状態と前回の起動・通知の許可・
/// 今ウィンドウを閉じたらどうなるか・アプリの終了で消える予定と戻す予定を示し、Windows の設定を開く。画面を開いたときと「読み直す」で読み直す。
/// </summary>
public sealed class SystemStatusViewModel : ObservableObject
{
    /// <summary>Windows の「スタートアップ アプリ」の設定。</summary>
    public static readonly Uri StartupSettingsUri = new("ms-settings:startupapps");

    /// <summary>Windows の「通知」の設定。</summary>
    public static readonly Uri NotificationSettingsUri = new("ms-settings:notifications");

    /// <summary>デプロイの予定を入れる画面に添える注意（D24）。</summary>
    public const string DeployScheduleNote = "予定に追加したデプロイは、アプリを終了するか、Windows を再起動・サインアウトすると消えます（パスワードを保存しないため）。";

    /// <summary>削除の予定を入れる画面に添える説明（D23）。</summary>
    public const string DeletionScheduleNote =
        "予定に追加した削除は、アプリを終了しても、次にアプリを起動してサインインしたときに戻します（始める前に計画を作り直し、変わっていれば削除しません）。";

    private readonly INotificationService? _notifications;
    private readonly IAppPreferences _preferences;
    private readonly JobQueue _queue;
    private readonly ScheduledJobStore _store;
    private readonly Func<string> _closeBehavior;

    public SystemStatusViewModel(
        StartupSettingViewModel startup, INotificationService? notifications, IAppPreferences preferences, JobQueue queue, ScheduledJobStore store,
        Func<string> closeBehavior, ISystemLauncher launcher)
    {
        Startup = startup;
        _notifications = notifications;
        _preferences = preferences;
        _queue = queue;
        _store = store;
        _closeBehavior = closeBehavior;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        OpenStartupSettingsCommand = new AsyncCommand(() => Open(launcher, StartupSettingsUri));
        OpenNotificationSettingsCommand = new AsyncCommand(() => Open(launcher, NotificationSettingsUri));
        // ジョブの登録・状態の変更と、前回の予定を戻したときに、件数と閉じたときの動きを出し直す
        queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.Summary))
                RaiseJobTexts();
        };
        store.PropertyChanged += (_, _) => RaiseJobTexts();
    }

    public StartupSettingViewModel Startup { get; }

    /// <summary>前回の Windows のサインイン時の起動。</summary>
    public string LastStartupText => _preferences.LastStartupLaunch is { } launch
        ? $"前回のサインイン時の起動: {launch.At.ToLocalTime():yyyy-MM-dd HH:mm}（" +
          (launch.SignedIn ? "対話なしでサインインし、利用可能期限と予定を確かめました）" : "対話なしでサインインできず、サインインが要ることを通知しました）")
        : "Windows のサインイン時に起動したことは、まだありません（有効にした後、次に Windows にサインインしたときに起動します。" +
          "有効なのに再起動の後も起動していなければ、Windows の「スタートアップ アプリ」の設定を確かめてください）。";

    public NotificationPermission NotificationPermission => _notifications?.Permission ?? NotificationPermission.Unavailable;

    /// <summary>通知が出せないか（出せなければ、画面で目立たせる）。</summary>
    public bool NotificationsBlocked => NotificationPermission != NotificationPermission.Enabled;

    public string NotificationText => NotificationPermission switch
    {
        NotificationPermission.Enabled => "許可されています: 期限の前の通知・ジョブの終わりの通知・サインインが要ることの通知を出します。",
        NotificationPermission.DisabledForApplication => "このアプリの通知が、Windows の設定で止められています。期限の前やジョブの終わりに通知が出ません。",
        NotificationPermission.DisabledForUser => "すべてのアプリの通知が、Windows の設定で止められています。期限の前やジョブの終わりに通知が出ません。",
        NotificationPermission.DisabledByGroupPolicy => "組織のポリシーで通知が止められています。期限の前やジョブの終わりに通知が出ません。",
        NotificationPermission.DisabledByManifest => "このアプリのパッケージの設定で通知が止められています。",
        _ => "この起動の方法では確かめられません（ストアやパッケージから導入したアプリで確かめられます）。",
    };

    /// <summary>今ウィンドウを閉じたらどうなるか。</summary>
    public string CloseBehaviorText => _closeBehavior();

    /// <summary>まだ始めていないジョブのうち、アプリの終了で消えるもの（デプロイ）と、次の起動で戻すもの（削除）。</summary>
    public string ScheduledText
    {
        get
        {
            var waiting = _queue.Jobs.Where(j => j.State is DesktopJobState.Scheduled or DesktopJobState.Waiting or DesktopJobState.Paused).ToList();
            var deploys = waiting.Count(j => j.IsDeployment);
            var deletions = waiting.Count(j => j.Saved != null);
            var text = waiting.Count == 0
                ? "まだ始めていないジョブ（予定・待機中・一時停止）はありません。"
                : $"まだ始めていないジョブ: デプロイ {deploys} 件はアプリの終了・Windows の再起動・サインアウトで消えます（パスワードを保存しないため）。" +
                  $"削除 {deletions} 件は、次にアプリを起動してサインインしたときに戻します。";
            return _store.PendingDeletions == 0 ? text : $"{text}\n前回のアプリの削除の予定 {_store.PendingDeletions} 件を、保存したアカウントでサインインしたときに戻します。";
        }
    }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand OpenStartupSettingsCommand { get; }

    public AsyncCommand OpenNotificationSettingsCommand { get; }

    /// <summary>読み直す（画面を開いたときと「読み直す」）。</summary>
    public async Task RefreshAsync()
    {
        await Startup.RefreshAsync();
        OnPropertyChanged(nameof(LastStartupText));
        OnPropertyChanged(nameof(NotificationPermission));
        OnPropertyChanged(nameof(NotificationsBlocked));
        OnPropertyChanged(nameof(NotificationText));
        RaiseJobTexts();
    }

    private void RaiseJobTexts()
    {
        OnPropertyChanged(nameof(CloseBehaviorText));
        OnPropertyChanged(nameof(ScheduledText));
    }

    private static Task Open(ISystemLauncher launcher, Uri uri)
    {
        launcher.OpenUri(uri);
        return Task.CompletedTask;
    }
}
