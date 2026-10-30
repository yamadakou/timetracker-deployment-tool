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
        Tray = new TaskbarIcon
        {
            ToolTipText = "TTDeployTool（Blazor Hybrid の試作）",
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "TrayIcon.ico")),
        };
        Tray.ForceCreate(enablesEfficiencyMode: false);
        Closed += (_, _) => Tray.Dispose();
    }

    /// <summary>通知領域のアイコン（WinUI の画面と同じ H.NotifyIcon の WPF 版）。</summary>
    public TaskbarIcon Tray { get; }

    public BlazorWebView BlazorView => WebView;
}
