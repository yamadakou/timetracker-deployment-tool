using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Blazor.Shell;

/// <summary>
/// 画面（Razor）とホスト（WPF・テスト用のホスト）が共有する状態（docs/design/desktop/desktop-blazor-hybrid.md の 16）。<see cref="MainViewModel"/> はホストが組み立てる
/// （WinUI 3 のアプリの App.xaml.cs と同じ部品。テスト用のビルドだけ Fixtures のモック）。ウィンドウ・通知領域・通知はホストが受け持ち、画面はこれを描く。
/// </summary>
public sealed class DesktopSession : ObservableObject
{
    private readonly Func<MainViewModel, Task> _startCore;
    private Task? _start;
    private bool _updateAvailable;

    /// <param name="start">始めの処理（既定は通常の起動。サインイン時の起動ではホストが替える。テストが、終わるまでの表示を確かめるときにも替える）</param>
    public DesktopSession(MainViewModel viewModel, BlazorDialogService dialogs, IClipboardService? clipboard, Func<MainViewModel, Task>? start = null)
    {
        ViewModel = viewModel;
        Dialogs = dialogs;
        Clipboard = clipboard;
        _startCore = start ?? (vm => vm.StartAsync(atStartup: false));
    }

    public MainViewModel ViewModel { get; }

    public BlazorDialogService Dialogs { get; }

    public IClipboardService? Clipboard { get; }

    /// <summary>Microsoft Store に新しい版がある（ホストが確かめる。上の帯に出す）。</summary>
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set => SetProperty(ref _updateAvailable, value);
    }

    /// <summary>始めの処理（前回のサインインの復元など）。何度呼んでも 1 回だけ。</summary>
    public Task StartAsync() => _start ??= _startCore(ViewModel);
}
