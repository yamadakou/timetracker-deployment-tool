using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Azure.Identity;
using Azure.Identity.Broker;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.AppNotifications;
using TTDeployTool.Controller.Desktop.Services;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// Windows との連携を、今の実装（WinUI 3 のアプリの Services のファイル）のまま WPF のホストで使えるかを確かめる（desktop-blazor-hybrid.md の 8）。
/// 実機の操作・本物の Azure は使わない: WAM はハンドルを渡して資格情報を作るところまで（トークンは取らない）、通知は出さずに使えるかと許可の状態を読むだけ。
/// 結果は項目ごとに値か「error: …」を書く（1 つが失敗しても、ほかを確かめる）。
/// </summary>
internal static class IntegrationCheck
{
    public static async Task WriteAsync(MainWindow window, string path)
    {
        var report = new Dictionary<string, object?>
        {
            ["packaged"] = HostSettings.IsPackaged,
            ["dataDirectory"] = HostSettings.DataDirectory,
        };
        var hwnd = new WindowInteropHelper(window).Handle;
        Try(report, "windowHandle", () => hwnd != IntPtr.Zero);
        Try(report, "wamSignInService", () =>
        {
            // WinUI のアプリと同じサインインの実装に、WPF の窓のハンドルを渡す
            _ = new WamSignInService(hwnd, null, HostSettings.DataDirectory);
            _ = new InteractiveBrowserCredential(new InteractiveBrowserCredentialBrokerOptions(hwnd));
            return true;
        });
        Try(report, "webView2Runtime", () => CoreWebView2Environment.GetAvailableBrowserVersionString());
        Try(report, "trayIcon", () => window.Tray.IsCreated);
        Try(report, "appNotificationsSupported", () => AppNotificationManager.IsSupported());
        Try(report, "notificationPermission", () => new AppNotificationService().Permission.ToString());
        report["startupTask"] = await TryAsync(async () => (await new StartupTaskService().GetStatusAsync()).ToString());
        Try(report, "dpapi", () =>
        {
            var protector = new DpapiSecretProtector();
            return protector.Unprotect(protector.Protect("試作の確認")) == "試作の確認";
        });
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Try(Dictionary<string, object?> report, string name, Func<object?> check)
    {
        try
        {
            report[name] = check();
        }
        catch (Exception ex)
        {
            report[name] = $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static async Task<object?> TryAsync(Func<Task<object?>> check)
    {
        try
        {
            return await check();
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
