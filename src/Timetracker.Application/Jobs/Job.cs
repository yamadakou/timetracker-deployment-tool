namespace Timetracker.Application.Jobs;

public enum JobKind
{
    Deploy,
    Delete,
}

public enum JobStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>ジョブの対象。同じ対象のジョブは同時に 1 つだけ実行する（大文字小文字は区別しない）。</summary>
public sealed record JobTarget(string Subscription, string ResourceGroup, string AppName)
{
    internal string Key => $"{Subscription}/{ResourceGroup}/{AppName}".ToLowerInvariant();
}

/// <summary>ジョブのログ 1 件。<see cref="Level"/> は info / verbose / warn / success / error。</summary>
public sealed record JobLogEntry(string Level, string Message, DateTimeOffset At);

/// <summary>実行中の手順（<see cref="Index"/> は 1 始まり）。</summary>
public sealed record JobStep(int Index, int Total, string Name);

/// <summary>ある時点のジョブの状態。<see cref="Logs"/> は指定した位置から後のログ。</summary>
public sealed record JobSnapshot(
    string JobId,
    JobKind Kind,
    JobStatus Status,
    JobTarget Target,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    JobStep? CurrentStep,
    IReadOnlyList<JobLogEntry> Logs,
    int NextLogOffset,
    object? Result,
    string? Error,
    string? DeviceCodeMessage);

/// <summary>
/// デプロイ・削除のジョブ。バックグラウンドの処理と、状態を問い合わせるツールから同時に使われるため、状態はロックの中で読み書きする。
/// </summary>
public sealed class Job
{
    private readonly Lock _lock = new();
    private readonly List<JobLogEntry> _logs = new();
    private JobStatus _status = JobStatus.Running;
    private JobStep? _step;
    private object? _result;
    private string? _error;
    private string? _deviceCodeMessage;
    private DateTimeOffset? _completedAt;

    internal Job(string id, JobKind kind, JobTarget target, string callerId, DateTimeOffset createdAt)
    {
        Id = id;
        Kind = kind;
        Target = target;
        CallerId = callerId;
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public JobKind Kind { get; }
    public JobTarget Target { get; }
    public string CallerId { get; }
    public DateTimeOffset CreatedAt { get; }

    public JobStatus Status
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>処理が終わると完了する（テストや終了処理で待つため）。</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    /// <param name="logOffset">この位置から後のログを返す（範囲外は 0 または末尾に丸める）</param>
    public JobSnapshot Snapshot(int logOffset = 0)
    {
        lock (_lock)
        {
            var offset = Math.Clamp(logOffset, 0, _logs.Count);
            return new JobSnapshot(Id, Kind, _status, Target, CreatedAt, _completedAt, _step,
                _logs.Skip(offset).ToList(), _logs.Count, _result, _error, _deviceCodeMessage);
        }
    }

    internal void AddLog(string level, string message, DateTimeOffset at)
    {
        lock (_lock) _logs.Add(new JobLogEntry(level, message, at));
    }

    internal void SetStep(JobStep step)
    {
        lock (_lock) _step = step;
    }

    internal void SetDeviceCodeMessage(string message)
    {
        lock (_lock) _deviceCodeMessage = message;
    }

    internal void Succeed(object? result, DateTimeOffset at)
    {
        lock (_lock)
        {
            _status = JobStatus.Succeeded;
            _result = result;
            _completedAt = at;
        }
    }

    internal void Fail(string error, DateTimeOffset at)
    {
        lock (_lock)
        {
            _status = JobStatus.Failed;
            // どの手順で失敗したかを示す
            _error = _step == null ? error : $"{_step.Name}（手順 {_step.Index}/{_step.Total}）で失敗しました: {error}";
            _completedAt = at;
        }
    }
}
