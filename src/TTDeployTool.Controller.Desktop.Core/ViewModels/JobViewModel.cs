using System.Collections.ObjectModel;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Mvvm;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// 実行中・実行したジョブ（デプロイ・削除）の進み具合、ログ、結果。実行中のジョブはキャンセルできない
/// （Azure の操作を途中で止めると中途半端な状態が残るため。全体設計 4.8）。
/// </summary>
public sealed class JobViewModel : ObservableObject
{
    /// <summary>状態を読み直す間隔（ジョブが終われば待たずに読む）。</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly Job _job;
    private int _logOffset;
    private JobSnapshot _snapshot = null!;

    public JobViewModel(Job job)
    {
        _job = job;
        Refresh();
    }

    public string Title => _job.Kind == JobKind.Deploy
        ? $"デプロイ（{_job.Target.ResourceGroup} / {_job.Target.AppName}）"
        : $"削除（{_job.Target.ResourceGroup} / {_job.Target.AppName}）";

    public ObservableCollection<string> Logs { get; } = [];

    public bool IsRunning => _snapshot.Status == JobStatus.Running;

    public string StatusText => _snapshot.Status switch
    {
        JobStatus.Running => "実行中",
        JobStatus.Succeeded => "完了",
        _ => "失敗",
    };

    public string? StepText => _snapshot.CurrentStep is { } step ? $"{step.Index} / {step.Total}: {step.Name}" : null;

    /// <summary>失敗した場合のメッセージと、次の手順。</summary>
    public string? Error => _snapshot.Error is { } error
        ? $"{error}\n途中まで作成・削除したリソースは、もう一度実行すると続きから処理します（状態は「状態の確認」で見られます）。"
        : null;

    /// <summary>デプロイが成功した場合の Timetracker の URL。</summary>
    public Uri? Endpoint => _snapshot.Result is DeploymentResult { TimetrackerUrl: { } url } ? new Uri(url) : null;

    /// <summary>デプロイが成功し、DB のデータを永続化した場合の保存先。</summary>
    public string? PersistenceSummary => _snapshot.Result is DeploymentResult { StorageAccountName: { } account }
        ? $"DB のデータはストレージアカウント {account}（Azure Files）に保存しています。"
        : null;

    /// <summary>削除が成功した場合の結果。</summary>
    public string? DeletionSummary => _snapshot.Result is DeletionOutcome d
        ? $"削除したもの: {Join(d.Deleted)}。既に無かったもの: {Join(d.AlreadyDeleted)}。リソースグループ {d.ResourceGroup} は残しています。"
        : null;

    /// <summary>ジョブが終わるまで状態を読み直す（呼んだスレッドで画面の項目を更新する）。</summary>
    public async Task WatchAsync(TimeProvider time, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        while (IsRunning)
        {
            await Task.WhenAny(_job.Completion, Task.Delay(interval, time, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            Refresh();
        }
    }

    // 前回の続きのログだけを足す
    private void Refresh()
    {
        _snapshot = _job.Snapshot(_logOffset);
        foreach (var log in _snapshot.Logs)
            Logs.Add($"{log.At.ToLocalTime():HH:mm:ss} {log.Message}");
        _logOffset = _snapshot.NextLogOffset;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(PersistenceSummary));
        OnPropertyChanged(nameof(DeletionSummary));
    }

    private static string Join(IReadOnlyList<string> names) => names.Count == 0 ? "なし" : string.Join(", ", names);
}
