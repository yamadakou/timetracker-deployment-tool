using Azure.ResourceManager.AppContainers;
using Timetracker.Domain.Deployment;

namespace Timetracker.Infrastructure.Azure;

/// <summary>
/// デプロイで行う Azure 操作。アプリケーション層はこのインターフェースに依存し、
/// 単体テストでは Azure SDK（ArmClient）をモックせずに差し替えられるようにする。
/// </summary>
public interface IAzureDeploymentGateway
{
    /// <summary>リソースグループが無ければ作成する（既存なら変更しない）。</summary>
    Task EnsureResourceGroupAsync(string subscriptionId, string rgName, string location, CancellationToken cancellationToken = default);

    /// <summary>Container Apps Environment が無ければ作成する（既存なら変更しない）。</summary>
    Task<ContainerAppManagedEnvironmentResource> EnsureContainerAppEnvAsync(
        string subscriptionId, string rgName, string envName, string location, CancellationToken cancellationToken = default);

    /// <summary>DB / Redis / Timetracker の各 Container App を作成または更新する。</summary>
    Task<ContainerAppsDeployment> CreateOrUpdateContainerAppAsync(
        string subscriptionId, string rgName, string appName, string location, DeployOptions opts,
        ContainerAppManagedEnvironmentResource env, CancellationToken cancellationToken = default);
}

/// <summary>作成・更新した Container App の名前と、Timetracker のエンドポイント URL（取得できなければ null）。</summary>
public sealed record ContainerAppsDeployment(string TimetrackerAppName, string DbAppName, string RedisAppName, string? TimetrackerUrl);
