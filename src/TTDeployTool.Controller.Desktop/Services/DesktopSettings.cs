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
    /// アプリのデータ（設定と予定の preferences.json・終わったジョブの記録の job-history.json・サインインの情報・診断用のログ。
    /// パスワードは、残すと選んだデプロイの予定だけ DPAPI で暗号化して置く）の置き場所。MSIX ではパッケージごとのフォルダー
    /// （LocalState。アンインストールで消える）、パッケージにしないで動かした場合は %LOCALAPPDATA%\TTDeployTool
    /// （docs/design/desktop/desktop-app.md の 7.3）。
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
