using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>試作のアプリのデータのフォルダー（パッケージにしたら LocalState、しなければ %LOCALAPPDATA%\TTDeployTool.BlazorPrototype。本番のアプリのデータとは分ける）。</summary>
internal static class HostSettings
{
    public static string DataDirectory { get; } = Resolve();

    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current.Id;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static string Resolve()
    {
        var path = IsPackaged
            ? Windows.Storage.ApplicationData.Current.LocalFolder.Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TTDeployTool.BlazorPrototype");
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>WPF のクリップボード。</summary>
internal sealed class WpfClipboard : IClipboardService
{
    public void CopyText(string text) => System.Windows.Clipboard.SetText(text);
}
