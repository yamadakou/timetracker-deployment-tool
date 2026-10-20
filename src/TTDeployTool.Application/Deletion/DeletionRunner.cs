using Azure;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Application.Deletion;

/// <summary>削除した結果。<see cref="Deleted"/> が false の場合、計画の作成後に既に削除されていた。</summary>
public sealed record DeletionResult(string Name, DeletionResourceKind Kind, bool Deleted);

/// <summary>
/// 削除計画で「削除する」となったリソースを、計画の順に削除する。
/// 確認の入力を待つ間や前のリソースの削除中に状態が変わることがあるため、各リソースの削除の直前に、
/// 現在の状態に計画と同じ規則を当てはめて確かめ直す。削除の対象でなくなっていれば（管理タグが外された、
/// 管理タグの無い同じ名前のリソースに置き換えられた、Environment 内に削除しない Container App が加わったなど）、
/// そこで中止する。
/// </summary>
public sealed class DeletionRunner
{
    /// <summary>リソースを削除した直後は一覧に反映されないことがあるため、リソースグループが空になるまで待つ上限。</summary>
    public static readonly TimeSpan EmptyWaitTimeout = TimeSpan.FromMinutes(2);

    /// <summary>リソースグループが空になったかを読み直す間隔。</summary>
    public static readonly TimeSpan EmptyPollInterval = TimeSpan.FromSeconds(10);

    private readonly IAzureDeploymentGateway _gateway;
    private readonly TimeProvider _time;

    public DeletionRunner(IAzureDeploymentGateway gateway, TimeProvider? time = null)
    {
        _gateway = gateway;
        _time = time ?? TimeProvider.System;
    }

    /// <param name="progress">削除を始める対象の通知</param>
    /// <exception cref="InvalidOperationException">計画の作成後に状態が変わり、削除の対象でなくなった</exception>
    public async Task<IReadOnlyList<DeletionResult>> RunAsync(
        DeletionPlan plan, IProgress<DeletionTarget>? progress = null, CancellationToken cancellationToken = default)
    {
        var planner = new DeletionPlanner(_gateway);
        var results = new List<DeletionResult>();
        foreach (var target in plan.Targets.Where(t => t.WillDelete))
        {
            var current = (await planner.PlanTargetsAsync(
                    plan.Subscription, plan.ResourceGroup, plan.AppName, plan.IncludeUntagged, plan.KeepDbData, cancellationToken))
                .Single(t => t.Name == target.Name);
            if (!current.Exists)
            {
                results.Add(new DeletionResult(target.Name, target.Kind, false));
                continue;
            }
            if (!current.WillDelete)
            {
                throw new InvalidOperationException(
                    $"{target.Name} は削除計画の作成後に状態が変わり、削除の対象でなくなったため中止しました（{current.Reason}）。" +
                    "もう一度実行して、削除計画を確認してください。");
            }

            progress?.Report(target);
            var deleted = target.Kind switch
            {
                DeletionResourceKind.ContainerApp => await _gateway.DeleteContainerAppAsync(plan.Subscription, plan.ResourceGroup, target.Name, cancellationToken),
                DeletionResourceKind.Environment => await _gateway.DeleteEnvironmentAsync(plan.Subscription, plan.ResourceGroup, target.Name, cancellationToken),
                _ => await _gateway.DeleteStorageAccountAsync(plan.Subscription, plan.ResourceGroup, target.Name, cancellationToken),
            };
            results.Add(new DeletionResult(target.Name, target.Kind, deleted));
        }
        return results;
    }

    /// <summary>
    /// 計画でリソースグループも削除するとした場合に、<see cref="RunAsync"/> の後に呼ぶ。リソースグループが空になったのを確かめてから削除する
    /// （空にならなければ削除しない。計画の後にリソースが加わっても巻き込まない）。
    /// </summary>
    /// <returns>削除した場合 true（既に無かった場合 false）</returns>
    /// <exception cref="InvalidOperationException">リソースが残った、またはリソースグループを削除できなかった（ロックなど）</exception>
    public async Task<bool> DeleteResourceGroupAsync(DeletionPlan plan, CancellationToken cancellationToken = default)
    {
        var deadline = _time.GetUtcNow() + EmptyWaitTimeout;
        IReadOnlyList<AzureResourceInfo> remaining;
        while ((remaining = await _gateway.ListResourcesAsync(plan.Subscription, plan.ResourceGroup, cancellationToken)).Count > 0
               && _time.GetUtcNow() < deadline)
        {
            await Task.Delay(EmptyPollInterval, _time, cancellationToken);
        }
        if (remaining.Count > 0)
        {
            throw new InvalidOperationException(
                $"リソースグループ {plan.ResourceGroup} にリソースが残っているため、リソースグループは削除しませんでした" +
                $"（{DeletionPlanner.RemainingReason(remaining.Select(r => $"{r.Name}（{r.Type}）").ToList())}）。本ツールのリソースの削除は終わっています。");
        }
        try
        {
            return await _gateway.DeleteResourceGroupAsync(plan.Subscription, plan.ResourceGroup, cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException(
                $"リソースグループ {plan.ResourceGroup} を削除できませんでした（{ex.Message}）。ロックが付いていないかを確かめてください。" +
                "本ツールのリソースの削除は終わっています。もう一度実行すると、リソースグループの削除だけを行います。", ex);
        }
    }
}
