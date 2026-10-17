using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Application.Deployment;

/// <summary>
/// デプロイ済みの環境の状態（MCP の get_deployment_info）。存在しないリソースは null。
/// <see cref="StorageAccount"/> は DB のデータを置くストレージアカウント（永続化していない、または削除済みなら null）。
/// </summary>
public sealed record DeploymentInfo(
    string Subscription,
    string ResourceGroup,
    string AppName,
    ResourceGroupInfo? ResourceGroupInfo,
    ManagedEnvironmentInfo? Environment,
    ContainerAppDetails? Timetracker,
    ContainerAppDetails? Db,
    ContainerAppDetails? Redis,
    StorageAccountInfo? StorageAccount = null)
{
    /// <summary>本ツールが作るリソースのいずれかがあるか。</summary>
    public bool Exists => Environment != null || Timetracker != null || Db != null || Redis != null || StorageAccount != null;

    /// <summary>DB アプリが DB のデータを Azure Files に置いているか。</summary>
    public bool DbPersisted => Db?.UsesDbStorage == true;

    /// <summary>Timetracker の URL（Ingress の FQDN が無ければ null）。</summary>
    public string? Endpoint => Timetracker?.Fqdn is { } fqdn ? $"https://{fqdn}" : null;
}

/// <summary>デプロイ済みの環境の状態を読む（Azure は変更しない）。</summary>
public sealed class DeploymentInspector
{
    private readonly IAzureDeploymentGateway _gateway;

    public DeploymentInspector(IAzureDeploymentGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<DeploymentInfo> InspectAsync(string subscriptionId, string rgName, string appName, CancellationToken cancellationToken = default)
    {
        var rg = await _gateway.GetResourceGroupAsync(subscriptionId, rgName, cancellationToken);
        if (rg == null)
            return new DeploymentInfo(subscriptionId, rgName, appName, null, null, null, null, null);

        var env = await _gateway.GetEnvironmentAsync(subscriptionId, rgName, ResourceNaming.Environment(appName), cancellationToken);
        var apps = await _gateway.ListContainerAppDetailsAsync(subscriptionId, rgName, cancellationToken);
        ContainerAppDetails? App(string name) => apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        var storage = await _gateway.GetStorageAccountAsync(
            subscriptionId, rgName, ResourceNaming.StorageAccount(subscriptionId, rgName, appName), cancellationToken);
        return new DeploymentInfo(subscriptionId, rgName, appName, rg, env,
            App(ResourceNaming.TimetrackerApp(appName)), App(ResourceNaming.DbApp(appName)), App(ResourceNaming.RedisApp(appName)), storage);
    }
}
