using Azure.ResourceManager.AppContainers;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Desktop.Fixtures;

/// <summary>
/// UI テストのモックの Azure の操作（desktop-ui-tests.md の 6.2）。読み取りは、モックのデータのリソースから答える（権限はすべてあり、
/// ストレージアカウントの名前は、データに無ければ使える）。書き込み（作成・更新・タグ・削除）は、<see cref="FixtureData.WriteDelaySeconds"/> 待ってから終わる
/// （データは変えない）。そのため、デプロイ・削除のジョブは計画を通ってしばらく実行中のまま残り、「停止」で次の手順の境目で止まる（一覧に実行中の行を出すため）。
/// Azure の SDK の型を返す書き込み（Environment の作成）は、待った後に例外にする（デプロイのジョブは失敗で終わる）。
/// </summary>
public sealed class FixtureGateway(FixtureData data) : IAzureDeploymentGateway
{
    private static readonly IReadOnlyList<PermissionSet> AllPermissions = [new(["*"], [])];

    // 書き込みは、決めた時間を待ってから終わる（取り消すと OperationCanceledException）
    private Task HoldAsync(CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromSeconds(data.WriteDelaySeconds), cancellationToken);

    private async Task<bool> DeletedAsync(CancellationToken cancellationToken)
    {
        await HoldAsync(cancellationToken);
        return true;
    }

    private async Task<T> CannotCreateAsync<T>(CancellationToken cancellationToken)
    {
        await HoldAsync(cancellationToken);
        throw new InvalidOperationException("UI テストのモックでは、Container Apps を作成しません。");
    }

    private IEnumerable<ManagedResourceRecord> In(string subscriptionId, string rgName) =>
        data.Resources.Where(r => Same(r.SubscriptionId, subscriptionId) && Same(r.ResourceGroup, rgName));

    private ManagedResourceRecord? Find(string subscriptionId, string rgName, string type, string name) =>
        In(subscriptionId, rgName).FirstOrDefault(r => r.Type == type && Same(r.Name, name));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public Task EnsureResourceGroupAsync(string subscriptionId, string rgName, string location, CancellationToken cancellationToken = default) =>
        HoldAsync(cancellationToken);

    public Task<ContainerAppManagedEnvironmentResource> EnsureContainerAppEnvAsync(
        string subscriptionId, string rgName, string appName, string location, IReadOnlyDictionary<string, string>? lifecycleTags,
        CancellationToken cancellationToken = default) => CannotCreateAsync<ContainerAppManagedEnvironmentResource>(cancellationToken);

    public Task EnsureDbStorageAsync(
        string subscriptionId, string rgName, string appName, string location, ContainerAppManagedEnvironmentResource env,
        CancellationToken cancellationToken = default) => HoldAsync(cancellationToken);

    public Task<ContainerAppsDeployment> CreateOrUpdateContainerAppAsync(
        string subscriptionId, string rgName, string appName, string location, DeployOptions opts,
        ContainerAppManagedEnvironmentResource env, CancellationToken cancellationToken = default) => CannotCreateAsync<ContainerAppsDeployment>(cancellationToken);

