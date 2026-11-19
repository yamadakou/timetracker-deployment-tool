using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using Azure.Identity;
using Azure.Identity.Broker;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Services;
using Windows.Services.Store;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// WinUI 3 のアプリが Windows に頼っている機能を、WPF のホストで同じように使えるかを確かめる（移行で機能が落ちないか。
/// docs/design/desktop/desktop-blazor-hybrid.md の 8・15）。Windows の実装は、WinUI 3 のアプリの Services のファイルをそのまま使う。
/// 実機の操作・本物の Azure は使わない: WAM はハンドルを渡して資格情報を作るところまで（トークンは取らない）、通知は実際に出して
/// 通知センターに入ったかを読み、UI Automation で通知を押して、アプリに届くかを見る。結果は項目ごとに値か「error: …」を書く（1 つが失敗しても、ほかを確かめる）。
/// </summary>
internal static class IntegrationCheck
{
    private const string NoticeTitle = "TTDeployTool の試作の通知";

    public static async Task WriteAsync(MainWindow window, string path, bool waitForReactivation)
    {
        var report = new Dictionary<string, object?>
        {
            ["packaged"] = HostSettings.IsPackaged,
            ["dataDirectory"] = HostSettings.DataDirectory,
            ["activationKind"] = App.ActivationKind,
        };
        var hwnd = window.Handle;
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
        Try(report, "trayToolTip", () => window.Tray.ToolTipText);
        Try(report, "dpapi", () =>
        {
            var protector = new DpapiSecretProtector();
            return protector.Unprotect(protector.Protect("試作の確認")) == "試作の確認";
        });
        Try(report, "folderDialog", () => new Microsoft.Win32.OpenFolderDialog { Title = "フォルダーを選ぶ" }.Title.Length > 0);
        report["shellRendered"] = await TryAsync(async () => await ShellRenderedAsync(window));
        // 入力欄の右クリックのメニュー（貼り付け）を出せる設定になっていること（EditMenu）
        Try(report, "editContextMenu", () => window.Core?.Settings.AreDefaultContextMenusEnabled);

        // 閉じたときの動き（ジョブも期限のあるデプロイも無ければ終了する。WinUI のアプリと同じ Core の判断）と、隠したウィンドウを通知領域から出せること
        Try(report, "closeAction", () => window.Session.ViewModel.RequestClose().ToString());
        Try(report, "trayRestoresWindow", () =>
        {
            window.Hide();
            var hidden = !window.IsVisible;
            window.ShowFromTray();
            return hidden && window.IsVisible;
        });

        // 通知: WinUI のアプリと同じ実装で出し、許可の状態を読み、通知センターに入ったかを確かめる
        Try(report, "notificationPermission", () => new AppNotificationService().Permission.ToString());
        Try(report, "notificationSetting", () => AppNotificationManager.Default.Setting.ToString());
        // 比べるため、OS の通知の API（WinRT の ToastNotificationManager。パッケージの ID で使う）でも、通知を出せるかを読む
        Try(report, "toastNotifierSetting", () => Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier().Setting.ToString());
        Try(report, "notificationShown", () =>
        {
            new AppNotificationService().Show(new AppNotice(NoticeTitle, "デプロイが終わりました（試作の確認）。", JobId: "job-check", Page: TTDeployTool.Controller.Desktop.Core.ViewModels.Pages.Status));
            return true;
        });
        report["notificationsInCenter"] = await TryAsync(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                var all = await AppNotificationManager.Default.GetAllAsync();
                if (all.Count > 0)
                    return all.Count;
                await Task.Delay(500);
            }
            return 0;
        });
        // 通知を UI Automation で押し、アプリの NotificationInvoked に届くか（押すとウィンドウを出し、引数を受け取る）
        window.Hide();
        report["notificationClick"] = await TryAsync(async () => await ClickToastAsync());
        for (var i = 0; i < 20 && App.InvokedNotifications.Count == 0; i++)
            await Task.Delay(500);
        report["notificationInvoked"] = App.InvokedNotifications.ToList();
        report["windowShownByNotification"] = window.IsVisible;
        window.ShowFromTray();
        await TryAsync(async () =>
        {
            await AppNotificationManager.Default.RemoveAllAsync();
            return true;
        });

        // サインイン時の起動: 状態を読み、有効にして、元に戻す（パッケージが要る）
        report["startupTask"] = await TryAsync(async () =>
        {
            var service = new StartupTaskService();
            var before = await service.GetStatusAsync();
            if (before == StartupTaskStatus.Unavailable)
                return before.ToString();
            var enabled = await service.EnableAsync();
            await service.DisableAsync();
            return $"{before} → 有効にする: {enabled} → 無効にする: {await service.GetStatusAsync()}";
        });

        // Store の更新の確認（WinUI のアプリの CheckForUpdatesAsync と同じ呼び方。Store から入れていなければ 0 件か誤り）
        report["storeUpdates"] = await TryAsync(async () =>
        {
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, hwnd);
            return (await context.GetAppAndOptionalStorePackageUpdatesAsync()).Count;
        });

        // 画面（WebView2）が止まったときの戻し方（オーナーの実機の確認、2026-11-17: ロックから戻ると真っ白で「応答なし」）。
        // 画面が応えるかの確かめ（ロックの解除・復帰で動くもの）、画面を描くプロセスを落としたら読み直して描き直すか、WebView を作り直して描き直すか
        report["responsiveCheck"] = await TryAsync(async () => await window.CheckResponsiveAsync());
        // ロックから戻った後の描き直し（画面をいったん隠して出し直す）の後も、画面が描かれて見えていること。WebView2 の起動の引数
        report["repainted"] = await TryAsync(async () => window.Repaint() && await ShellRenderedAsync(window) is true && await window.CheckResponsiveAsync() == "応えました（ページの見え方: visible）");
        report["browserArguments"] = await TryAsync(async () => await window.Core!.ExecuteScriptAsync("navigator.userAgent") is { Length: > 0 } ? MainWindow.BrowserArguments : null);
        report["rendererCrashRecovered"] = await TryAsync(async () =>
        {
            var before = window.Recoveries;
            // CDP の Page.crash で、画面を描くプロセスを落とす（ProcessFailed の RenderProcessExited が来る）
            _ = window.Core!.CallDevToolsProtocolMethodAsync("Page.crash", "{}");
            for (var i = 0; i < 40 && window.Recoveries == before; i++)
                await Task.Delay(500);
            return window.Recoveries > before && await ShellRenderedAsync(window) is true;
        });
        report["webViewRecreated"] = await TryAsync(async () =>
        {
            window.RecreateWebView();
            return window.Recreations == 1 && await ShellRenderedAsync(window) is true && window.Core?.Settings.AreDefaultContextMenusEnabled == true;
        });
        // 長いロックの後: 画面を作り直し、開いていた画面（ジョブの一覧）から描く
        report["recoveredAfterLongLock"] = await TryAsync(async () =>
        {
            await window.Core!.ExecuteScriptAsync("document.querySelector(\"[data-id='Nav.Jobs']\").click()");
            if (!await RenderedAsync(window, "Panel.Jobs"))
                return "ジョブの一覧を開けない";
            var shows = window.OverlayShows;
            var result = await window.CheckAfterAsync("確認のための長いロック", MainWindow.LongLock);
            // 作り直す間は「画面を読み直しています…」を出し、終わったら外す
            if (window.OverlayShows != shows + 1 || window.IsOverlayShown)
                return $"読み直しの表示が出ない・外れない（{window.OverlayShows - shows} 回・今 {window.IsOverlayShown}）";
            return window.Recreations == 2 && await RenderedAsync(window, "Panel.Jobs") ? result : $"作り直した後にジョブの一覧が無い（{result}）";
        });

        // 2 つ目の起動が 1 つ目に渡り、ウィンドウを出す（CI が印を見て 2 つ目を起動する）
        if (waitForReactivation)
        {
            window.Hide();
            await File.WriteAllTextAsync(Path.Combine(HostSettings.DataDirectory, App.CheckWaiting), "");
            for (var i = 0; i < 120 && App.Reactivations == 0; i++)
                await Task.Delay(500);
            report["reactivations"] = App.Reactivations;
            report["windowShownBySecondLaunch"] = window.IsVisible;
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    // 画面（Razor）がサインインの画面を描いたか（MSIX にまとめたとき、画面のファイルがパッケージに入っているか）。60 秒まで待つ
    private static async Task<object?> ShellRenderedAsync(MainWindow window) => await RenderedAsync(window, "Panel.Target");

    // 画面に data-id の要素が描かれたか。60 秒まで待つ
    private static async Task<bool> RenderedAsync(MainWindow window, string id)
    {
        for (var i = 0; i < 120; i++)
        {
            if (window.Core is { } core && await core.ExecuteScriptAsync($"document.querySelector(\"[data-id='{id}']\") !== null") == "true")
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    // 画面に出た通知（トースト）を、名前に通知の題を含む要素から探して押す。見つからなければ、見えた最上位の窓の名前を返す（調べるため）
    private static async Task<object?> ClickToastAsync()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            var found = await Task.Run(() =>
            {
                foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition))
                {
                    seen.Add($"{top.Current.ClassName}: {top.Current.Name}");
                    if (top.Current.ClassName != "Windows.UI.Core.CoreWindow")
                        continue;
                    foreach (AutomationElement element in top.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                    {
                        if (!element.Current.Name.Contains(NoticeTitle))
                            continue;
                        for (var target = element; target != null; target = TreeWalker.ControlViewWalker.GetParent(target))
                        {
                            if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                            {
                                ((InvokePattern)pattern).Invoke();
                                return $"押した（{target.Current.ControlType.ProgrammaticName}: {target.Current.Name}）";
                            }
                        }
                    }
                }
                return null;
            });
            if (found != null)
                return found;
            await Task.Delay(500);
        }
        return $"通知が見つからない（最上位の窓: {string.Join(" / ", seen.Where(s => s.StartsWith("Windows.UI.Core.CoreWindow")))}）";
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
