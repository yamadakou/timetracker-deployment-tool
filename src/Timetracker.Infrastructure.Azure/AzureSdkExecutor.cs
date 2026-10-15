using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.Authorization;
using Timetracker.Common;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;

namespace Timetracker.Infrastructure.Azure;

public class AzureSdkExecutor : IAzureDeploymentGateway
{
    private readonly IDeploymentLogger _logger;
    private readonly ArmClient _arm;

    public AzureSdkExecutor(IDeploymentLogger logger, TokenCredential credential)
        : this(logger, new ArmClient(credential))
    {
    }

    public AzureSdkExecutor(IDeploymentLogger logger, ArmClient arm)
    {
        _logger = logger;
        _arm = arm;
    }

    public async Task EnsureResourceGroupAsync(string subscriptionId, string rgName, string location, CancellationToken cancellationToken = default)
    {
        var sub = _arm.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
        var rgs = sub.GetResourceGroups();

        // 既存 RG に CreateOrUpdate(PUT) するとタグが消える／リージョン不一致でエラーになるため、
        // 既存の場合は変更せずそのまま使う
        var existing = await rgs.GetIfExistsAsync(rgName, cancellationToken);
        if (existing.HasValue)
        {
            var rgLocation = existing.Value!.Data.Location;
            if (rgLocation != new AzureLocation(location))
            {
                _logger.Warn($"既存の ResourceGroup '{rgName}' のリージョン ({rgLocation}) は --location ({location}) と異なります。Container Apps は {location} に作成されます。");
            }
            _logger.Info($"既存の ResourceGroup を使用: {rgName}");
            return;
        }

        await rgs.CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(location), cancellationToken);
        _logger.Info($"ResourceGroup を作成: {rgName}");
    }

    public async Task<ContainerAppManagedEnvironmentResource> EnsureContainerAppEnvAsync(
        string subscriptionId, string rgName, string appName, string location, CancellationToken cancellationToken = default)
    {
        var envName = ResourceNaming.Environment(appName);
        var rg = _arm.GetResourceGroupResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}/resourceGroups/{rgName}"));
        var envCollection = rg.GetContainerAppManagedEnvironments();

        // 既存 Environment に PUT すると既存設定（ログ設定など）が上書きされるため、既存ならそのまま使う
        var existing = await envCollection.GetIfExistsAsync(envName, cancellationToken);
        if (existing.HasValue)
        {
            // Container App は Environment と同じリージョンに作成する必要があるため、異なる場合は作成前に止める
            var envLocation = existing.Value!.Data.Location;
            if (envLocation != new AzureLocation(location))
            {
                throw new InvalidOperationException(
                    $"既存の Container Apps Environment '{envName}' のリージョン ({envLocation}) が --location ({location}) と異なります。" +
                    $"--location {envLocation.Name} を指定するか、別の --app-name を指定してください。");
            }
            _logger.Info($"既存の Container Apps Environment を使用: {envName}");
            return existing.Value!;
        }

        var data = ContainerAppSpecBuilder.BuildManagedEnvironment(location, appName);
        var envOp = await envCollection.CreateOrUpdateAsync(WaitUntil.Completed, envName, data, cancellationToken);
        var env = envOp.Value;
        _logger.Info($"Container Apps Environment を作成: {envName}");
        return env;
    }

    /// <summary>
    /// DB アプリが既に存在する場合、更新（リビジョン再作成）で DB コンテナが再起動し、
    /// 永続ボリューム未設定のためデータが失われる旨を警告する。
    /// </summary>
    /// <returns>DB アプリが既に存在する場合 true</returns>
    public async Task<bool> WarnIfDbAppExistsAsync(string subscriptionId, string rgName, string dbAppName, CancellationToken cancellationToken = default)
    {
        var rg = _arm.GetResourceGroupResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}/resourceGroups/{rgName}"));
        var existing = await rg.GetContainerApps().GetIfExistsAsync(dbAppName, cancellationToken);
        if (!existing.HasValue)
            return false;

        WarnDbDataLoss(dbAppName);
        return true;
    }

    private void WarnDbDataLoss(string dbAppName) =>
        _logger.Warn($"DB アプリ '{dbAppName}' は既に存在します。設定が変わると DB コンテナが再起動し、" +
                     "永続ボリュームを設定していないため DB のデータは失われます。必要に応じて事前にバックアップしてください。");

    // 既存の Container App のタグ（存在しなければ null）。更新時に引き継ぐために読む
    private static async Task<IReadOnlyDictionary<string, string>?> GetExistingTagsAsync(
        ContainerAppCollection apps, string name, CancellationToken cancellationToken)
    {
        var existing = await apps.GetIfExistsAsync(name, cancellationToken);
        return existing.HasValue ? new Dictionary<string, string>(existing.Value!.Data.Tags) : null;
    }

    public async Task<ContainerAppsDeployment> CreateOrUpdateContainerAppAsync(
        string subscriptionId,
        string rgName,
        string appName,
        string location,
        DeployOptions opts,
        ContainerAppManagedEnvironmentResource env,
        CancellationToken cancellationToken = default)
    {
        var rg = _arm.GetResourceGroupResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}/resourceGroups/{rgName}"));
        var apps = rg.GetContainerApps();

        var dbPort = ContainerAppSpecBuilder.GetDbPort(opts);

        // 1) DB
        var dbAppName = ResourceNaming.DbApp(appName);
        var dbTags = await GetExistingTagsAsync(apps, dbAppName, cancellationToken);
        if (dbTags != null)
            WarnDbDataLoss(dbAppName);
        var dbAppOp = await apps.CreateOrUpdateAsync(WaitUntil.Completed, dbAppName,
            ContainerAppSpecBuilder.WithManagedTags(ContainerAppSpecBuilder.BuildDbApp(opts, location, env.Id), appName, dbTags),
            cancellationToken);
        var dbApp = dbAppOp.Value;
        // 内部接続は Container App 名を使用
        var dbEndpointHost = dbAppName;
        _logger.Info($"DB app ready: {dbApp.Data.Name} (endpoint: {dbEndpointHost}:{dbPort})");

        // 2) Redis
        var redisAppName = ResourceNaming.RedisApp(appName);
        var redisTags = await GetExistingTagsAsync(apps, redisAppName, cancellationToken);
        var redisAppOp = await apps.CreateOrUpdateAsync(WaitUntil.Completed, redisAppName,
            ContainerAppSpecBuilder.WithManagedTags(ContainerAppSpecBuilder.BuildRedisApp(opts, location, env.Id), appName, redisTags),
            cancellationToken);
        var redisApp = redisAppOp.Value;
        var redisEndpointHost = redisAppName;
        _logger.Info($"Redis app ready: {redisApp.Data.Name} (endpoint: {redisEndpointHost}:{ContainerAppSpecBuilder.RedisPort})");

        // 3) Timetracker
        var ttAppName = ResourceNaming.TimetrackerApp(appName);
        var ttTags = await GetExistingTagsAsync(apps, ttAppName, cancellationToken);
        var ttAppOp = await apps.CreateOrUpdateAsync(WaitUntil.Completed, ttAppName,
            ContainerAppSpecBuilder.WithManagedTags(
                ContainerAppSpecBuilder.BuildTimetrackerApp(opts, location, env.Id, dbEndpointHost, redisEndpointHost), appName, ttTags),
            cancellationToken);
        var ttApp = ttAppOp.Value;
        var ttUrl = ttApp.Data.Configuration?.Ingress?.Fqdn != null
            ? $"https://{ttApp.Data.Configuration.Ingress.Fqdn}"
            : string.Empty;

        _logger.Success($"Container Apps ready: {ttApp.Data.Name}, {dbApp.Data.Name}, {redisApp.Data.Name}");
        if (!string.IsNullOrEmpty(ttUrl))
        {
            _logger.Success($"Timetracker endpoint: {ttUrl}");
        }
        else
        {
            _logger.Warn("Timetracker endpoint FQDN not available yet. Check Container App ingress settings.");
        }

        return new ContainerAppsDeployment(ttApp.Data.Name, dbApp.Data.Name, redisApp.Data.Name,
            string.IsNullOrEmpty(ttUrl) ? null : ttUrl);
    }

    private ResourceGroupResource ResourceGroup(string subscriptionId, string rgName) =>
        _arm.GetResourceGroupResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}/resourceGroups/{rgName}"));

    public async Task<ResourceGroupInfo?> GetResourceGroupAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default)
    {
        var sub = _arm.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
        var existing = await sub.GetResourceGroups().GetIfExistsAsync(rgName, cancellationToken);
        return existing.HasValue ? new ResourceGroupInfo(existing.Value!.Data.Name, existing.Value.Data.Location.Name) : null;
    }

    public async Task<ManagedEnvironmentInfo?> GetEnvironmentAsync(
        string subscriptionId, string rgName, string envName, CancellationToken cancellationToken = default)
    {
        var existing = await ResourceGroup(subscriptionId, rgName).GetContainerAppManagedEnvironments().GetIfExistsAsync(envName, cancellationToken);
        if (!existing.HasValue)
            return null;
        var env = existing.Value!;
        return new ManagedEnvironmentInfo(
            env.Id.ToString(), env.Data.Name, env.Data.Location.Name, new Dictionary<string, string>(env.Data.Tags), env.Data.ProvisioningState?.ToString());
    }

    public async Task<IReadOnlyList<ContainerAppInfo>> ListContainerAppsAsync(
        string subscriptionId, string rgName, CancellationToken cancellationToken = default)
    {
        var result = new List<ContainerAppInfo>();
        await foreach (var app in ResourceGroup(subscriptionId, rgName).GetContainerApps().GetAllAsync(cancellationToken))
        {
            var environmentId = app.Data.EnvironmentId ?? app.Data.ManagedEnvironmentId;
            result.Add(new ContainerAppInfo(app.Data.Name, environmentId?.ToString(), new Dictionary<string, string>(app.Data.Tags)));
        }
        return result;
    }

    public async Task<IReadOnlyList<ContainerAppDetails>> ListContainerAppDetailsAsync(
        string subscriptionId, string rgName, CancellationToken cancellationToken = default)
    {
        var result = new List<ContainerAppDetails>();
        await foreach (var app in ResourceGroup(subscriptionId, rgName).GetContainerApps().GetAllAsync(cancellationToken))
        {
            var container = app.Data.Template?.Containers.FirstOrDefault();
            result.Add(new ContainerAppDetails(
                app.Data.Name,
                app.Data.ProvisioningState?.ToString(),
                app.Data.RunningStatus?.ToString(),
                container?.Image,
                container?.Resources?.Cpu,
                container?.Resources?.Memory,
                app.Data.LatestRevisionName,
                app.Data.Configuration?.Ingress?.Fqdn,
                new Dictionary<string, string>(app.Data.Tags)));
        }
        return result;
    }

    public async Task<bool> DeleteContainerAppAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default)
    {
        var existing = await ResourceGroup(subscriptionId, rgName).GetContainerApps().GetIfExistsAsync(name, cancellationToken);
        if (!existing.HasValue)
            return false;
        await existing.Value!.DeleteAsync(WaitUntil.Completed, cancellationToken);
        return true;
    }

    public async Task<bool> DeleteEnvironmentAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default)
    {
        var existing = await ResourceGroup(subscriptionId, rgName).GetContainerAppManagedEnvironments().GetIfExistsAsync(name, cancellationToken);
        if (!existing.HasValue)
            return false;
        await existing.Value!.DeleteAsync(WaitUntil.Completed, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PermissionSet>> GetPermissionsAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default)
    {
        var result = new List<PermissionSet>();
        await foreach (var p in ResourceGroup(subscriptionId, rgName).GetAzurePermissionsForResourceGroupsAsync(cancellationToken))
        {
            result.Add(new PermissionSet(p.Actions.ToList(), p.NotActions.ToList()));
        }
        return result;
    }

    public async Task<IReadOnlyList<PermissionSet>> GetResourcePermissionsAsync(
        string subscriptionId, string rgName, string resourceType, string name, CancellationToken cancellationToken = default)
    {
        var result = new List<PermissionSet>();
        await foreach (var p in ResourceGroup(subscriptionId, rgName)
                           .GetAzurePermissionsForResourcesAsync("Microsoft.App", "", resourceType, name, cancellationToken))
        {
            result.Add(new PermissionSet(p.Actions.ToList(), p.NotActions.ToList()));
        }
        return result;
    }
}
