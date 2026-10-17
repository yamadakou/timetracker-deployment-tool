using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Timetracker.Controller.Desktop.Core.Services;
using Windows.Storage.Pickers;

namespace Timetracker.Controller.Desktop.Services;

/// <summary>確認のダイアログと、フォルダーの選択（WinUI 3 のデスクトップアプリでは、ピッカーにウィンドウのハンドルを渡す）。</summary>
internal sealed class DialogService : IDialogService
{
    private readonly Window _window;
    private readonly IntPtr _hwnd;

    public DialogService(Window window, IntPtr hwnd)
    {
        _window = window;
        _hwnd = hwnd;
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _window.Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

/// <summary>フォルダーや URL を、OS の既定のアプリ（エクスプローラー・ブラウザー）で開く。</summary>
internal sealed class SystemLauncher : ISystemLauncher
{
    public void OpenFolder(string path) => Start(path);

    public void OpenUri(Uri uri) => Start(uri.AbsoluteUri);

    private static void Start(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
