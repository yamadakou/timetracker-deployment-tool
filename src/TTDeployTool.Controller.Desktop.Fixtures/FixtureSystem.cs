using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Fixtures;

/// <summary>
/// UI テストのモックが、アプリの外の機能の代わりに書く記録（desktop-ui-tests.md の 6.3）。アプリのデータのフォルダーの <see cref="FileName"/> に、
/// 1 行 1 件で「種類（タブ）内容」を書き足す。テストはこれを読んで、通知・開いたフォルダーと URL・サインイン時の起動の登録を確かめる。
/// </summary>
public sealed class FixtureEvents(string path)
{
    public const string FileName = "uitest-events.txt";

    public const string Notice = "通知";

    public const string OpenFolder = "フォルダーを開く";

    public const string OpenUri = "URL を開く";

    public const string StartupTask = "サインイン時の起動";

    private readonly object _lock = new();

    public void Record(string kind, string text)
    {
        lock (_lock)
            File.AppendAllText(path, $"{kind}\t{text.Replace('\n', ' ')}{Environment.NewLine}");
    }

    /// <summary>書いた記録（種類と内容。ファイルが無ければ空）。テストが読む。</summary>
    public static IReadOnlyList<(string Kind, string Text)> Read(string path) =>
        File.Exists(path)
            ? [.. File.ReadAllLines(path).Select(l => l.Split('\t', 2)).Select(p => (p[0], p.Length > 1 ? p[1] : ""))]
            : [];
}

/// <summary>Windows の通知の代わりに、出した通知を記録する（通知の許可の状態は、モックのデータから）。</summary>
public sealed class FixtureNotifications(FixtureEvents events, NotificationPermission permission) : INotificationService
{
    public NotificationPermission Permission => permission;

    public void Show(AppNotice notice) => events.Record(FixtureEvents.Notice, $"{notice.Title}\t{notice.Message}");
}

/// <summary>既定のアプリで開く代わりに、開いたフォルダーと URL を記録する。</summary>
public sealed class FixtureLauncher(FixtureEvents events) : ISystemLauncher
{
    public void OpenFolder(string path) => events.Record(FixtureEvents.OpenFolder, path);

    public void OpenUri(Uri uri) => events.Record(FixtureEvents.OpenUri, uri.ToString());
}

/// <summary>
/// Windows のサインイン時の起動の代わり（Windows に登録しない）。始めの状態はモックのデータから。利用者やポリシーで無効にされていれば、有効にできない。
/// </summary>
public sealed class FixtureStartupTask(FixtureEvents events, StartupTaskStatus initial) : IStartupTaskService
{
    private StartupTaskStatus _status = initial;

    public Task<StartupTaskStatus> GetStatusAsync() => Task.FromResult(_status);

    public Task<StartupTaskStatus> EnableAsync()
    {
        if (_status is StartupTaskStatus.Disabled)
            _status = StartupTaskStatus.Enabled;
        events.Record(FixtureEvents.StartupTask, _status.ToString());
        return Task.FromResult(_status);
    }

    public Task DisableAsync()
    {
        if (_status is StartupTaskStatus.Enabled)
            _status = StartupTaskStatus.Disabled;
        events.Record(FixtureEvents.StartupTask, _status.ToString());
        return Task.CompletedTask;
    }
}
