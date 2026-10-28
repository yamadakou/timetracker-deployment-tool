using System.Diagnostics.Tracing;
using Azure.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TTDeployTool.Controller.Desktop.Core.Diagnostics;

/// <summary>
/// 診断用のログ（詳細設計 docs/design/desktop/desktop-app.md の 7.2）。アプリのデータの置き場所の logs に、日ごとのファイルで書く。
/// サインインやサブスクリプションの読み込みで何が失敗したかを、利用者の PC で後から確かめるため。
/// ファイルはこの PC にだけ置き、送信しない。パスワードとトークンは書かない（Azure SDK も既定でトークンと PII を出さない）。
/// </summary>
public sealed class DiagnosticLog : ILoggerFactory
{
    /// <summary>この日数より古いログのファイルは、起動時に消す。</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private const string FilePrefix = "ttdeploytool-desktop-";
    private const string AzureIdentitySource = "Azure-Identity";

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    public DiagnosticLog(string directory, TimeProvider time)
    {
        System.IO.Directory.CreateDirectory(directory);
        Directory = directory;
        _time = time;
    }

    /// <summary>ログのファイルを置くフォルダー（画面から開けるようにする）。</summary>
    public string Directory { get; }

    /// <summary>今日のログのファイル。</summary>
    /// <summary>ファイルを消す処理（テストで、消せない場合を作るために差し替える）。</summary>
    internal Action<string> DeleteFile { get; set; } = File.Delete;

    public string CurrentFile => Path.Combine(Directory, $"{FilePrefix}{_time.GetLocalNow():yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>ほかの出力先は使わない（このログはファイルにだけ書く）。</summary>
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    public void Dispose()
    {
    }

    /// <summary>保存期間を過ぎたログのファイルを消す（消せないものは残す）。</summary>
    public void DeleteOld()
    {
        var limit = _time.GetUtcNow().UtcDateTime - Retention;
        foreach (var file in System.IO.Directory.GetFiles(Directory, $"{FilePrefix}*.log"))
        {
            if (File.GetLastWriteTimeUtc(file) >= limit)
                continue;
            try
            {
                DeleteFile(file);
            }
            catch (Exception)
            {
                // ほかのプロセスが開いているなど。次回の起動で消す
            }
        }
    }

    /// <summary>
    /// Azure SDK のイベント（EventSource）をこのログに書く。Azure.Identity（サインインとトークンの取得）は情報も、
    /// それ以外（Azure.Core の HTTP など）は警告とエラーだけを書く。戻り値を破棄すると止まる。
    /// </summary>
    public IDisposable ForwardAzureEvents()
    {
        var logger = CreateLogger("Azure");
        return new AzureEventSourceListener(
            (e, message) => Forward(logger, e.EventSource.Name, e.Level, message),
            EventLevel.Informational);
    }

    internal static void Forward(ILogger logger, string source, EventLevel level, string message)
    {
        if (level > EventLevel.Warning && source != AzureIdentitySource)
            return;
        logger.Log(ToLogLevel(level), "{Source}: {Message}", source, message);
    }

    internal static LogLevel ToLogLevel(EventLevel level) => level switch
    {
        EventLevel.Critical => LogLevel.Critical,
        EventLevel.Error => LogLevel.Error,
        EventLevel.Warning => LogLevel.Warning,
        EventLevel.Verbose => LogLevel.Debug,
        _ => LogLevel.Information,
    };

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = $"{_time.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {category}: {message}{Environment.NewLine}";
        if (exception != null)
            line += exception.ToString() + Environment.NewLine;
        lock (_lock)
        {
            try
            {
                File.AppendAllText(CurrentFile, line);
            }
            catch (Exception)
            {
                // ログを書けなくても、アプリの動作は止めない
            }
        }
    }

    private sealed class FileLogger(DiagnosticLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                log.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
