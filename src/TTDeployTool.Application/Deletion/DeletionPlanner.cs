using TTDeployTool.Application.Permissions;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Application.Deletion;

/// <summary>
/// 削除計画を作る（読み取りのみ）。本ツールの命名規則に一致し、かつ管理タグがあるリソースだけを削除の対象にする
/// （includeUntagged の場合は管理タグが無くても対象にする。別のアプリ・ツールの管理タグがあるものは対象にしない）。リソースグループは削除しない。
/// DB のデータを置いたストレージアカウントも同じ規則で削除の対象にする（keepDbData の場合は残す）。
/// deleteResourceGroup の場合は、本ツールのリソースを削除した後にリソースグループが空になるなら、リソースグループも削除の対象にする。
/// </summary>
public sealed class DeletionPlanner
{
    public const string NotFoundReason = "存在しません";
    public const string UnmanagedReason = "管理タグがありません";
    public const string OtherOwnerReason = "別のアプリまたは別のツールの管理タグがあります（--include-untagged でも対象にしません）";
    public const string KeptReason = "--keep-db-data の指定により残します（DB のデータ）";

    /// <summary>リソースグループの削除の判定で、残るリソースとして名前を示す数の上限。</summary>
    public const int RemainingShown = 10;

    // 削除の対象の種類と、Azure のリソースの種類
    private static readonly IReadOnlyDictionary<DeletionResourceKind, string> ResourceTypes = new Dictionary<DeletionResourceKind, string>
    {
        [DeletionResourceKind.ContainerApp] = "Microsoft.App/containerApps",
        [DeletionResourceKind.Environment] = "Microsoft.App/managedEnvironments",
        [DeletionResourceKind.StorageAccount] = "Microsoft.Storage/storageAccounts",
    };

    private readonly IAzureDeploymentGateway _gateway;

