using Azure.ResourceManager.AppContainers;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;

namespace Timetracker.Infrastructure.Azure;

/// <summary>
/// デプロイ・削除で行う Azure 操作。アプリケーション層はこのインターフェースに依存し、
/// 単体テストでは Azure SDK（ArmClient）をモックせずに差し替えられるようにする。
/// </summary>
public interface IAzureDeploymentGateway
{
    /// <summary>リソースグループが無ければ作成する（既存なら変更しない）。</summary>
    Task EnsureResourceGroupAsync(string subscriptionId, string rgName, string location, CancellationToken cancellationToken = default);

    /// <summary>アプリ名に対応する Container Apps Environment が無ければ作成する（既存なら変更しない）。</summary>
    Task<ContainerAppManagedEnvironmentResource> EnsureContainerAppEnvAsync(
        string subscriptionId, string rgName, string appName, string location, CancellationToken cancellationToken = default);

    /// <summary>DB / Redis / Timetracker の各 Container App を作成または更新する。</summary>
    Task<ContainerAppsDeployment> CreateOrUpdateContainerAppAsync(
        string subscriptionId, string rgName, string appName, string location, DeployOptions opts,
        ContainerAppManagedEnvironmentResource env, CancellationToken cancellationToken = default);

    /// <summary>リソースグループの情報（存在しなければ null）。</summary>
    Task<ResourceGroupInfo?> GetResourceGroupAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default);

    /// <summary>Container Apps Environment の情報（存在しなければ null）。</summary>
    Task<ManagedEnvironmentInfo?> GetEnvironmentAsync(
        string subscriptionId, string rgName, string envName, CancellationToken cancellationToken = default);

    /// <summary>リソースグループ内の Container App の一覧。</summary>
    Task<IReadOnlyList<ContainerAppInfo>> ListContainerAppsAsync(
        string subscriptionId, string rgName, CancellationToken cancellationToken = default);

    /// <summary>リソースグループ内の Container App の詳細（状態・イメージ・スペック・URL）の一覧。</summary>
    Task<IReadOnlyList<ContainerAppDetails>> ListContainerAppDetailsAsync(
        string subscriptionId, string rgName, CancellationToken cancellationToken = default);

    /// <summary>Container App を削除する。存在しなければ何もしない。</summary>
    /// <returns>削除した場合 true</returns>
    Task<bool> DeleteContainerAppAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default);

    /// <summary>Container Apps Environment を削除する。存在しなければ何もしない。</summary>
    /// <returns>削除した場合 true</returns>
    Task<bool> DeleteEnvironmentAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default);

    /// <summary>実行している ID が、リソースグループで持つ権限の一覧。</summary>
    Task<IReadOnlyList<PermissionSet>> GetPermissionsAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 実行している ID が、リソースグループ内の既存のリソース（Microsoft.App の <paramref name="resourceType"/> と名前）で持つ権限の一覧
    /// （リソースに割り当てられたロールと、上位のスコープから継承したロールを含む）。
    /// </summary>
    Task<IReadOnlyList<PermissionSet>> GetResourcePermissionsAsync(
        string subscriptionId, string rgName, string resourceType, string name, CancellationToken cancellationToken = default);
}

/// <summary>作成・更新した Container App の名前と、Timetracker のエンドポイント URL（取得できなければ null）。</summary>
public sealed record ContainerAppsDeployment(string TimetrackerAppName, string DbAppName, string RedisAppName, string? TimetrackerUrl);

public sealed record ResourceGroupInfo(string Name, string Location);

/// <summary>Container Apps Environment の情報。<see cref="ProvisioningState"/> は取得できなければ null。</summary>
public sealed record ManagedEnvironmentInfo(
    string Id, string Name, string Location, IReadOnlyDictionary<string, string> Tags, string? ProvisioningState = null);

/// <summary>Container App の情報。<see cref="EnvironmentId"/> は所属する Environment のリソース ID。</summary>
public sealed record ContainerAppInfo(string Name, string? EnvironmentId, IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// Container App の詳細。取得できない項目は null（作成中などで値がまだ無い場合がある）。
/// <see cref="Image"/>・<see cref="Cpu"/>・<see cref="Memory"/> は最初のコンテナの値。<see cref="Fqdn"/> は Ingress の FQDN。
/// </summary>
public sealed record ContainerAppDetails(
    string Name,
    string? ProvisioningState,
    string? RunningStatus,
    string? Image,
    double? Cpu,
    string? Memory,
    string? LatestRevisionName,
    string? Fqdn,
    IReadOnlyDictionary<string, string> Tags);
