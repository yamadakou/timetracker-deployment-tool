using Timetracker.Application.Permissions;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Application.Deletion;

/// <summary>
/// 削除計画を作る（読み取りのみ）。本ツールの命名規則に一致し、かつ管理タグがあるリソースだけを削除の対象にする
/// （includeUntagged の場合は管理タグが無くても対象にする）。リソースグループは削除しない。
/// </summary>
public sealed class DeletionPlanner
{
    public const string NotFoundReason = "存在しません";
    public const string UnmanagedReason = "管理タグがありません";

    private readonly IAzureDeploymentGateway _gateway;

    public DeletionPlanner(IAzureDeploymentGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<DeletionPlan> PlanAsync(
        string subscriptionId, string rgName, string appName, bool includeUntagged, CancellationToken cancellationToken = default)
    {
        var appNames = new[] { ResourceNaming.TimetrackerApp(appName), ResourceNaming.RedisApp(appName), ResourceNaming.DbApp(appName) };
        var envName = ResourceNaming.Environment(appName);

        if (await _gateway.GetResourceGroupAsync(subscriptionId, rgName, cancellationToken) == null)
        {
            var notFound = appNames.Select(n => NotFound(n, DeletionResourceKind.ContainerApp))
                .Append(NotFound(envName, DeletionResourceKind.Environment)).ToList();
            return new DeletionPlan(subscriptionId, rgName, appName, notFound, PermissionCheckResult.Ok);
        }

        var existingApps = await _gateway.ListContainerAppsAsync(subscriptionId, rgName, cancellationToken);
        var targets = new List<DeletionTarget>();
        foreach (var name in appNames)
        {
            var app = existingApps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            targets.Add(app == null
                ? NotFound(name, DeletionResourceKind.ContainerApp)
                : Candidate(name, DeletionResourceKind.ContainerApp, ManagedTags.IsManaged(app.Tags, appName), includeUntagged));
        }

        var env = await _gateway.GetEnvironmentAsync(subscriptionId, rgName, envName, cancellationToken);
        targets.Add(env == null ? NotFound(envName, DeletionResourceKind.Environment) : PlanEnvironment(env, appName, includeUntagged, existingApps, targets));

        var actions = new List<string>();
        if (targets.Any(t => t.WillDelete && t.Kind == DeletionResourceKind.ContainerApp))
            actions.Add(AzurePermissions.ContainerAppDelete);
        if (targets.Any(t => t.WillDelete && t.Kind == DeletionResourceKind.Environment))
            actions.Add(AzurePermissions.EnvironmentDelete);
        var permissionCheck = actions.Count == 0
            ? PermissionCheckResult.Ok
            : await new PermissionChecker(_gateway).CheckAsync(subscriptionId, rgName, actions, cancellationToken);

        return new DeletionPlan(subscriptionId, rgName, appName, targets, permissionCheck);
    }

    // Environment は、中に削除しない Container App が残ると削除できないため、その場合は削除しない
    private static DeletionTarget PlanEnvironment(
        ManagedEnvironmentInfo env, string appName, bool includeUntagged, IReadOnlyList<ContainerAppInfo> existingApps, List<DeletionTarget> appTargets)
    {
        var target = Candidate(env.Name, DeletionResourceKind.Environment, ManagedTags.IsManaged(env.Tags, appName), includeUntagged);
        if (!target.WillDelete)
            return target;

        var deleting = appTargets.Where(t => t.WillDelete).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = existingApps
            .Where(a => string.Equals(a.EnvironmentId, env.Id, StringComparison.OrdinalIgnoreCase) && !deleting.Contains(a.Name))
            .Select(a => a.Name)
            .ToList();
        return remaining.Count == 0
            ? target
            : target with { WillDelete = false, Reason = $"Environment 内に削除しない Container App（{string.Join(", ", remaining)}）が残るため" };
    }

    private static DeletionTarget NotFound(string name, DeletionResourceKind kind) => new(name, kind, false, false, false, NotFoundReason);

    private static DeletionTarget Candidate(string name, DeletionResourceKind kind, bool managed, bool includeUntagged) =>
        managed || includeUntagged
            ? new DeletionTarget(name, kind, true, managed, true, null)
            : new DeletionTarget(name, kind, true, false, false, UnmanagedReason);
}