    public DeletionPlanner(IAzureDeploymentGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<DeletionPlan> PlanAsync(
        string subscriptionId, string rgName, string appName, bool includeUntagged, bool keepDbData = false, bool deleteResourceGroup = false,
        CancellationToken cancellationToken = default)
    {
        var targets = await PlanTargetsAsync(subscriptionId, rgName, appName, includeUntagged, keepDbData, cancellationToken);
        var resourceGroup = deleteResourceGroup ? await PlanResourceGroupAsync(subscriptionId, rgName, targets, cancellationToken) : null;

        var requirements = targets.Where(t => t.WillDelete)
            .Select(t => t.Kind switch
            {
                DeletionResourceKind.ContainerApp => new PermissionRequirement(AzurePermissions.ContainerAppDelete, TargetResource.ContainerApp(t.Name)),
                DeletionResourceKind.Environment => new PermissionRequirement(AzurePermissions.EnvironmentDelete, TargetResource.Environment(t.Name)),
                _ => new PermissionRequirement(AzurePermissions.StorageAccountDelete, TargetResource.StorageAccount(t.Name)),
            })
            .ToList();
        if (resourceGroup?.WillDelete == true)
            requirements.Add(new PermissionRequirement(AzurePermissions.ResourceGroupDelete));
        var permissionCheck = requirements.Count == 0
            ? PermissionCheckResult.Ok
            : await new PermissionChecker(_gateway).CheckAsync(subscriptionId, rgName, requirements, cancellationToken);

        return new DeletionPlan(subscriptionId, rgName, appName, targets, permissionCheck, includeUntagged, keepDbData, resourceGroup);
    }

    /// <summary>
    /// 削除の対象（<paramref name="targets"/> の <see cref="DeletionTarget.WillDelete"/> のもの）を消した後に、リソースグループが空になるかを確かめる
    /// （ほかのアプリ名の環境や、本ツール以外のリソースが 1 つでも残れば削除しない）。
    /// </summary>
    public async Task<ResourceGroupDeletion> PlanResourceGroupAsync(
        string subscriptionId, string rgName, IReadOnlyList<DeletionTarget> targets, CancellationToken cancellationToken = default)
    {
        if (await _gateway.GetResourceGroupAsync(subscriptionId, rgName, cancellationToken) == null)
            return new ResourceGroupDeletion(false, false, NotFoundReason, []);

        var deleting = targets.Where(t => t.WillDelete).Select(t => (t.Name, Type: ResourceTypes[t.Kind])).ToList();
        var remaining = (await _gateway.ListResourcesAsync(subscriptionId, rgName, cancellationToken))
            .Where(r => !deleting.Any(d => string.Equals(d.Name, r.Name, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(d.Type, r.Type, StringComparison.OrdinalIgnoreCase)))
            .Select(r => $"{r.Name}（{r.Type}）")
            .ToList();
        return remaining.Count == 0
            ? new ResourceGroupDeletion(true, true, null, [])
            : new ResourceGroupDeletion(true, false, RemainingReason(remaining), remaining);
    }

    /// <summary>リソースグループに残るリソースの説明（多ければ、先頭の <see cref="RemainingShown"/> 件と残りの数）。</summary>
    public static string RemainingReason(IReadOnlyList<string> remaining) =>
        $"ほかのリソースが残ります: {string.Join(", ", remaining.Take(RemainingShown))}" +
        (remaining.Count > RemainingShown ? $" ほか {remaining.Count - RemainingShown} 件" : "");

    /// <summary>現在の Azure の状態から、各対象を削除するかを決める（権限は確認しない）。</summary>
    public async Task<IReadOnlyList<DeletionTarget>> PlanTargetsAsync(
        string subscriptionId, string rgName, string appName, bool includeUntagged, bool keepDbData = false,
        CancellationToken cancellationToken = default)
    {
        var appNames = new[] { ResourceNaming.TimetrackerApp(appName), ResourceNaming.RedisApp(appName), ResourceNaming.DbApp(appName) };
        var envName = ResourceNaming.Environment(appName);
        var accountName = ResourceNaming.StorageAccount(subscriptionId, rgName, appName);

        if (await _gateway.GetResourceGroupAsync(subscriptionId, rgName, cancellationToken) == null)
        {
            return appNames.Select(n => NotFound(n, DeletionResourceKind.ContainerApp))
                .Append(NotFound(envName, DeletionResourceKind.Environment))
                .Append(NotFound(accountName, DeletionResourceKind.StorageAccount)).ToList();
        }

        var existingApps = await _gateway.ListContainerAppsAsync(subscriptionId, rgName, cancellationToken);
        var targets = new List<DeletionTarget>();
        foreach (var name in appNames)
        {
            var app = existingApps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            targets.Add(app == null
                ? NotFound(name, DeletionResourceKind.ContainerApp)
                : Candidate(name, DeletionResourceKind.ContainerApp, app.Tags, appName, includeUntagged));
        }

        var env = await _gateway.GetEnvironmentAsync(subscriptionId, rgName, envName, cancellationToken);
        targets.Add(env == null ? NotFound(envName, DeletionResourceKind.Environment) : PlanEnvironment(env, appName, includeUntagged, existingApps, targets));

        var account = await _gateway.GetStorageAccountAsync(subscriptionId, rgName, accountName, cancellationToken);
        targets.Add(account == null ? NotFound(accountName, DeletionResourceKind.StorageAccount)
            : keepDbData ? new DeletionTarget(accountName, DeletionResourceKind.StorageAccount, true, ManagedTags.IsManaged(account.Tags, appName), false, KeptReason)
            : Candidate(accountName, DeletionResourceKind.StorageAccount, account.Tags, appName, includeUntagged));
        return targets;
    }

    // Environment は、中に削除しない Container App が残ると削除できないため、その場合は削除しない
    private static DeletionTarget PlanEnvironment(
        ManagedEnvironmentInfo env, string appName, bool includeUntagged, IReadOnlyList<ContainerAppInfo> existingApps, List<DeletionTarget> appTargets)
    {
        var target = Candidate(env.Name, DeletionResourceKind.Environment, env.Tags, appName, includeUntagged);
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

    private static DeletionTarget Candidate(
        string name, DeletionResourceKind kind, IReadOnlyDictionary<string, string> tags, string appName, bool includeUntagged)
    {
        if (ManagedTags.IsManaged(tags, appName))
            return new DeletionTarget(name, kind, true, true, true, null);
        if (ManagedTags.HasOtherOwner(tags, appName))
            return new DeletionTarget(name, kind, true, false, false, OtherOwnerReason);
        return includeUntagged
            ? new DeletionTarget(name, kind, true, false, true, null)
            : new DeletionTarget(name, kind, true, false, false, UnmanagedReason);
    }
}
