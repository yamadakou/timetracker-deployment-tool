using System.Reflection;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// ビルドで決める設定と、アプリのデータのフォルダー（パッケージにしたら LocalState、しなければ %LOCALAPPDATA%\TTDeployTool.BlazorPrototype。
/// WinUI 3 のアプリのデータとは分ける。docs/design/desktop/desktop-blazor-hybrid.md の 16）。テスト用のビルド（UITEST_MOCK）だけ、環境変数
/// TT_BLAZOR_DATA_DIRECTORY があれば、そのフォルダーを使う（テストごとにモックのデータを置くため）。
/// </summary>
internal static class HostSettings
{
    public static string DataDirectory { get; } = Resolve();

    /// <summary>サインインに使うアプリ登録のクライアント ID（ビルドの /p:DesktopClientId。WinUI のアプリと同じ）。空なら null。</summary>
    public static string? ClientId { get; } = typeof(HostSettings).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "DesktopClientId")?.Value is { Length: > 0 } id ? id : null;

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
#if UITEST_MOCK
        if (Environment.GetEnvironmentVariable("TT_BLAZOR_DATA_DIRECTORY") is { Length: > 0 } test)
        {
            Directory.CreateDirectory(test);
            return test;
        }
#endif
        var path = IsPackaged
            ? Windows.Storage.ApplicationData.Current.LocalFolder.Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TTDeployTool.BlazorPrototype");
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>フォルダーや URL を、OS の既定のアプリ（エクスプローラー・ブラウザー）で開く（WinUI のアプリの SystemLauncher と同じ）。</summary>
internal sealed class SystemLauncher : ISystemLauncher
{
    public void OpenFolder(string path) => Start(path);

    public void OpenUri(Uri uri) => Start(uri.AbsoluteUri);

    private static void Start(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
