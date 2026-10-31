using System.Windows;
using System.Windows.Interop;
using H.NotifyIcon;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
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
    private bool _exiting;
    private DesktopSession? _session;

    public MainWindow()
    {
        InitializeComponent();
        Handle = new WindowInteropHelper(this).EnsureHandle();
        // テスト（Playwright の WebView2Tests）だけ: 環境変数 TT_BLAZOR_CDP_PORT があれば、WebView2 を CDP のポートと別のユーザーデータのフォルダーで作る
        // （Microsoft Learn「ASP.NET Core Blazor Hybrid」の Web View configuration: BlazorWebViewInitializing）
        if (Environment.GetEnvironmentVariable("TT_BLAZOR_CDP_PORT") is { Length: > 0 } port)
        {
            WebView.BlazorWebViewInitializing += (_, e) =>
            {
                e.EnvironmentOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions($"--remote-debugging-port={port}");
                if (Environment.GetEnvironmentVariable("TT_BLAZOR_USER_DATA") is { Length: > 0 } folder)
                    e.UserDataFolder = folder;
            };
        }
        WebView.BlazorWebViewInitialized += (_, e) => Core = e.WebView.CoreWebView2;
        Closing += OnClosing;
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
    public void Initialize(DesktopSession session)
    {
        _session = session;
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

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _session == null)
            return;
        e.Cancel = true;
        if (ViewModel.RequestClose() == CloseAction.Hide)
            Hide();
        else
            Exit();
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
