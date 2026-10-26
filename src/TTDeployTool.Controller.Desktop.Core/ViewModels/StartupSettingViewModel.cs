using Microsoft.Extensions.Logging;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// Windows のサインイン時に、通知領域で起動するか（「ジョブ」の画面。docs/design/desktop-jobs.md の 6.5、D16）。
/// 期限日のあるデプロイのジョブを初めて登録したときに、まだ有効でなければ一度だけ尋ねる。
/// </summary>
public sealed class StartupSettingViewModel : ObservableObject
{
    private readonly IStartupTaskService? _service;
    private readonly IAppPreferences _preferences;
    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;
    private StartupTaskStatus _status = StartupTaskStatus.Unavailable;

    public StartupSettingViewModel(IStartupTaskService? service, IAppPreferences preferences, IDialogService dialogs, ILogger logger)
    {
        _service = service;
        _preferences = preferences;
        _dialogs = dialogs;
        _logger = logger;
        ToggleCommand = new AsyncCommand(ToggleAsync, () => CanChange);
    }

    /// <summary>有効・無効を切り替える。</summary>
    public AsyncCommand ToggleCommand { get; }

    public StartupTaskStatus Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value))
                return;
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(ToggleText));
            OnPropertyChanged(nameof(CanChange));
            OnPropertyChanged(nameof(StatusText));
            ToggleCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEnabled => Status == StartupTaskStatus.Enabled;

    /// <summary>切り替えのボタンの文言。</summary>
    public string ToggleText => IsEnabled ? "無効にする" : "有効にする";

    /// <summary>アプリから変えられるか（利用者・ポリシーで無効にされた場合と、確かめられない場合は変えられない）。</summary>
    public bool CanChange => Status is StartupTaskStatus.Enabled or StartupTaskStatus.Disabled;

    public string StatusText => Status switch
    {
        StartupTaskStatus.Enabled => "有効: Windows にサインインすると、ウィンドウを出さずに通知領域で起動し、利用可能期限を確かめます。",
        StartupTaskStatus.Disabled => "無効: このアプリを起動している間だけ、利用可能期限を確かめます。",
        StartupTaskStatus.DisabledByUser =>
            "タスク マネージャーの「スタートアップ アプリ」か、Windows の「設定」→「アプリ」→「スタートアップ」で無効にされています。有効にするには、そこで有効にしてください。",
        StartupTaskStatus.DisabledByPolicy => "組織のポリシーで無効にされているか、この PC では使えません。",
        _ => "この起動の方法では使えません（ストアやパッケージから導入したアプリで使えます）。",
    };

    /// <summary>
    /// 期限での削除は、どの PC でもこのアプリが動いていなければ遅れることの説明（画面に出す）。
    /// </summary>
    public static string Explanation =>
        "期限の前の通知と期限での削除は、このアプリが動いている間に行います。どの PC でもアプリが動いていなければ、次にどれかの PC で動いたときに削除します。";

    /// <summary>今の状態を読む（起動時と、画面を開いたとき）。</summary>
    public async Task RefreshAsync()
    {
        Status = _service == null ? StartupTaskStatus.Unavailable : await _service.GetStatusAsync();
    }

    /// <summary>期限日のあるデプロイのジョブを登録したとき。まだ尋ねておらず、無効なら、一度だけ有効にするかを尋ねる。</summary>
    public async Task AskOnceAsync()
    {
        if (_preferences.StartupTaskAsked)
            return;
        await RefreshAsync();
        if (Status != StartupTaskStatus.Disabled)
            return;
        _preferences.StartupTaskAsked = true;
        if (await _dialogs.ConfirmAsync(
                "Windows のサインイン時に起動しますか？",
                "利用可能期限を過ぎたデプロイは、このアプリが自動で削除します。Windows にサインインしたときに、ウィンドウを出さずに通知領域で起動すると、" +
                "期限を確実に確かめられます。あとで「ジョブ」の画面で変えられます。",
                "起動する"))
            await SetAsync(true);
    }

    private Task ToggleAsync() => SetAsync(!IsEnabled);

    private async Task SetAsync(bool enable)
    {
        if (enable)
        {
            Status = await _service!.EnableAsync();
        }
        else
        {
            await _service!.DisableAsync();
            Status = await _service.GetStatusAsync();
        }
        _logger.LogInformation("サインイン時の起動: {Status}", Status);
    }
}
