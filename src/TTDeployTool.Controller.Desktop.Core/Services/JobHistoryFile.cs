using System.Text.Json;
using System.Text.Json.Serialization;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>
/// 終わったジョブの記録（docs/design/desktop/desktop-jobs.md の 6.8、D30）。「詳細」と「Markdown でコピー」で見られるものと、一覧の日時（登録・変更・実行）を残す。
/// パスワード・トークン・計画の中身は持たない。
/// </summary>
public sealed record JobRecord(
    JobKind Kind,
    string Subscription,
    string ResourceGroup,
    string AppName,
    bool ExpiryDeletion,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset FinishedAt,
    DesktopJobState State,
    string? Error,
    string? Note,
    string? Endpoint,
    string? PersistenceSummary,
    string? ExpirySummary,
    string? DeletionSummary,
    IReadOnlyList<string> Logs,
    DateTimeOffset? RegisteredAt = null,
    DateTimeOffset? ChangedAt = null,
    DateTimeOffset? StartedAt = null)
{
    [JsonIgnore]
    public JobTarget Target => new(Subscription, ResourceGroup, AppName);
}

/// <summary>終わったジョブの記録の保存先（D29）。</summary>
public interface IJobHistoryFile
{
    /// <summary>保存した記録（古い順）。</summary>
    IReadOnlyList<JobRecord> Records { get; set; }
}

/// <summary>保存しない記録（テストと、保存先が無い場合）。</summary>
public sealed class InMemoryJobHistory : IJobHistoryFile
{
    public IReadOnlyList<JobRecord> Records { get; set; } = [];
}

/// <summary>
/// 終わったジョブの記録を JSON のファイル（アプリのデータの job-history.json）に保存する。ログを含むため、設定（preferences.json）とは分ける。
/// 読めない・書けない場合は空で動く（記録が残らないだけで、ジョブは実行できるため）。
/// </summary>
public sealed class FileJobHistory : IJobHistoryFile
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _path;
    private IReadOnlyList<JobRecord> _records;

    public FileJobHistory(string path)
    {
        _path = path;
        _records = Load(path);
    }

    public IReadOnlyList<JobRecord> Records
    {
        get => _records;
        set
        {
            _records = value;
            Save();
        }
    }

    private static IReadOnlyList<JobRecord> Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(path), Options) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_records, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても、このアプリを終了するまでは一覧に残る
        }
    }
}
