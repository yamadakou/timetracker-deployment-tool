using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Fixtures;

/// <summary>
/// UI テストのモックで組み立てる <see cref="MainViewModel"/>（Blazor Hybrid 版のテスト用のビルド・テスト用のホスト・画面の単体テストで使う。docs/design/desktop/desktop-blazor-hybrid.md の 16）。
/// 部品は WinUI 3 のアプリの UI テスト用のビルド（App.xaml.cs の UITEST_MOCK）と同じ: モックのサインインと Azure、通知・既定のアプリで開く・サインイン時の起動は
/// アプリのデータのフォルダーの記録（<see cref="FixtureEvents"/>）に書く。設定（preferences.json）と終わったジョブの記録（job-history.json）もそのフォルダーに置く。
/// </summary>
public static class FixtureApp
{
    /// <param name="data">モックのデータ（null ならアプリのデータの <see cref="FixtureData.FileName"/> を読む。それも無ければ、サインインできない空のデータ）</param>
    public static MainViewModel CreateViewModel(string dataDirectory, IDialogService dialogs, IClipboardService? clipboard, TimeProvider time,
        FixtureData? data = null, ILoggerFactory? loggers = null, ISecretProtector? secrets = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FixtureData.FileName);
        var fixture = data ?? FixtureData.Load(path);
        var events = new FixtureEvents(Path.Combine(dataDirectory, FixtureEvents.FileName));
        var used = fixture ?? new FixtureData(new SignedInAccount("", null), [], [], []);
        return new MainViewModel(new FixtureSignInService(() => fixture ??= FixtureData.Load(path)), new FixtureAzureClients(used), dialogs, new FixtureLauncher(events), time,
            loggers ?? NullLoggerFactory.Instance,
            preferences: new FilePreferences(Path.Combine(dataDirectory, "preferences.json")),
            notifications: new FixtureNotifications(events, used.Notifications),
            clipboard: clipboard,
            startupTask: new FixtureStartupTask(events, used.StartupTask),
            secrets: secrets,
            history: new FileJobHistory(Path.Combine(dataDirectory, "job-history.json")));
    }
}
