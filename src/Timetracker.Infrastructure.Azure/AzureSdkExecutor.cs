using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
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

    private void WarnDbDataLoss(string dbAppName) => _logger.Warn(DbPersistence.DataLossWarning(dbAppName, false, false)!);

    // 既存の Container App のタグ（存在しなければ null）。更新時に引き継ぐために読む
    private static async Task<IReadOnlyDictionary<string, string>?> GetExistingTagsAsync(
        ContainerAppCollection apps, string name, CancellationToken cancellationToken)
    {
        var existing = await apps.GetIfExistsAsync(name, cancellationToken);
        return existing.HasValue ? new Dictionary<string, string>(existing.Value!.Data.Tags) : null;
    }

    // ボリュームで使っている Environment のストレージ（Azure Files）の名前
    private static IReadOnlyList<string> StorageNames(ContainerAppData data) =>
        data.Template?.Volumes
            .Where(v => v.StorageType == ContainerAppStorageType.AzureFile && v.StorageName != null)
            .Select(v => v.StorageName)
            .ToList() ?? [];

    public async Task EnsureDbStorageAsync(
        string subscriptionId, string rgName, string appName, string location, ContainerAppManagedEnvironmentResource env,
        CancellationToken cancellationToken = default)
    {
        var accountName = ResourceNaming.StorageAccount(subscriptionId, rgName, appName);
        var shareName = ResourceNaming.DbFileShare(appName);
        var accounts = ResourceGroup(subscriptionId, rgName).GetStorageAccounts();

        // 既存のストレージアカウントは変更しない（PUT すると利用者が変えた設定やタグが上書きされるため）
        var existing = await accounts.GetIfExistsAsync(accountName, cancellationToken: cancellationToken);
        StorageAccountResource account;
        if (existing.HasValue)
        {
            account = existing.Value!;
            _logger.Info($"既存のストレージアカウントを使用: {accountName}");
        }
        else
        {
            var created = await accounts.CreateOrUpdateAsync(
                WaitUntil.Completed, accountName, ContainerAppSpecBuilder.BuildStorageAccount(location, appName), cancellationToken);
            account = created.Value;
            _logger.Info($"ストレージアカウントを作成: {accountName}");
        }

        var shares = account.GetFileService().GetFileShares();
        if ((await shares.GetIfExistsAsync(shareName, cancellationToken: cancellationToken)).HasValue)
        {
            _logger.Info($"既存のファイル共有を使用: {shareName}");
        }
        else
        {
            await shares.CreateOrUpdateAsync(WaitUntil.Completed, shareName, ContainerAppSpecBuilder.BuildDbFileShare(), cancellationToken: cancellationToken);
            _logger.Info($"ファイル共有を作成: {shareName}");
        }

        // キーが再生成されていてもマウントできるよう、毎回取得して登録し直す（キーはログに出さない）
        var keys = await account.GetKeysAsync(cancellationToken: cancellationToken);
        var key = keys.Value.Keys.FirstOrDefault()?.Value;
        if (key == null)
            throw new InvalidOperationException($"ストレージアカウント '{accountName}' のアカウントキーを取得できませんでした。");
        await env.GetContainerAppManagedEnvironmentStorages().CreateOrUpdateAsync(
            WaitUntil.Completed, DbPersistence.StorageName,
            ContainerAppSpecBuilder.BuildEnvironmentStorage(accountName, key, shareName), cancellationToken);
        _logger.Info($"Container Apps Environment にストレージを登録: {DbPersistence.StorageName}（{accountName}/{shareName}）");
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
        var existingDb = await apps.GetIfExistsAsync(dbAppName, cancellationToken);
        IReadOnlyDictionary<string, string>? dbTags = null;
        // 永続化を続ける場合は、新旧の DB コンテナが同じデータを同時に開かないよう、停止してから更新し、開始する
        var restartDb = false;
        if (existingDb.HasValue)
        {
            var current = existingDb.Value!;
            dbTags = new Dictionary<string, string>(current.Data.Tags);
            var currentlyPersisted = StorageNames(current.Data).Contains(DbPersistence.StorageName, StringComparer.OrdinalIgnoreCase);
            if (DbPersistence.DataLossWarning(dbAppName, currentlyPersisted, opts.PersistDb == true) is { } warning)
            {
                _logger.Warn(warning);
            }
            else
            {
                _logger.Info(DbPersistence.RestartNotice(dbAppName));
                await current.StopAsync(WaitUntil.Completed, cancellationToken);
                restartDb = true;
            }
        }
        var dbAppOp = await apps.CreateOrUpdateAsync(WaitUntil.Completed, dbAppName,
            ContainerAppSpecBuilder.WithManagedTags(ContainerAppSpecBuilder.BuildDbApp(opts, location, env.Id), appName, dbTags),
            cancellationToken);
        var dbApp = dbAppOp.Value;
        if (restartDb)
        {
            await dbApp.StartAsync(WaitUntil.Completed, cancellationToken);
            _logger.Info($"DB アプリを開始: {dbAppName}");
        }
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
            result.Add(new ContainerAppInfo(
                app.Data.Name, environmentId?.ToString(), new Dictionary<string, string>(app.Data.Tags), StorageNames(app.Data)));
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
                new Dictionary<string, string>(app.Data.Tags),
                StorageNames(app.Data)));
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

    public async Task<StorageAccountInfo?> GetStorageAccountAsync(
        string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default)
    {
        var existing = await ResourceGroup(subscriptionId, rgName).GetStorageAccounts().GetIfExistsAsync(name, cancellationToken: cancellationToken);
        if (!existing.HasValue)
            return null;
        var data = existing.Value!.Data;
        return new StorageAccountInfo(data.Name, data.Location.Name, new Dictionary<string, string>(data.Tags));
    }

    public async Task<bool> IsStorageAccountNameAvailableAsync(string subscriptionId, string name, CancellationToken cancellationToken = default)
    {
        var sub = _arm.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
        var result = await sub.CheckStorageAccountNameAvailabilityAsync(new StorageAccountNameAvailabilityContent(name), cancellationToken);
        return result.Value.IsNameAvailable == true;
    }

    public async Task<bool> FileShareExistsAsync(
        string subscriptionId, string rgName, string accountName, string shareName, CancellationToken cancellationToken = default)
    {
        var account = await ResourceGroup(subscriptionId, rgName).GetStorageAccounts().GetIfExistsAsync(accountName, cancellationToken: cancellationToken);
        return account.HasValue &&
               (await account.Value!.GetFileService().GetFileShares().GetIfExistsAsync(shareName, cancellationToken: cancellationToken)).HasValue;
    }

    public async Task<bool> EnvironmentStorageExistsAsync(
        string subscriptionId, string rgName, string envName, string storageName, CancellationToken cancellationToken = default)
    {
        var env = await ResourceGroup(subscriptionId, rgName).GetContainerAppManagedEnvironments().GetIfExistsAsync(envName, cancellationToken);
        return env.HasValue &&
               (await env.Value!.GetContainerAppManagedEnvironmentStorages().GetIfExistsAsync(storageName, cancellationToken)).HasValue;
    }

    public async Task<bool> DeleteStorageAccountAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default)
    {
        var existing = await ResourceGroup(subscriptionId, rgName).GetStorageAccounts().GetIfExistsAsync(name, cancellationToken: cancellationToken);
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
        string subscriptionId, string rgName, string resourceProviderNamespace, string resourceType, string name,
        CancellationToken cancellationToken = default)
    {
        var result = new List<PermissionSet>();
        await foreach (var p in ResourceGroup(subscriptionId, rgName)
                           .GetAzurePermissionsForResourcesAsync(resourceProviderNamespace, "", resourceType, name, cancellationToken))
        {
            result.Add(new PermissionSet(p.Actions.ToList(), p.NotActions.ToList()));
        }
        return result;
    }
}
