using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Windows.Services.Store;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// アプリのウィンドウ。表示する項目の切り替え、閉じる前の確認、Store の更新の確認だけを行い、
/// 処理は ViewModel（<see cref="MainViewModel"/>）に任せる。
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _closeConfirmed;

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

    // 実行中のジョブがあれば、閉じる前に確認する（閉じるとジョブは止まる）
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
            return;
        args.Cancel = true;
        if (await ViewModel.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
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
