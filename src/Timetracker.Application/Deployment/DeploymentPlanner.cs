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
        // 権限の一覧はリソースグループ単位でしか取得できない
        if (await _gateway.GetResourceGroupAsync(opts.Subscription, opts.ResourceGroup, cancellationToken) == null)
        {
            return PermissionCheckResult.NotChecked(
                $"リソースグループ '{opts.ResourceGroup}' がまだ無いため、権限を事前に確認できません" +
                "（作成する権限が無い場合は最初の手順で失敗し、リソースは作成されません）。");
        }

        var actions = AzurePermissions.Deploy.ToList();
        var envName = ResourceNaming.Environment(opts.AppName);
        if (await _gateway.GetEnvironmentAsync(opts.Subscription, opts.ResourceGroup, envName, cancellationToken) == null)
        {
            actions.Add(AzurePermissions.EnvironmentWrite);
        }

        return await new PermissionChecker(_gateway).CheckAsync(opts.Subscription, opts.ResourceGroup, actions, cancellationToken);
    }
}
