using Microsoft.Extensions.Logging;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.Jobs;

/// <summary>
/// 終わったジョブの記録（docs/design/desktop-jobs.md の 6.8、D29〜D32）。起動したときに、前回までのアプリで終わったジョブを一覧に入れ、
/// ジョブが終わったとき・記録から削除したときに保存する。終わった時刻から 180 日を過ぎたものと、新しい順に 500 件を超えた分は、保存しない。
/// </summary>
public sealed class JobHistory
{
    /// <summary>残す記録の数。</summary>
    public const int MaxRecords = 500;

    /// <summary>記録を残す期間（終わった時刻から）。</summary>
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(180);

    private readonly JobQueue _queue;
    private readonly IJobHistoryFile _file;
    private readonly TimeProvider _time;
    private IReadOnlyList<long> _saved;

    public JobHistory(JobQueue queue, IJobHistoryFile file, TimeProvider time, ILogger logger)
    {
        _queue = queue;
        _file = file;
        _time = time;
        var stored = file.Records;
        var records = Prune(stored);
        _saved = [.. queue.AddRecords(records).Select(j => j.Order)];
        if (records.Count != stored.Count)
            file.Records = records;
        logger.LogInformation("終わったジョブの記録: {Count} 件を読みました（古いもの・上限を超えたもの {Dropped} 件を除きました）", records.Count, stored.Count - records.Count);
        // 登録・消去と、状態の変更（どれも Summary の変更を通知する）で保存する
        queue.Jobs.CollectionChanged += (_, _) => Save();
        queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.Summary))
                Save();
        };
    }

    // 期間を過ぎたものを除き、新しいものから上限まで残す（古い順）
    private List<JobRecord> Prune(IEnumerable<JobRecord> records)
    {
        var since = _time.GetUtcNow() - RetentionPeriod;
        return [.. records.Where(r => r.FinishedAt >= since).OrderBy(r => r.FinishedAt).TakeLast(MaxRecords)];
    }

    // 終わったジョブの組み合わせが変わったときだけ書く（ログを含むため、状態の変更のたびに書き直さない）
    private void Save()
    {
        var finished = _queue.Jobs.Where(j => !j.IsActive && j.FinishedAt != null).OrderBy(j => j.Order).ToList();
        var orders = finished.Select(j => j.Order).ToList();
        if (orders.SequenceEqual(_saved))
            return;
        _saved = orders;
        _file.Records = Prune(finished.Select(j => j.ToRecord()));
    }
}
