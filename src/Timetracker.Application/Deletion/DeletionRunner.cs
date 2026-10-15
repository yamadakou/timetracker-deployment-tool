using Timetracker.Infrastructure.Azure;

namespace Timetracker.Application.Deletion;

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
    private readonly IAzureDeploymentGateway _gateway;

    public DeletionRunner(IAzureDeploymentGateway gateway)
    {
        _gateway = gateway;
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
            var current = (await planner.PlanTargetsAsync(plan.Subscription, plan.ResourceGroup, plan.AppName, plan.IncludeUntagged, cancellationToken))
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
            var deleted = target.Kind == DeletionResourceKind.ContainerApp
                ? await _gateway.DeleteContainerAppAsync(plan.Subscription, plan.ResourceGroup, target.Name, cancellationToken)
                : await _gateway.DeleteEnvironmentAsync(plan.Subscription, plan.ResourceGroup, target.Name, cancellationToken);
            results.Add(new DeletionResult(target.Name, target.Kind, deleted));
        }
        return results;
    }
}
