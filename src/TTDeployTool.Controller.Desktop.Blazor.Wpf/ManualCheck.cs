using System.Text;
using System.Windows;
using Microsoft.Windows.AppNotifications;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Services;
using Windows.UI.Notifications;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// オーナーの実機での確認のための、通知領域のメニュー（docs/design/desktop/desktop-blazor-hybrid-check.md）。CI のランナーには通知を画面に出す仕組みが無いため、
/// 通知を出す・押してウィンドウが出る・サインイン時の起動は、ここから人が試す（desktop-blazor-hybrid.md の 15.1）。
/// 通知は、WinUI 3 のアプリと同じ実装（Windows App SDK）と、使えなかったときの代わり（OS の通知の API）の 2 つで出す。
/// </summary>
internal static class ManualCheck
{
    /// <summary>メニューの項目（見出しと、押したときの処理）。</summary>
    public static IReadOnlyList<(string Header, Func<Task> Run)> Items(MainWindow window) =>
    [
        ("通知を試す（今のアプリと同じ: Windows App SDK）", () =>
        {
            new AppNotificationService().Show(new AppNotice("試作の通知（Windows App SDK）", "この通知を押すと、試作のウィンドウが出ます。", JobId: "job-manual", Page: "status"));
            return Task.CompletedTask;
        }),
        ("通知を試す（代わり: OS の通知の API）", () =>
        {
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var texts = xml.GetElementsByTagName("text");
            texts[0].InnerText = "試作の通知（OS の通知の API）";
            texts[1].InnerText = "この通知を押すと、試作のウィンドウが出ます。";
            var toast = new ToastNotification(xml);
            toast.Activated += (_, _) => window.Dispatcher.InvokeAsync(() => Opened(window, "OS の通知の API", ""));
            ToastNotificationManager.CreateToastNotifier().Show(toast);
            return Task.CompletedTask;
        }),
        ("サインイン時の起動を切り替える", async () =>
        {
            var service = new StartupTaskService();
            var now = await service.GetStatusAsync();
            if (now == StartupTaskStatus.Enabled)
                await service.DisableAsync();
            else
                await service.EnableAsync();
            MessageBox.Show(window, $"サインイン時の起動: {now} → {await service.GetStatusAsync()}", "TTDeployTool（試作）");
        }),
        ("状態を見る", async () => MessageBox.Show(window, await StatusAsync(), "TTDeployTool（試作）")),
    ];

    /// <summary>通知を押してウィンドウが出たとき（どちらの通知からかと、引数を出す）。</summary>
    public static void Opened(MainWindow window, string source, string arguments)
    {
        window.ShowFromTray();
        MessageBox.Show(window, $"通知から開きました（{source}）。{arguments}", "TTDeployTool（試作）");
    }

    private static async Task<string> StatusAsync()
    {
        var text = new StringBuilder();
        text.AppendLine($"起動の種類: {App.ActivationKind}（サインイン時の起動なら StartupTask）");
        text.AppendLine($"パッケージ: {(HostSettings.IsPackaged ? "MSIX" : "パッケージにしない")}");
        text.AppendLine($"通知の許可（今のアプリと同じ: Windows App SDK）: {Read(() => AppNotificationManager.Default.Setting.ToString())}");
        text.AppendLine($"通知の許可（OS の通知の API）: {Read(() => ToastNotificationManager.CreateToastNotifier().Setting.ToString())}");
        text.AppendLine($"通知センターの、この試作の通知: {await ReadAsync(async () => (await AppNotificationManager.Default.GetAllAsync()).Count.ToString())} 件");
        text.AppendLine($"サインイン時の起動: {await ReadAsync(async () => (await new StartupTaskService().GetStatusAsync()).ToString())}");
        text.AppendLine($"押された通知（Windows App SDK）: {(App.InvokedNotifications.Count == 0 ? "なし" : string.Join(" / ", App.InvokedNotifications))}");
        return text.ToString();
    }

    private static string Read(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"読めない（{ex.GetType().Name}: {ex.Message}）";
        }
    }

    private static async Task<string> ReadAsync(Func<Task<string>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception ex)
        {
            return $"読めない（{ex.GetType().Name}: {ex.Message}）";
        }
    }
}
