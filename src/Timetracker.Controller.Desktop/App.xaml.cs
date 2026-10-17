using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Timetracker.Controller.Desktop.Core.Services;
using Timetracker.Controller.Desktop.Core.ViewModels;
using Timetracker.Controller.Desktop.Services;

namespace Timetracker.Controller.Desktop;

/// <summary>
/// アプリの起動。画面の状態と操作（<see cref="MainViewModel"/>）に、OS の機能（WAM でのサインイン、ダイアログ、既定のアプリで開く）を渡す。
/// </summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var viewModel = new MainViewModel(
            new WamSignInService(hwnd, DesktopSettings.ClientId, DesktopSettings.DataDirectory),
            new AzureClients(),
            new DialogService(_window, hwnd),
            new SystemLauncher(),
            TimeProvider.System,
            NullLoggerFactory.Instance);
        _window.Initialize(viewModel, hwnd);
        _window.Activate();
    }
}
