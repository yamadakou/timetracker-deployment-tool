using Timetracker.Application.Permissions;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Application.Deployment;

/// <summary>デプロイ前の確認（読み取りのみ）。</summary>
public sealed class DeploymentPlanner
{
    private readonly IAzureDeploymentGateway _gateway;

    public DeploymentPlanner(IAzureDeploymentGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>デプロイに必要な権限があるかを確認する。</summary>
    public async Task<PermissionCheckResult> CheckPermissionsAsync(DeployOptions opts, CancellationToken cancellationToken = default)
    {
        // 権限の一覧は、存在するリソースグループかリソースでしか取得できない
        if (await _gateway.GetResourceGroupAsync(opts.Subscription, opts.ResourceGroup, cancellationToken) == null)
        {
            return PermissionCheckResult.NotChecked(
                $"リソースグループ '{opts.ResourceGroup}' がまだ無いため、権限を事前に確認できません" +
                "（作成する権限が無い場合は最初の手順で失敗し、リソースは作成されません）。");
        }

        // 既存のリソースへの操作は、リソースに割り当てられたロールでも許可される。作成するものはリソースグループでの権限が必要
        var existingApps = await _gateway.ListContainerAppsAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        var requirements = new List<PermissionRequirement>();
        foreach (var name in new[] { ResourceNaming.DbApp(opts.AppName), ResourceNaming.RedisApp(opts.AppName), ResourceNaming.TimetrackerApp(opts.AppName) })
        {
            var resource = existingApps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                ? TargetResource.ContainerApp(name)
                : null;
            requirements.Add(new PermissionRequirement(AzurePermissions.ContainerAppRead, resource));
            requirements.Add(new PermissionRequirement(AzurePermissions.ContainerAppWrite, resource));
        }

        var envName = ResourceNaming.Environment(opts.AppName);
        var envExists = await _gateway.GetEnvironmentAsync(opts.Subscription, opts.ResourceGroup, envName, cancellationToken) != null;
        var envResource = envExists ? TargetResource.Environment(envName) : null;
        requirements.Add(new PermissionRequirement(AzurePermissions.EnvironmentRead, envResource));
        requirements.Add(new PermissionRequirement(AzurePermissions.EnvironmentJoin, envResource));
        if (!envExists)
        {
            requirements.Add(new PermissionRequirement(AzurePermissions.EnvironmentWrite));
        }

        return await new PermissionChecker(_gateway).CheckAsync(opts.Subscription, opts.ResourceGroup, requirements, cancellationToken);
    }
}
