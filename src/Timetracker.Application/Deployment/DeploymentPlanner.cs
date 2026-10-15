using Azure.Core;
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
        var rg = await _gateway.GetResourceGroupAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        if (rg == null)
            return ResourceGroupMissing(opts);
        var existingApps = await _gateway.ListContainerAppsAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        var env = await _gateway.GetEnvironmentAsync(opts.Subscription, opts.ResourceGroup, ResourceNaming.Environment(opts.AppName), cancellationToken);
        return await CheckPermissionsAsync(opts, existingApps, env != null, cancellationToken);
    }

    /// <summary>
    /// 既存のリソースを読み取り、デプロイで何が起きるかの計画を作る（Azure は変更しない）。
    /// 判定は実際のデプロイ（AzureSdkExecutor）と同じ: リソースグループのリージョン違いは警告、Environment のリージョン違いは実行できない。
    /// </summary>
    /// <param name="opts">検証済みの設定</param>
    public async Task<DeploymentPlan> PlanAsync(DeployOptions opts, CancellationToken cancellationToken = default)
    {
        var operations = new List<PlannedOperation>();
        var blockers = new List<string>();
        var warnings = new List<string>();
        var location = new AzureLocation(opts.Location);
        var envName = ResourceNaming.Environment(opts.AppName);
        var appNames = new[] { ResourceNaming.DbApp(opts.AppName), ResourceNaming.RedisApp(opts.AppName), ResourceNaming.TimetrackerApp(opts.AppName) };

        var rg = await _gateway.GetResourceGroupAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        if (rg == null)
        {
            // リソースグループが無ければ、その中のリソースも無い
            operations.Add(new PlannedOperation(PlannedResourceKind.ResourceGroup, opts.ResourceGroup, PlannedAction.Create));
            operations.Add(new PlannedOperation(PlannedResourceKind.Environment, envName, PlannedAction.Create));
            operations.AddRange(appNames.Select(n => new PlannedOperation(PlannedResourceKind.ContainerApp, n, PlannedAction.Create)));
            var notChecked = ResourceGroupMissing(opts);
            warnings.Add(notChecked.Note!);
            return new DeploymentPlan(opts, operations, blockers, warnings, notChecked);
        }

        operations.Add(new PlannedOperation(PlannedResourceKind.ResourceGroup, rg.Name, PlannedAction.UseExisting));
        if (new AzureLocation(rg.Location) != location)
            warnings.Add($"既存のリソースグループ '{rg.Name}' のリージョン ({rg.Location}) は location ({opts.Location}) と異なります。Container Apps は {opts.Location} に作成されます。");

        var env = await _gateway.GetEnvironmentAsync(opts.Subscription, opts.ResourceGroup, envName, cancellationToken);
        if (env == null)
        {
            operations.Add(new PlannedOperation(PlannedResourceKind.Environment, envName, PlannedAction.Create));
        }
        else
        {
            operations.Add(new PlannedOperation(PlannedResourceKind.Environment, envName, PlannedAction.UseExisting));
            // Container App は Environment と同じリージョンに作る必要があるため、実行しても途中で失敗する
            if (new AzureLocation(env.Location) != location)
                blockers.Add(
                    $"既存の Container Apps Environment '{envName}' のリージョン ({env.Location}) が location ({opts.Location}) と異なります。" +
                    $"location に {env.Location} を指定するか、別の app_name を指定してください。");
        }

        var existingApps = await _gateway.ListContainerAppsAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        foreach (var name in appNames)
        {
            var exists = existingApps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            var dataLoss = exists && name == ResourceNaming.DbApp(opts.AppName);
            operations.Add(new PlannedOperation(PlannedResourceKind.ContainerApp, name, exists ? PlannedAction.Update : PlannedAction.Create, dataLoss));
            if (dataLoss)
                warnings.Add($"DB アプリ '{name}' は既に存在します。設定が変わると DB コンテナが再起動し、" +
                             "永続ボリュームを設定していないため DB のデータは失われます。必要に応じて事前にバックアップしてください。");
        }

        var permissions = await CheckPermissionsAsync(opts, existingApps, env != null, cancellationToken);
        if (permissions.Status == PermissionCheckStatus.Missing)
            blockers.Add($"権限が不足しています: {string.Join(", ", permissions.MissingActions)}。" +
                         "README の「必要な RBAC ロールと権限」を参照し、ロールの割り当てを確認してください。");
        else if (permissions.Status == PermissionCheckStatus.NotChecked)
            warnings.Add($"権限を事前に確認できませんでした: {permissions.Note}");

        return new DeploymentPlan(opts, operations, blockers, warnings, permissions);
    }

    // 権限の一覧は、存在するリソースグループかリソースでしか取得できない
    private static PermissionCheckResult ResourceGroupMissing(DeployOptions opts) =>
        PermissionCheckResult.NotChecked(
            $"リソースグループ '{opts.ResourceGroup}' がまだ無いため、権限を事前に確認できません" +
            "（作成する権限が無い場合は最初の手順で失敗し、リソースは作成されません）。");

    private async Task<PermissionCheckResult> CheckPermissionsAsync(
        DeployOptions opts, IReadOnlyList<ContainerAppInfo> existingApps, bool envExists, CancellationToken cancellationToken)
    {
        // 既存のリソースへの操作は、リソースに割り当てられたロールでも許可される。作成するものはリソースグループでの権限が必要
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
