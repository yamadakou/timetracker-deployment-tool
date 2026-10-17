using Timetracker.Domain.Azure;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Application.Permissions;

public enum PermissionCheckStatus
{
    /// <summary>必要な権限がすべてある。</summary>
    Ok,
    /// <summary>不足している権限がある。</summary>
    Missing,
    /// <summary>確認できなかった（理由は <see cref="PermissionCheckResult.Note"/>）。</summary>
    NotChecked,
}

/// <summary>権限の事前確認の結果。</summary>
public sealed record PermissionCheckResult(PermissionCheckStatus Status, IReadOnlyList<string> MissingActions, string? Note)
{
    public static PermissionCheckResult Ok { get; } = new(PermissionCheckStatus.Ok, [], null);

    public static PermissionCheckResult NotChecked(string note) => new(PermissionCheckStatus.NotChecked, [], note);
}

/// <summary>既存のリソース（リソースプロバイダーの名前空間・種類・名前）。</summary>
public sealed record TargetResource(string Namespace, string Type, string Name)
{
    public static TargetResource ContainerApp(string name) => new("Microsoft.App", "containerApps", name);

    public static TargetResource Environment(string name) => new("Microsoft.App", "managedEnvironments", name);

    public static TargetResource StorageAccount(string name) => new("Microsoft.Storage", "storageAccounts", name);
}

/// <summary>必要な権限 1 件。<see cref="Resource"/> は操作する既存のリソース（作成する場合は null）。</summary>
public sealed record PermissionRequirement(string Action, TargetResource? Resource = null);

/// <summary>
/// 実行する ID が必要な権限を持っているかを、操作の前に確認する
/// （途中で権限エラーになり、一部のリソースだけが作成・削除された状態が残るのを防ぐ）。
/// まずリソースグループでの権限で判定し、許可されていない操作は、既存のリソースに対するものであれば
/// そのリソースでの権限（リソースに割り当てられたロールを含む）でも判定する。
/// 拒否割り当てや Azure Policy による拒否は反映されないため、確認を通っても実行時に失敗することはある。
/// </summary>
public sealed class PermissionChecker
{
    private readonly IAzureDeploymentGateway _gateway;

    public PermissionChecker(IAzureDeploymentGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<PermissionCheckResult> CheckAsync(
        string subscriptionId, string rgName, IReadOnlyCollection<PermissionRequirement> requirements, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PermissionSet> rgPermissions;
        try
        {
            rgPermissions = await _gateway.GetPermissionsAsync(subscriptionId, rgName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 確認できないことを理由に止めない（実際の操作で権限が無ければ Azure 側でエラーになる）
            return NotChecked(ex);
        }

        var resourcePermissions = new Dictionary<TargetResource, IReadOnlyList<PermissionSet>>();
        var missing = new List<string>();
        foreach (var requirement in requirements)
        {
            if (PermissionEvaluator.IsAllowed(rgPermissions, requirement.Action))
                continue;

            // リソースグループでは許可されていなくても、リソースに割り当てられたロールで許可されていることがある
            if (requirement.Resource is { } resource)
            {
                if (!resourcePermissions.TryGetValue(resource, out var permissions))
                {
                    try
                    {
                        permissions = await _gateway.GetResourcePermissionsAsync(
                            subscriptionId, rgName, resource.Namespace, resource.Type, resource.Name, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return NotChecked(ex);
                    }
                    resourcePermissions[resource] = permissions;
                }
                if (PermissionEvaluator.IsAllowed(permissions, requirement.Action))
                    continue;
            }

            if (!missing.Contains(requirement.Action))
                missing.Add(requirement.Action);
        }

        return missing.Count == 0
            ? PermissionCheckResult.Ok
            : new PermissionCheckResult(PermissionCheckStatus.Missing, missing, null);
    }

    private static PermissionCheckResult NotChecked(Exception ex) =>
        PermissionCheckResult.NotChecked($"権限の一覧を取得できませんでした: {ex.Message}");
}
