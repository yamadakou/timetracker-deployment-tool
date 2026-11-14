using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TTDeployTool.Application.Inventory;
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
    private TableBinder<DeploymentEntry>? _deploymentsTable;
    private TableBinder<JobViewModel>? _jobsTable;

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
        // 題の左とタスク バーのアイコン（オーナーのアイコン。No.89）
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico"));
        // デプロイの一覧の行の操作で、画面を切り替える
        viewModel.NavigationRequested += Navigate;
        // 表の選択と ViewModel の選択は、Core の SelectionSync が合わせる（画面の側は渡すだけ）
        viewModel.Deployments.TableSelection.Attach(DeploymentsTable.SelectedItems);
        viewModel.JobList.TableSelection.Attach(JobsList.SelectedItems);
        // 表の ▽・並べ替え・列の幅と順序は、Core の決まりで行う（docs/design/desktop/desktop-tables.md の 10）
        var deployments = viewModel.Deployments;
        _deploymentsTable = new TableBinder<DeploymentEntry>(DeploymentsTable, deployments.Filters, viewModel.DeploymentsLayout, deployments,
            row => (row as DeploymentRowViewModel)?.Entry, DeploymentListViewModel.CanSortBy,
            () => deployments.SortColumn, () => deployments.SortDescending, deployments.SortBy, deployments.ClearSort);
        var jobs = viewModel.JobList;
        _jobsTable = new TableBinder<JobViewModel>(JobsList, jobs.Filters, viewModel.JobsLayout, jobs, row => row as JobViewModel, JobListViewModel.CanSortBy,
            () => jobs.SortColumn, () => jobs.SortDescending, jobs.SortBy, jobs.ClearSort);
        JobsList.CellDoubleTapped += OnJobCellDoubleTapped;
        // 行の右クリックのメニューの操作（項目は Core が作る。No.89）と、デプロイの一覧の行のダブルクリック（その行だけを選んで詳細を出す）
        _deploymentsTable.RowMenu = rows => deployments.MenuFor([.. rows.OfType<DeploymentRowViewModel>()]);
        _jobsTable.RowMenu = rows => jobs.MenuFor([.. rows.OfType<JobViewModel>()], viewModel.OpenJobAsync, viewModel.SignIn.IsSignedIn);
        deployments.BatchEditRequested += () => DispatcherQueue.TryEnqueue(() => BatchExpiryDate.Focus(FocusState.Programmatic));
        jobs.RescheduleRequested += () => DispatcherQueue.TryEnqueue(() => JobsScheduleDate.Focus(FocusState.Programmatic));
        DeploymentsTable.CellDoubleTapped += (_, e) =>
        {
            if (e.Item is DeploymentRowViewModel row)
                deployments.SetSelection([row]);
        };
        PrepareDetailText(DeploymentDetail);
        _deploymentsTable.BindText(DeploymentsText);
        _jobsTable.BindText(JobsText);
        CreateTray();
        _ = viewModel.StartAsync(atStartup);
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

    // スペックのスライダー（No.86）: 目盛りの位置を CPU の段に入れる（Slider の値は double、段は int のため、TwoWay で結べない）
    private void OnSpecStepChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var spec = (string)((FrameworkElement)sender).Tag switch
        {
            "TtSpec" => ViewModel.Settings.TtSpec,
            "DbSpec" => ViewModel.Settings.DbSpec,
            _ => ViewModel.Settings.RedisSpec,
        };
        spec.Step = (int)Math.Round(e.NewValue);
    }

    // デプロイの一覧の料金の目安の単位（円・USD。No.87）
    private void OnDeploymentsCurrencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is Core.Pricing.CurrencyOption option)
            ViewModel.Deployments.Costs.Currency = option;
    }

    // 料金の目安の単位（円・USD）
    private void OnCurrencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is Core.Pricing.CurrencyOption option)
            ViewModel.Cost.Currency = option;
    }

    private void OnExpiryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is ExpiryOption option)
            ViewModel.Settings.Expiry = option;
    }

    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Settings.SelectedTemplate = ((ComboBox)sender).SelectedItem as DeploymentTemplate;

    // 削除・実行状況の画面の「デプロイの一覧に戻る」
    private void OnBackToList(object sender, RoutedEventArgs e) => Navigate(Pages.Deployments);

    // 表の上のフィルタの対象の列（ComboBox の選択を ViewModel の型に戻す）
    private void OnDeploymentTextTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is TextFilterTarget<DeploymentEntry> target)
            ViewModel.Deployments.Filters.TextTarget = target;
    }

    private void OnDeploymentDateColumnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is TableColumn<DeploymentEntry> column)
            ViewModel.Deployments.Filters.DateColumn = column;
    }

    private void OnJobTextTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is TextFilterTarget<JobViewModel> target)
            ViewModel.JobList.Filters.TextTarget = target;
    }

    private void OnJobDateColumnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is TableColumn<JobViewModel> column)
            ViewModel.JobList.Filters.DateColumn = column;
    }

    // 表の「列の幅と順序を初期値に戻す」
    private void OnResetDeploymentsLayout(object sender, RoutedEventArgs e) => _deploymentsTable?.Reset();

    private void OnResetJobsLayout(object sender, RoutedEventArgs e) => _jobsTable?.Reset();

    // 表で選んだ行を ViewModel に伝える（表の SelectedItems は結び付けられないため。Core の SelectionSync）
    private void OnDeploymentsSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.Deployments.TableSelection.ViewChanged();

    private void OnJobsSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.JobList.TableSelection.ViewChanged();

    // ジョブの一覧の「詳細」と、行のダブルクリック: 実行状況の画面で開く
    private void OnShowJobDetail(object sender, RoutedEventArgs e)
    {
        if (ViewModel.JobList.Current is { } job)
            _ = ViewModel.OpenJobAsync(job);
    }

    // 行のダブルクリックで、そのジョブの実行状況の画面を開く。表のセルは、読み取り専用の表ではダブルクリックを処理済みにして外に伝えないため
    // （TableViewCell.OnDoubleTapped）、表の CellDoubleTapped で受ける（モックの UI テストで分かった。desktop-ui-tests.md の 6.3）
    private void OnJobCellDoubleTapped(object? sender, WinUI.TableView.TableViewCellDoubleTappedEventArgs e)
    {
        if (e.Item is JobViewModel job)
            _ = ViewModel.OpenJobAsync(job);
    }

    // 右の詳細の文字を範囲選択できるようにし（パスワードは除く）、右クリックで「選んだ文字をコピー」「詳細をコピー」を出す（No.89）
    private void PrepareDetailText(FrameworkElement detail)
    {
        TextBlock? source = null;
        var selected = new MenuFlyoutItem { Text = "選んだ文字をコピー" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(selected, "Detail.CopySelection");
        selected.Click += (_, _) =>
        {
            if (ViewModel.Deployments.Current is { } row && source != null)
                _ = row.CopySelectedTextAsync(source.SelectedText);
        };
        var all = new MenuFlyoutItem { Text = "詳細をコピー" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(all, "Detail.CopyAll");
        all.Click += (_, _) => _ = ViewModel.Deployments.Current?.CopyDetailCommand.ExecuteAsync();
        var menu = new MenuFlyout { Items = { selected, all } };
        menu.Opening += (_, _) =>
        {
            source = menu.Target as TextBlock;
            selected.IsEnabled = source is { SelectedText.Length: > 0 };
        };
        detail.ContextFlyout = menu;
        foreach (var text in TextBlocksIn(detail))
        {
            if (!text.IsTextSelectionEnabled && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(text) is "Row.DbPassword" or "Row.TrackerPassword")
                continue;
            text.IsTextSelectionEnabled = true;
            text.SelectionFlyout = null;
            text.ContextFlyout = menu;
        }
    }

    // 詳細の中の文字（パネル・枠の子。テンプレートは使っていない）
    private static IEnumerable<TextBlock> TextBlocksIn(DependencyObject element) => element switch
    {
        TextBlock text => new[] { text },
        Panel panel => panel.Children.SelectMany(TextBlocksIn),
        Border { Child: { } child } => TextBlocksIn(child),
        _ => Enumerable.Empty<TextBlock>(),
    };

    // 実行状況の画面の「戻る」: 開いた画面（ジョブの一覧かデプロイの一覧）に戻る
    private void OnBackFromStatus(object sender, RoutedEventArgs e) => Navigate(ViewModel.ReturnPage);

    private void OnPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is int size)
            ViewModel.Deployments.PageSize = size;
    }

    private void OnJobPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is int size)
            ViewModel.JobList.PageSize = size;
    }

    private void OnConcurrencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is int concurrency)
            ViewModel.Queue.MaxConcurrency = concurrency;
    }

    // 上部の帯の「ジョブを見る」
    private void OnShowJobs(object sender, RoutedEventArgs e) => Navigate(Pages.Jobs);

    // 件数のカードの「絞り込む」（No.86）: 押したカードの条件で絞り込む。同じカードをもう一度押すと外す（ジョブの一覧と同じ）
    private void OnDeploymentsQuickFilter(object sender, RoutedEventArgs e)
    {
        var filter = Enum.Parse<DeploymentQuickFilter>((string)((FrameworkElement)sender).Tag);
        ViewModel.Deployments.QuickFilter = ViewModel.Deployments.QuickFilter == filter ? DeploymentQuickFilter.All : filter;
    }

    private void OnJobsQuickFilter(object sender, RoutedEventArgs e)
    {
        var filter = Enum.Parse<JobQuickFilter>((string)((FrameworkElement)sender).Tag);
        ViewModel.JobList.QuickFilter = ViewModel.JobList.QuickFilter == filter ? JobQuickFilter.All : filter;
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // メニューに無い画面（削除・実行状況）を開いたときは、メニューの選択を外すだけ
        if ((args.SelectedItem as NavigationViewItem)?.Tag is string tag)
            ShowPanel(tag);
    }

    /// <summary>画面を開く（メニューにある画面はメニューを選び、削除・実行状況はメニューの選択を外して開く。docs/design/desktop/desktop-wizard.md の 2）。</summary>
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

    // 画面は、Tag が画面の名前（Pages）のもの（XAML の対応は単体テスト XamlContractTests で確かめる）
    private void ShowPanel(string tag)
    {
        foreach (var panel in PagesHost.Children.OfType<FrameworkElement>())
            panel.Visibility = (string)panel.Tag == tag ? Visibility.Visible : Visibility.Collapsed;
        _ = ViewModel.PageShownAsync(tag);
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

    /// <summary>通知を押したとき: ウィンドウを出し、開く画面は ViewModel が決める（MainViewModel.OpenFromNotificationAsync）。</summary>
    public void ShowFromNotification(IReadOnlyDictionary<string, string> arguments)
    {
        ShowFromTray();
        _ = ViewModel.OpenFromNotificationAsync(arguments);
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
            // System.Drawing.Icon で読むため、ICO（BMP の形式。オーナーのアイコン docs/design/desktop/icon/TTDeployTool.ico の各サイズを BMP にしたもの。No.89）にする
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
