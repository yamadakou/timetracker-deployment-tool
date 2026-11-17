using System.Windows;
using System.Windows.Interop;
using H.NotifyIcon;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using TTDeployTool.Controller.Desktop.Blazor.Shell;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Windows.Services.Store;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// ウィンドウ（BlazorWebView に画面の根 <see cref="DesktopShell"/> を描く）と通知領域のアイコン（WinUI 3 のアプリの MainWindow と同じ動き。docs/design/desktop/desktop-blazor-hybrid.md の 16）:
/// 閉じると、終わっていないジョブか期限のあるデプロイがあれば隠して通知領域に残り（<see cref="MainViewModel.RequestClose"/>）、無ければ終了する。
/// 通知領域のアイコンは、押すと開き、メニューは「開く」「終了」（終了は確かめる）。ツールチップはジョブの状況。起動したら Microsoft Store の更新を確かめる。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>ロックの解除・復帰の後、画面に確かめるまでの待ち（画面とネットワークが戻るのを待つ）。</summary>
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(2);

    /// <summary>画面がこの時間までに応えなければ、止まったとみなして作り直す。</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>これより長くロック・スリープしていたら、戻ったときに画面を作り直す（WebView2 の描いたものの取り込みが途切れ、出し直しでは戻らないことがあるため）。</summary>
    public static readonly TimeSpan LongLock = TimeSpan.FromMinutes(1);

    /// <summary>作り直した画面ができあがるまで待つ長さ。</summary>
    public static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(30);

    private DateTimeOffset? _lockedAt;
    private TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _exiting;
    private DesktopSession? _session;
    private ILogger _logger = NullLogger.Instance;
    private BlazorWebView _view;
    // BlazorWebView が使う WebView2 の WPF の部品（WebView2CompositionControl: WebView2 が描いたものを WPF の画面に取り込んで映す）
    private FrameworkElement? _webView2;

    /// <summary>
    /// WebView2 の起動の引数。Chromium のウィンドウの隠れ具合の判定（CalculateNativeWinOcclusion）を止める（オーナーの実機の確認、2026-11-18:
    /// ロックから戻ると、画面は応える〔「画面の確かめ: 応えました」〕のに真っ白のままだった。ロックの間に隠れたとみなして描くのを止め、戻っても描き直さないことがある）。
    /// </summary>
    public const string BrowserArguments = "--disable-features=CalculateNativeWinOcclusion";

    public MainWindow()
    {
        InitializeComponent();
        // ウィンドウの題の左とタスク バーのアイコン（アプリのアイコン。オーナーの用意したもの、2026-11-14）
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "TrayIcon.ico")));
        Handle = new WindowInteropHelper(this).EnsureHandle();
        // テスト（Playwright の WebView2Tests）だけ: 環境変数 TT_BLAZOR_CDP_PORT があれば、WebView2 を CDP のポートと別のユーザーデータのフォルダーで作る
        // （Microsoft Learn「ASP.NET Core Blazor Hybrid」の Web View configuration: BlazorWebViewInitializing）
        _view = WebView;
        Hook(_view);
        Closing += OnClosing;
        // ロックの解除・スリープからの復帰で、画面（WebView2）が応えるかを確かめる（オーナーの実機の確認、2026-11-17: 長い時間のロックから戻ると、
        // 画面が真っ白で「応答なし」になった。アプリのログには誤りが無く、WebView2 のプロセスが止まったとみられる。desktop-blazor-hybrid.md の 16.26）
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closed += (_, _) =>
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        };
    }

    /// <summary>画面のプロセスが落ちた・止まったのを受けて、戻した回数（確認で読む）。</summary>
    public int Recoveries { get; private set; }

    /// <summary>画面の WebView を作り直した回数（確認で読む）。</summary>
    public int Recreations { get; private set; }

    // WebView2 を作るときの設定と、作った後の処理（作り直したときも同じ）
    private void Hook(BlazorWebView view)
    {
        // テスト（Playwright の WebView2Tests）だけ: 環境変数 TT_BLAZOR_CDP_PORT があれば、WebView2 を CDP のポートと別のユーザーデータのフォルダーで作る
        // （Microsoft Learn「ASP.NET Core Blazor Hybrid」の Web View configuration: BlazorWebViewInitializing）
        var port = Environment.GetEnvironmentVariable("TT_BLAZOR_CDP_PORT");
        view.BlazorWebViewInitializing += (_, e) =>
        {
            e.EnvironmentOptions = new CoreWebView2EnvironmentOptions(port is { Length: > 0 } ? $"{BrowserArguments} --remote-debugging-port={port}" : BrowserArguments);
            if (port is { Length: > 0 } && Environment.GetEnvironmentVariable("TT_BLAZOR_USER_DATA") is { Length: > 0 } folder)
                e.UserDataFolder = folder;
        };
        view.BlazorWebViewInitialized += (_, e) =>
        {
            _webView2 = e.WebView;
            Core = e.WebView.CoreWebView2;
            // 入力欄の右クリックの「切り取り・コピー・貼り付け・すべて選択」（EditMenu）
            EditMenu.Attach(Core);
            // 画面のプロセスが落ちた・止まったら、ログに残して戻す（前は受けておらず、真っ白のままだった）
            Core.ProcessFailed += OnProcessFailed;
            _created.TrySetResult();
        };
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.LogWarning("画面（WebView2）のプロセスが止まりました: {Kind}（理由 {Reason}・終了コード {ExitCode}・{Description}）",
            e.ProcessFailedKind, e.Reason, e.ExitCode, e.ProcessDescription);
        switch (e.ProcessFailedKind)
        {
            // 画面を描くプロセスが落ちた・応えない: 画面を読み直す（ジョブと一覧は .NET の側にあるため、消えない）
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                Recoveries++;
                Dispatcher.InvokeAsync(ReloadPage);
                break;
            // WebView2 の全体が落ちた: WebView を作り直す
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                Recoveries++;
                Dispatcher.InvokeAsync(RecreateWebView);
                break;
            // GPU などのほかのプロセスは、WebView2 が作り直す（ログに残すだけ）
        }
    }

    private void ReloadPage()
    {
        _logger.LogInformation("画面を読み直します");
        try
        {
            Core?.Reload();
        }
        catch (Exception ex)
        {
            // 読み直せない（WebView2 の全体が使えない）なら、作り直す
            _logger.LogWarning(ex, "画面を読み直せませんでした。作り直します");
            RecreateWebView();
        }
    }

    /// <summary>画面の WebView を作り直す（止まった WebView2 を捨てて、新しく作る。画面は今の状態から描き直す）。</summary>
    public void RecreateWebView()
    {
        _logger.LogInformation("画面（WebView2）を作り直します");
        var old = _view;
        var fresh = new BlazorWebView { HostPage = old.HostPage, Services = old.Services };
        fresh.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(DesktopShell) });
        Hook(fresh);
        Core = null;
        _webView2 = null;
        _created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _view = fresh;
        Content = fresh;
        Recreations++;
        _ = DisposeQuietlyAsync(old);
    }

    private async Task DisposeQuietlyAsync(BlazorWebView view)
    {
        try
        {
            await view.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "止まった画面を捨てられませんでした");
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            Dispatcher.InvokeAsync(() => CheckAfterAsync($"Windows のロックを解除しました（{e.Reason}）", Locked()));
        else if (e.Reason is SessionSwitchReason.SessionLock)
        {
            _lockedAt ??= DateTimeOffset.Now;
            _logger.LogInformation("Windows をロックしました");
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Dispatcher.InvokeAsync(() => CheckAfterAsync("スリープから復帰しました", LongLock));
        else if (e.Mode == PowerModes.Suspend)
        {
            _lockedAt ??= DateTimeOffset.Now;
            _logger.LogInformation("スリープに入ります");
        }
    }

    // ロック・スリープしていた長さ（分からなければ 0）
    private TimeSpan Locked()
    {
        var locked = _lockedAt is { } at ? DateTimeOffset.Now - at : TimeSpan.Zero;
        _lockedAt = null;
        return locked;
    }

    /// <summary>
    /// ロックの解除・復帰の後の戻し方（オーナーの実機の確認、2026-11-18: 画面は応えるのに真っ白のままだった）。長いロック・スリープの後は画面を作り直し
    /// （開いていた画面は DesktopSession.Page で戻る）、短ければ描き直させる。その後、画面が応えるかを確かめる。確かめの結果を返す（確認で読む）。
    /// </summary>
    public async Task<string> CheckAfterAsync(string what, TimeSpan locked)
    {
        _logger.LogInformation("{What}（{Minutes:0.#} 分）。画面が応えるかを確かめます", what, locked.TotalMinutes);
        await Task.Delay(ResumeDelay);
        if (locked >= LongLock)
        {
            RecreateWebView();
            // 新しい WebView2 ができあがるまで待ってから確かめる（すぐに確かめると「画面がまだありません」になった。CI、2026-11-18）
            await Task.WhenAny(_created.Task, Task.Delay(CreateTimeout));
            await WaitRenderedAsync();
        }
        else
            Repaint();
        return await CheckResponsiveAsync();
    }

    // 作り直した画面を、Blazor が描き終えるまで待つ（CreateTimeout まで）
    private async Task WaitRenderedAsync()
    {
        var until = DateTimeOffset.Now + CreateTimeout;
        while (DateTimeOffset.Now < until)
        {
            if (Core is { } core && await core.ExecuteScriptAsync("document.querySelector('.tt-app') !== null") == "true")
                return;
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// 画面をいったん隠して出し直し、WebView2 に描き直させる（ロックの解除・復帰の後。応えるのに真っ白のままのときの戻し方）。描き直させたら true。
    /// </summary>
    public bool Repaint()
    {
        if (_webView2 is not { } view)
            return false;
        view.Visibility = Visibility.Hidden;
        view.UpdateLayout();
        view.Visibility = Visibility.Visible;
        _logger.LogInformation("画面を描き直させました");
        return true;
    }

    /// <summary>
    /// 画面が応えるかを確かめ、応えなければ作り直す・描かれていなければ読み直す。結果を返す（ログにも残す。確認で読む）。
    /// </summary>
    public async Task<string> CheckResponsiveAsync()
    {
        string result;
        if (Core is not { } core)
            result = "画面がまだありません";
        else
        {
            // 画面が描かれていれば、ページから見た見え方（visible・hidden）を返す。描かれていなければ none
            var probe = core.ExecuteScriptAsync("document.querySelector('.tt-app') !== null ? document.visibilityState : 'none'");
            if (await Task.WhenAny(probe, Task.Delay(ResponseTimeout)) != probe)
            {
                result = "応えません（作り直しました）";
                RecreateWebView();
            }
            else if ((await probe).Trim('"') is var state && state == "none")
            {
                result = "描かれていません（読み直しました）";
                ReloadPage();
            }
            else
                result = $"応えました（ページの見え方: {state}）";
        }
        _logger.LogInformation("画面の確かめ: {Result}", result);
        return result;
    }

    /// <summary>ウィンドウのハンドル（WAM のサインイン・Store の確認に渡す）。</summary>
    public IntPtr Handle { get; }

    /// <summary>通知領域のアイコン（WinUI の画面と同じ H.NotifyIcon の WPF 版）。</summary>
    public TaskbarIcon Tray { get; private set; } = default!;

    /// <summary>作られた WebView2（確認で、画面が出たかを読む）。</summary>
    public Microsoft.Web.WebView2.Core.CoreWebView2? Core { get; private set; }

    /// <summary>画面と共有する状態（Initialize で入れる）。</summary>
    public DesktopSession Session => _session!;

    private MainViewModel ViewModel => _session!.ViewModel;

    /// <summary>画面のサービスを入れ、通知領域のアイコンを作り、Store の更新を確かめる（始めの処理は、画面を描いたときに DesktopShell が始める）。</summary>
    public void Initialize(DesktopSession session, ILogger? logger = null)
    {
        _session = session;
        _logger = logger ?? NullLogger.Instance;
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddTTDeployToolDesktop(session);
        Resources.Add("services", services.BuildServiceProvider());
        CreateTray();
        _ = CheckForUpdatesAsync();
    }

    /// <summary>隠したウィンドウを出す（通知領域のアイコン、2 つ目の起動、通知から）。</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>通知を押した: ウィンドウを出し、そのジョブを開く（<paramref name="open"/> が false なら出すだけ。連携の確認のとき）。</summary>
    public void ShowFromNotification(IReadOnlyDictionary<string, string> arguments, bool open = true)
    {
        ShowFromTray();
        if (open)
            _ = ViewModel.OpenFromNotificationAsync(arguments);
    }

    /// <summary>終了する（通知領域の「終了」で確かめた後と、確認の終わり）。</summary>
    public void Exit()
    {
        _exiting = true;
        Tray?.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    // × で閉じた: ジョブか期限のあるデプロイがあれば隠し、無ければそのまま閉じて終了する（ShutdownMode は OnMainWindowClose）。
    // 前は閉じている途中で Exit（Close）を呼んでおり、WPF が InvalidOperationException を出して異常終了していた（オーナーの実機のログ、2026-11-18）
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _session == null)
            return;
        if (ViewModel.RequestClose() == CloseAction.Hide)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _exiting = true;
        Tray?.Dispose();
    }

    private void CreateTray()
    {
        var open = new AsyncCommand(() =>
        {
            ShowFromTray();
            return Task.CompletedTask;
        });
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "開く", Command = open });
        // 画面が真っ白で戻らないときに、手で作り直す（ジョブと一覧は消えない。オーナーの実機の確認、2026-11-18）
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "画面を作り直す",
            Command = new AsyncCommand(() =>
            {
                ShowFromTray();
                RecreateWebView();
                return Task.CompletedTask;
            }),
        });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "終了", Command = new AsyncCommand(ExitFromTrayAsync) });
        Tray = new TaskbarIcon
        {
            ToolTipText = TrayToolTip(),
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "TrayIcon.ico")),
            ContextMenu = menu,
            LeftClickCommand = open,
            NoLeftClickDelay = true,
        };
        Tray.ForceCreate(enablesEfficiencyMode: false);
        ViewModel.Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.Summary))
                Dispatcher.InvokeAsync(() => Tray.ToolTipText = TrayToolTip());
        };
    }

    private string TrayToolTip() => ViewModel.Queue.Summary is { Length: > 0 } summary ? $"TTDeployTool（{summary}）" : "TTDeployTool（ジョブはありません）";

    // 通知領域の「終了」: ウィンドウを出して、終わっていないジョブがあれば確かめる（WinUI のアプリと同じ）
    private async Task ExitFromTrayAsync()
    {
        ShowFromTray();
        if (await ViewModel.ConfirmExitAsync())
            Exit();
    }

    // Microsoft Store に新しい版があれば、上の帯に出す（Store から入れていなければ確かめられないため、出さない）
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, Handle);
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            _session!.UpdateAvailable = updates.Count > 0;
        }
        catch (Exception)
        {
            _session!.UpdateAvailable = false;
        }
    }
}
