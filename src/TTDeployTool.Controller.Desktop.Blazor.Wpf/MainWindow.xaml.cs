using System.Windows;
using H.NotifyIcon;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// 窓と BlazorWebView（Microsoft Learn「Build a Windows Presentation Foundation (WPF) Blazor app」の形）と、通知領域のアイコン。
/// サービスは画面（Razor）が使う試作のもの（Fixtures のモックの MainViewModel）と、WPF のクリップボード。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddTTDeployToolPrototype(HostSettings.DataDirectory, _ => new WpfClipboard());
        Resources.Add("services", services.BuildServiceProvider());
        InitializeComponent();
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
        // 通知領域のアイコン（押すと開く。メニューは「開く」「終了」。WinUI のアプリと同じ）
        var open = new Core.Mvvm.AsyncCommand(() =>
        {
            ShowFromTray();
            return Task.CompletedTask;
        });
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "開く", Command = open });
        // オーナーの実機での確認のための項目（ManualCheck）
        foreach (var (header, run) in ManualCheck.Items(this))
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = header, Command = new Core.Mvvm.AsyncCommand(run) });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "終了", Command = new Core.Mvvm.AsyncCommand(() =>
        {
            Exit();
            return Task.CompletedTask;
        }) });
        Tray = new TaskbarIcon
        {
            ToolTipText = "TTDeployTool（Blazor Hybrid の試作）",
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "TrayIcon.ico")),
            ContextMenu = menu,
            LeftClickCommand = open,
            NoLeftClickDelay = true,
        };
        Tray.ForceCreate(enablesEfficiencyMode: false);
        // 閉じると、終了せずに隠して通知領域に残る（WinUI のアプリはジョブがあるときだけ。試作のホストは画面の状態を持たないため、いつも隠す）
        Closing += (_, e) =>
        {
            if (_exiting)
                return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) => Tray.Dispose();
    }

    private bool _exiting;

    /// <summary>隠したウィンドウを出す（通知領域のアイコン、2 つ目の起動、通知から）。</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>通知領域の「終了」と、確認の終わり。</summary>
    public void Exit()
    {
        _exiting = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>通知領域のアイコン（WinUI の画面と同じ H.NotifyIcon の WPF 版）。</summary>
    public TaskbarIcon Tray { get; }

    /// <summary>作られた WebView2（確認で、画面に表が出たかを読む）。</summary>
    public Microsoft.Web.WebView2.Core.CoreWebView2? Core { get; private set; }
}
