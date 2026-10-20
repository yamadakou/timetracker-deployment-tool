using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Windows.Services.Store;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// アプリのウィンドウ。表示する項目の切り替え、閉じるときに隠すか終了するか、通知領域のアイコン、Store の更新の確認だけを行い、
/// 処理は ViewModel（<see cref="MainViewModel"/>）に任せる。
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _exiting;
    private TaskbarIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = false;
    }

    public MainViewModel ViewModel { get; private set; } = null!;

    public void Initialize(MainViewModel viewModel, IntPtr hwnd)
    {
        ViewModel = viewModel;
        Bindings.Update();
        AppWindow.Closing += OnClosing;
        CreateTray();
        _ = viewModel.InitializeAsync();
        _ = CheckForUpdatesAsync(hwnd);
    }

    // ComboBox の選択（object）を ViewModel の型に戻す（x:Bind の TwoWay は SelectedItem の object を変換できないため）
    private void OnSubscriptionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Target.SelectedSubscription = ((ComboBox)sender).SelectedItem as SubscriptionChoice;

    private void OnDbTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is string dbType)
            ViewModel.Settings.DbType = dbType;
    }

    private void OnPersistDbChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is PersistDbOption option)
            ViewModel.Settings.PersistDb = option;
    }

    private void OnConcurrencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is int concurrency)
            ViewModel.Queue.MaxConcurrency = concurrency;
    }

    // 上部の帯の「ジョブを見る」
    private void OnShowJobs(object sender, RoutedEventArgs e) => Nav.SelectedItem = JobsNavItem;

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        foreach (var (panel, name) in new (FrameworkElement, string)[]
                 {
                     (TargetPanel, "target"), (SettingsPanel, "settings"), (DeployPanel, "deploy"), (StatusPanel, "status"),
                     (DeletePanel, "delete"), (DryRunPanel, "dryrun"), (JobsPanel, "jobs"),
                 })
            panel.Visibility = name == tag ? Visibility.Visible : Visibility.Collapsed;
    }

    // 終わっていないジョブがあれば、終了せずにウィンドウを隠して通知領域に残る（D3）。無ければ終了する
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
            return;
        args.Cancel = true;
        if (ViewModel.RequestClose() == CloseAction.Hide)
            sender.Hide();
        else
            Exit();
    }

    /// <summary>隠したウィンドウを出す（通知領域のアイコン、2 つ目の起動、通知から）。</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    /// <summary>通知から、ジョブの画面を開く（ジョブが一覧に残っていれば、そこまで送る）。</summary>
    public void ShowJob(string? jobId)
    {
        ShowFromTray();
        Nav.SelectedItem = JobsNavItem;
        if (jobId != null && ViewModel.FindJob(jobId) is { } job && JobsList.ContainerFromItem(job) is UIElement container)
            container.StartBringIntoView();
    }

    // 通知領域のアイコン（押すと開く。メニューは「開く」「終了」）。ツールチップに、終わっていないジョブの状況を出す
    private void CreateTray()
    {
        var open = new AsyncCommand(() =>
        {
            ShowFromTray();
            return Task.CompletedTask;
        });
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "開く", Command = open });
        menu.Items.Add(new MenuFlyoutItem { Text = "終了", Command = new AsyncCommand(ExitFromTrayAsync) });
        _tray = new TaskbarIcon
        {
            ToolTipText = TrayToolTip(),
            // System.Drawing.Icon で読むため、ICO（BMP の形式。Assets/Square150x150Logo.png から作ったもの）にする
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/TrayIcon.ico")),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = open,
            NoLeftClickDelay = true,
        };
        // ジョブを実行し続けるため、効率モード（優先度を下げる）にはしない
        _tray.ForceCreate(enablesEfficiencyMode: false);
        ViewModel.Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.Summary))
                _tray.ToolTipText = TrayToolTip();
        };
    }

    private string TrayToolTip() => ViewModel.Queue.Summary is { Length: > 0 } summary ? $"TTDeployTool（{summary}）" : "TTDeployTool（ジョブはありません）";

    // 通知領域の「終了」。確認のダイアログを出すため、先にウィンドウを出す
    private async Task ExitFromTrayAsync()
    {
        ShowFromTray();
        if (await ViewModel.ConfirmExitAsync())
            Exit();
    }

    private void Exit()
    {
        _exiting = true;
        _tray?.Dispose();
        Close();
    }

    // Store から導入した場合だけ、更新があるかを確かめる（Store 以外から動かした場合は何もしない。詳細設計 8.2）
    private async Task CheckForUpdatesAsync(IntPtr hwnd)
    {
        try
        {
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, hwnd);
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            UpdateBar.IsOpen = updates.Count > 0;
        }
        catch (Exception)
        {
            UpdateBar.IsOpen = false;
        }
    }
}
