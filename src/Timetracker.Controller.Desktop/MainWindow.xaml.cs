using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Timetracker.Controller.Desktop.Core.ViewModels;
using Windows.Services.Store;

namespace Timetracker.Controller.Desktop;

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

    // x:Bind の TwoWay で、ComboBox の選択（object）を ViewModel の型に戻す
    private void SetSubscription(object value) => ViewModel.Target.SelectedSubscription = value as SubscriptionChoice;

    private void SetDbType(object value) => ViewModel.Settings.DbType = (string)value;

    private void SetPersistDb(object value) => ViewModel.Settings.PersistDb = (PersistDbOption)value;

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
