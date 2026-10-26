using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
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

    /// <param name="atStartup">Windows のサインイン時に起動された（ウィンドウを出さず、サインインできなければ通知する）</param>
    public void Initialize(MainViewModel viewModel, IntPtr hwnd, bool atStartup = false)
    {
        ViewModel = viewModel;
        Bindings.Update();
        AppWindow.Closing += OnClosing;
        // デプロイの一覧の行の操作で、画面を切り替える
        viewModel.NavigationRequested += Navigate;
        CreateTray();
        _ = atStartup ? viewModel.InitializeAtStartupAsync() : viewModel.InitializeAsync();
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

    private void OnExpiryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is ExpiryOption option)
            ViewModel.Settings.Expiry = option;
    }

    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Settings.SelectedTemplate = ((ComboBox)sender).SelectedItem as DeploymentTemplate;

    private void OnSubscriptionFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is SubscriptionFilterOption option)
            ViewModel.Deployments.SubscriptionFilter = option;
    }

    // 削除・実行状況の画面の「デプロイの一覧に戻る」
    private void OnBackToList(object sender, RoutedEventArgs e) => Navigate("deployments");

    private void OnExpiryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is ExpiryFilterOption option)
            ViewModel.Deployments.ExpiryFilter = option;
    }

    // 表の見出しを押すと並べ替える（Tag は列の名前。docs/design/desktop-wizard.md の 7）
    private void OnSortDeployments(object sender, RoutedEventArgs e) =>
        ViewModel.Deployments.SortBy(Enum.Parse<DeploymentSortKey>((string)((Button)sender).Tag));

    private void OnSortJobs(object sender, RoutedEventArgs e) =>
        ViewModel.JobList.SortBy(Enum.Parse<JobSortKey>((string)((Button)sender).Tag));

    private void OnJobFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is JobFilterOption option)
            ViewModel.JobList.Filter = option;
    }

    // 見出しの絞り込みの「閉じる」
    private void OnCloseFlyout(object sender, RoutedEventArgs e)
    {
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot))
            popup.IsOpen = false;
    }

    // 表で選んだ行を ViewModel に伝える（ListView の SelectedItems は結び付けられないため）
    private void OnDeploymentsSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Deployments.SetSelection(DeploymentsTable.SelectedItems.OfType<DeploymentRowViewModel>());

    private void OnJobsSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.JobList.SetSelection(JobsList.SelectedItems.OfType<JobViewModel>());

    // ジョブの一覧の「詳細」と、行のダブルクリック: 実行状況の画面で開く
    private void OnShowJobDetail(object sender, RoutedEventArgs e)
    {
        if (ViewModel.JobList.Current is { } job)
            _ = ViewModel.OpenJobAsync(job);
    }

    private void OnJobDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is JobViewModel job)
            _ = ViewModel.OpenJobAsync(job);
    }

    // 実行状況の画面の「戻る」: 開いた画面（ジョブの一覧かデプロイの一覧）に戻る
    private void OnBackFromStatus(object sender, RoutedEventArgs e) => Navigate(ViewModel.ReturnPage);

    private void OnPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is int size)
            ViewModel.Deployments.PageSize = size;
    }

    private void OnConcurrencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is int concurrency)
            ViewModel.Queue.MaxConcurrency = concurrency;
    }

    // 上部の帯の「ジョブを見る」
    private void OnShowJobs(object sender, RoutedEventArgs e) => Navigate("jobs");

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // メニューに無い画面（削除・実行状況）を開いたときは、メニューの選択を外すだけ
        if ((args.SelectedItem as NavigationViewItem)?.Tag is string tag)
            ShowPanel(tag);
    }

    /// <summary>画面を開く（メニューにある画面はメニューを選び、削除・実行状況はメニューの選択を外して開く。docs/design/desktop-wizard.md の 2）。</summary>
    private void Navigate(string tag)
    {
        if (Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag) is { } item)
        {
            Nav.SelectedItem = item;
            return;
        }
        Nav.SelectedItem = null;
        ShowPanel(tag);
    }

    private void ShowPanel(string tag)
    {
        foreach (var (panel, name) in new (FrameworkElement, string)[]
                 {
                     (TargetPanel, "target"), (DeploymentsPanel, "deployments"), (WizardPanel, "wizard"), (StatusPanel, "status"),
                     (DeletePanel, "delete"), (DryRunPanel, "dryrun"), (JobsPanel, "jobs"), (BatchDeletePanel, "batchdelete"), (SettingsPanel, "settings"),
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

    /// <summary>通知から、画面を開く（deployments はデプロイの一覧、signin はサインインの画面）。</summary>
    public void ShowPage(string page)
    {
        ShowFromTray();
        Navigate(page == "signin" ? "target" : page);
    }

    /// <summary>通知から、ジョブの詳細（実行状況）を開く（一覧から消した場合などは、ジョブの一覧を開く）。</summary>
    public void ShowJob(string? jobId)
    {
        ShowFromTray();
        if (jobId != null && ViewModel.FindJob(jobId) is { } job)
            _ = ViewModel.OpenJobAsync(job);
        else
            Navigate("jobs");
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
