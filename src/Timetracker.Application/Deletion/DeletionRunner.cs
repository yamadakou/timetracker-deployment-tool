using Timetracker.Infrastructure.Azure;

namespace Timetracker.Application.Deletion;

/// <summary>削除した結果。<see cref="Deleted"/> が false の場合、計画の作成後に既に削除されていた。</summary>
public sealed record DeletionResult(string Name, DeletionResourceKind Kind, bool Deleted);

/// <summary>削除計画で「削除する」となったリソースを、計画の順に削除する。</summary>
public sealed class DeletionRunner
{
    private readonly IAzureDeploymentGateway _gateway;

    public DeletionRunner(IAzureDeploymentGateway gateway)
    {
        _gateway = gateway;
    }

    /// <param name="progress">削除を始める対象の通知</param>
    public async Task<IReadOnlyList<DeletionResult>> RunAsync(
        DeletionPlan plan, IProgress<DeletionTarget>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<DeletionResult>();
        foreach (var target in plan.Targets.Where(t => t.WillDelete))
        {
            progress?.Report(target);
            var deleted = target.Kind == DeletionResourceKind.ContainerApp
                ? await _gateway.DeleteContainerAppAsync(plan.Subscription, plan.ResourceGroup, target.Name, cancellationToken)
                : await _gateway.DeleteEnvironmentAsync(plan.Subscription, plan.ResourceGroup, target.Name, cancellationToken);
            results.Add(new DeletionResult(target.Name, target.Kind, deleted));
        }
        return results;
    }
}