    public Task<ResourceGroupInfo?> GetResourceGroupAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default)
    {
        var group = data.ResourceGroups?.FirstOrDefault(g => Same(g.SubscriptionId, subscriptionId) && Same(g.Name, rgName));
        var first = In(subscriptionId, rgName).FirstOrDefault();
        return Task.FromResult(group != null ? new ResourceGroupInfo(group.Name, group.Location, group.Tags)
            : first != null ? new ResourceGroupInfo(first.ResourceGroup, first.Location)
            : null);
    }

    public Task<ManagedEnvironmentInfo?> GetEnvironmentAsync(string subscriptionId, string rgName, string envName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(subscriptionId, rgName, ManagedResourceRecord.EnvironmentType, envName) is { } env
            ? new ManagedEnvironmentInfo(env.Id, env.Name, env.Location, env.Tags, "Succeeded")
            : null);

    public Task<IReadOnlyList<ContainerAppInfo>> ListContainerAppsAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default)
    {
        var environment = In(subscriptionId, rgName).FirstOrDefault(r => r.Type == ManagedResourceRecord.EnvironmentType)?.Id;
        return Task.FromResult<IReadOnlyList<ContainerAppInfo>>([.. Apps(subscriptionId, rgName).Select(a => new ContainerAppInfo(a.Name, environment, a.Tags, a.App?.StorageNames))]);
    }

    public Task<IReadOnlyList<ContainerAppDetails>> ListContainerAppDetailsAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ContainerAppDetails>>([.. Apps(subscriptionId, rgName).Select(a => new ContainerAppDetails(
            a.Name, a.App?.ProvisioningState, a.App?.RunningStatus, a.App?.Image, a.App?.Cpu, a.App?.Memory, null, a.App?.Fqdn, a.Tags, a.App?.StorageNames))]);

    private IEnumerable<ManagedResourceRecord> Apps(string subscriptionId, string rgName) =>
        In(subscriptionId, rgName).Where(r => r.Type == ManagedResourceRecord.ContainerAppType);

    public Task<IReadOnlyDictionary<string, string>?> GetContainerAppSecretsAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Secrets?.FirstOrDefault(s => Same(s.SubscriptionId, subscriptionId) && Same(s.ResourceGroup, rgName) && Same(s.ContainerApp, name))?.Values);

    public Task UpdateContainerAppTagsAsync(string subscriptionId, string rgName, string name, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default) =>
        HoldAsync(cancellationToken);

    public Task UpdateStorageAccountTagsAsync(string subscriptionId, string rgName, string accountName, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default) =>
        HoldAsync(cancellationToken);

    public Task DeleteContainerAppTagsAsync(string subscriptionId, string rgName, string name, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default) =>
        HoldAsync(cancellationToken);

    public Task DeleteStorageAccountTagsAsync(string subscriptionId, string rgName, string accountName, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default) =>
        HoldAsync(cancellationToken);

    public Task<StorageAccountInfo?> GetStorageAccountAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(subscriptionId, rgName, ManagedResourceRecord.StorageAccountType, name) is { } account
            ? new StorageAccountInfo(account.Name, account.Location, account.Tags)
            : null);

    public Task<bool> IsStorageAccountNameAvailableAsync(string subscriptionId, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(!data.Resources.Any(r => r.Type == ManagedResourceRecord.StorageAccountType && Same(r.Name, name)));

    public Task<bool> FileShareExistsAsync(string subscriptionId, string rgName, string accountName, string shareName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(subscriptionId, rgName, ManagedResourceRecord.StorageAccountType, accountName) != null);

    public Task<bool> EnvironmentStorageExistsAsync(string subscriptionId, string rgName, string envName, string storageName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Apps(subscriptionId, rgName).Any(a => a.App?.StorageNames.Contains(storageName, StringComparer.OrdinalIgnoreCase) == true));

    public Task<bool> DeleteContainerAppAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default) =>
        DeletedAsync(cancellationToken);

    public Task<bool> DeleteEnvironmentAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default) =>
        DeletedAsync(cancellationToken);

    public Task<bool> DeleteStorageAccountAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default) =>
        DeletedAsync(cancellationToken);

    public Task<IReadOnlyList<AzureResourceInfo>> ListResourcesAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AzureResourceInfo>>([.. In(subscriptionId, rgName).Select(r => new AzureResourceInfo(r.Name, r.Type))]);

    public Task<bool> DeleteResourceGroupAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default) =>
        DeletedAsync(cancellationToken);

    public Task<IReadOnlyList<PermissionSet>> GetPermissionsAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default) =>
        Task.FromResult(AllPermissions);

    public Task<IReadOnlyList<PermissionSet>> GetResourcePermissionsAsync(
        string subscriptionId, string rgName, string resourceProviderNamespace, string resourceType, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(AllPermissions);
}
