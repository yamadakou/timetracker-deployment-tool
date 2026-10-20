using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TTDeployTool.Common;

namespace TTDeployTool.Application.Jobs;

/// <summary>ジョブの保存先。</summary>
public interface IJobStore
{
    void Add(Job job);
    Job? Get(string jobId);
    IReadOnlyList<Job> List();
}

/// <summary>プロセス内のメモリに置くジョブの保存先（stdio ではサーバーの終了で消える）。</summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public void Add(Job job) => _jobs[job.Id] = job;

    public Job? Get(string jobId) => _jobs.GetValueOrDefault(jobId);

    public IReadOnlyList<Job> List() => _jobs.Values.ToList();
}

/// <summary>ジョブの処理に渡すもの: ジョブのログに書くロガーと、手順の通知。</summary>
public sealed class JobContext
{
    private readonly Job _job;

    internal JobContext(Job job, IDeploymentLogger logger)
    {
        _job = job;
        Logger = logger;
    }

    public IDeploymentLogger Logger { get; }

    /// <summary>次の手順を始める。停止を要求されていれば、始めずに止める（<see cref="JobStoppedException"/>）。</summary>
    public void ReportStep(int index, int total, string name)
    {
        if (_job.StopRequested)
            throw new JobStoppedException();
        _job.SetStep(new JobStep(index, total, name));
    }
}

/// <summary>停止の要求により、手順の境目で止めた（ジョブは「停止」になる）。</summary>
public sealed class JobStoppedException() : Exception("停止を要求されたため、次の手順に進みませんでした。");

/// <summary>ジョブを始めた結果。同じ対象のジョブが実行中なら <see cref="Job"/> は null で、<see cref="ConflictingJobId"/> にそのジョブの ID が入る。</summary>
public sealed record JobStartResult(Job? Job, string? ConflictingJobId);

/// <summary>
/// デプロイ・削除をバックグラウンドで実行し、状態を問い合わせられるようにする（MCP の start_* と get_job_status）。
/// 実行中のジョブはキャンセルしない（Azure の操作を途中で止めると中途半端な状態が残るため）。停止の要求（<see cref="Job.RequestStop"/>）は、
/// 手順の境目でだけ受け付ける。
/// </summary>
public sealed class JobManager
{
    private readonly IJobStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _startLock = new();

    public JobManager(IJobStore store, TimeProvider time, ILogger<JobManager> logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <param name="work">ジョブの処理。戻り値がジョブの結果になる。例外を投げるとジョブは失敗する</param>
    public JobStartResult TryStart(JobKind kind, JobTarget target, string callerId, Func<JobContext, Task<object?>> work)
    {
        Job job;
        // 同じ対象のジョブが実行中かの確認と登録を、まとめて行う
        lock (_startLock)
        {
            var running = _store.List().FirstOrDefault(j => j.Target.Key == target.Key && j.Status == JobStatus.Running);
            if (running != null)
                return new JobStartResult(null, running.Id);
            job = new Job(InMemoryPlanStore.NewId("job_"), kind, target, callerId, _time.GetUtcNow());
            _store.Add(job);
        }

        var context = new JobContext(job, new JobLogger(job, _time, _logger));
        job.Completion = Task.Run(async () =>
        {
            try
            {
                job.Succeed(await work(context), _time.GetUtcNow());
            }
            catch (JobStoppedException)
            {
                _logger.LogInformation("ジョブ {JobId} を停止しました", job.Id);
                job.Stop(_time.GetUtcNow());
            }
            catch (Exception ex)
            {
                // スタックトレースはサーバーのログにだけ出す
                _logger.LogError(ex, "ジョブ {JobId} が失敗しました", job.Id);
                job.Fail(ex.Message, _time.GetUtcNow());
            }
        });
        return new JobStartResult(job, null);
    }

    /// <summary>ジョブを返す。ほかの呼び出し元のジョブは存在しないものとして扱う。</summary>
    public Job? Get(string jobId, string callerId) => _store.Get(jobId) is { } job && job.CallerId == callerId ? job : null;

    /// <summary>呼び出し元のジョブを新しい順に返す。</summary>
    public IReadOnlyList<Job> List(string callerId, JobStatus? status = null) =>
        _store.List()
            .Where(j => j.CallerId == callerId && (status == null || j.Status == status))
            .OrderByDescending(j => j.CreatedAt)
            .ToList();
}

/// <summary>共有層のログ（<see cref="IDeploymentLogger"/>）をジョブのログに書き、サーバーのログにも出す。</summary>
internal sealed class JobLogger : IDeploymentLogger
{
    // CredentialFactory のデバイスコード認証のメッセージ。利用者に見せる必要があるため、ジョブの状態にも載せる
    internal const string DeviceCodePrefix = "デバイスコード認証:";

    private readonly Job _job;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public JobLogger(Job job, TimeProvider time, ILogger logger)
    {
        _job = job;
        _time = time;
        _logger = logger;
    }

    public void Info(string message)
    {
        if (message.StartsWith(DeviceCodePrefix, StringComparison.Ordinal))
            _job.SetDeviceCodeMessage(message);
        Write("info", LogLevel.Information, message);
    }

    public void Verbose(string message) => Write("verbose", LogLevel.Debug, message);
    public void Warn(string message) => Write("warn", LogLevel.Warning, message);
    public void Success(string message) => Write("success", LogLevel.Information, message);
    public void Error(string message) => Write("error", LogLevel.Error, message);

    public void Exception(Exception ex)
    {
        _logger.LogError(ex, "[{JobId}] {Message}", _job.Id, ex.Message);
        _job.AddLog("error", ex.Message, _time.GetUtcNow());
    }

    private void Write(string level, LogLevel logLevel, string message)
    {
        _logger.Log(logLevel, "[{JobId}] {Message}", _job.Id, message);
        _job.AddLog(level, message, _time.GetUtcNow());
    }
}
