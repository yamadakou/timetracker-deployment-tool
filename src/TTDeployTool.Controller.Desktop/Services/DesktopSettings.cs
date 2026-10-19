using System.Reflection;

namespace TTDeployTool.Controller.Desktop.Services;

/// <summary>ビルドで決める設定と、アプリのデータの置き場所。</summary>
internal static class DesktopSettings
{
    /// <summary>
    /// サインインに使うアプリ登録のクライアント ID（ビルドの /p:DesktopClientId）。空なら null（Azure.Identity の既定。開発用）。
    /// </summary>
    public static string? ClientId { get; } = typeof(DesktopSettings).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "DesktopClientId")?.Value is { Length: > 0 } id ? id : null;

    /// <summary>
    /// アプリのデータ（サインインの情報。パスワードは置かない）の置き場所。MSIX ではパッケージごとのフォルダー
    /// （アンインストールで消える）、パッケージにしないで動かした場合は %LOCALAPPDATA% の下。
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            try
            {
                return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            catch (InvalidOperationException)
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TTDeployTool");
                Directory.CreateDirectory(path);
                return path;
            }
        }
    }
}
