using Azure.ResourceManager.AppContainers;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Infrastructure.Azure;

/// <summary>
/// デプロイ・削除で行う Azure 操作。アプリケーション層はこのインターフェースに依存し、
/// 単体テストでは Azure SDK（ArmClient）をモックせずに差し替えられるようにする。
/// </summary>
public interface IAzureDeploymentGateway
{
    /// <summary>リソースグループが無ければ作成する（既存なら変更しない）。作成した場合は、本ツールが作ったことを示すタグを付ける。</summary>
    Task EnsureResourceGroupAsync(string subscriptionId, string rgName, string location, CancellationToken cancellationToken = default);

    /// <summary>アプリ名に対応する Container Apps Environment が無ければ作成する（既存なら変更しない）。</summary>
    Task<ContainerAppManagedEnvironmentResource> EnsureContainerAppEnvAsync(
        string subscriptionId, string rgName, string appName, string location, CancellationToken cancellationToken = default);

    /// <summary>
    /// DB のデータを置くストレージ（ストレージアカウント・ファイル共有）が無ければ作成し、Environment にストレージとして登録する
    /// （アカウントキーは毎回取得して登録し直す）。
    /// </summary>
    Task EnsureDbStorageAsync(
        string subscriptionId, string rgName, string appName, string location, ContainerAppManagedEnvironmentResource env,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// DB / Redis / Timetracker の各 Container App を作成または更新する。
    /// <see cref="DeployOptions.PersistDb"/> が true なら DB アプリに Azure Files をマウントし、
    /// 既に Azure Files を使っている DB アプリは、停止してから更新し、開始する。
    /// </summary>
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

    /// <summary>
    /// Container App のシークレットの名前と値（<c>listSecrets</c>。存在しなければ null）。
    /// 権限 <c>Microsoft.App/containerApps/listSecrets/action</c> が要る。値はログに書かない。
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> GetContainerAppSecretsAsync(
        string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Container App のタグに <paramref name="tags"/> を加える・上書きする（タグの API の Merge。ほかのタグとアプリの設定は変えず、新しいリビジョンも作らない）。
    /// </summary>
    Task UpdateContainerAppTagsAsync(
        string subscriptionId, string rgName, string name, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);

    /// <summary>Container Apps Environment のタグに <paramref name="tags"/> を加える・上書きする（タグの API の Merge。設定とほかのタグは変えない）。</summary>
    Task UpdateEnvironmentTagsAsync(
        string subscriptionId, string rgName, string envName, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);

    /// <summary>ストレージアカウントのタグに <paramref name="tags"/> を加える・上書きする（タグの API の Merge。設定とほかのタグは変えない）。</summary>
    Task UpdateStorageAccountTagsAsync(
        string subscriptionId, string rgName, string accountName, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);

    /// <summary>ストレージアカウントの情報（存在しなければ null）。</summary>
    Task<StorageAccountInfo?> GetStorageAccountAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default);

    /// <summary>ストレージアカウントの名前が使えるか（Azure 全体で一意のため、他で使われていれば false）。</summary>
    Task<bool> IsStorageAccountNameAvailableAsync(string subscriptionId, string name, CancellationToken cancellationToken = default);

    /// <summary>ストレージアカウントにファイル共有があるか。</summary>
    Task<bool> FileShareExistsAsync(
        string subscriptionId, string rgName, string accountName, string shareName, CancellationToken cancellationToken = default);

    /// <summary>Container Apps Environment にストレージが登録されているか。</summary>
    Task<bool> EnvironmentStorageExistsAsync(
        string subscriptionId, string rgName, string envName, string storageName, CancellationToken cancellationToken = default);

    /// <summary>Container App を削除する。存在しなければ何もしない。</summary>
    /// <returns>削除した場合 true</returns>
    Task<bool> DeleteContainerAppAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default);

    /// <summary>Container Apps Environment を削除する。存在しなければ何もしない。</summary>
    /// <returns>削除した場合 true</returns>
    Task<bool> DeleteEnvironmentAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default);

    /// <summary>ストレージアカウントを削除する（中のファイル共有とデータも消える）。存在しなければ何もしない。</summary>
    /// <returns>削除した場合 true</returns>
    Task<bool> DeleteStorageAccountAsync(string subscriptionId, string rgName, string name, CancellationToken cancellationToken = default);

    /// <summary>リソースグループの中のリソース（入れ子でないもの）の一覧。</summary>
    Task<IReadOnlyList<AzureResourceInfo>> ListResourcesAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default);

    /// <summary>リソースグループを削除する（中に残っているリソースもすべて消える）。存在しなければ何もしない。</summary>
    /// <returns>削除した場合 true</returns>
    Task<bool> DeleteResourceGroupAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default);

    /// <summary>実行している ID が、リソースグループで持つ権限の一覧。</summary>
    Task<IReadOnlyList<PermissionSet>> GetPermissionsAsync(string subscriptionId, string rgName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 実行している ID が、リソースグループ内の既存のリソース（<paramref name="resourceProviderNamespace"/> の <paramref name="resourceType"/> と名前）で持つ権限の一覧
    /// （リソースに割り当てられたロールと、上位のスコープから継承したロールを含む）。
    /// </summary>
    Task<IReadOnlyList<PermissionSet>> GetResourcePermissionsAsync(
        string subscriptionId, string rgName, string resourceProviderNamespace, string resourceType, string name,
        CancellationToken cancellationToken = default);
}

/// <summary>作成・更新した Container App の名前と、Timetracker のエンドポイント URL（取得できなければ null）。</summary>
public sealed record ContainerAppsDeployment(string TimetrackerAppName, string DbAppName, string RedisAppName, string? TimetrackerUrl);

/// <summary>リソースグループの情報。<see cref="Tags"/> は取得しなければ null。</summary>
public sealed record ResourceGroupInfo(string Name, string Location, IReadOnlyDictionary<string, string>? Tags = null)
{
    /// <summary>本ツールがデプロイのときに作ったリソースグループか（作ったときに管理タグの managed-by を付ける）。</summary>
    public bool CreatedByTool => ManagedTags.IsCreatedByTool(Tags);
}

/// <summary>リソースグループの中のリソース。<see cref="Type"/> は「Microsoft.App/containerApps」のような種類。</summary>
public sealed record AzureResourceInfo(string Name, string Type);

/// <summary>Container Apps Environment の情報。<see cref="ProvisioningState"/> は取得できなければ null。</summary>
public sealed record ManagedEnvironmentInfo(
    string Id, string Name, string Location, IReadOnlyDictionary<string, string> Tags, string? ProvisioningState = null);

/// <summary>
/// Container App の情報。<see cref="EnvironmentId"/> は所属する Environment のリソース ID。
/// <see cref="StorageNames"/> は、ボリュームで使っている Environment のストレージ（Azure Files）の名前。
/// </summary>
public sealed record ContainerAppInfo(
    string Name, string? EnvironmentId, IReadOnlyDictionary<string, string> Tags, IReadOnlyList<string>? StorageNames = null)
{
    /// <summary>DB のデータを置くストレージ（<see cref="DbPersistence.StorageName"/>）を使っているか。</summary>
    public bool UsesDbStorage => DbStorage.IsUsed(StorageNames);
}

/// <summary>ストレージアカウントの情報。</summary>
public sealed record StorageAccountInfo(string Name, string Location, IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// Container App の詳細。取得できない項目は null（作成中などで値がまだ無い場合がある）。
/// <see cref="Image"/>・<see cref="Cpu"/>・<see cref="Memory"/> は最初のコンテナの値。<see cref="Fqdn"/> は Ingress の FQDN。
/// <see cref="StorageNames"/> は、ボリュームで使っている Environment のストレージ（Azure Files）の名前。
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
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyList<string>? StorageNames = null)
{
    /// <summary>DB のデータを置くストレージ（<see cref="DbPersistence.StorageName"/>）を使っているか。</summary>
    public bool UsesDbStorage => DbStorage.IsUsed(StorageNames);
}

internal static class DbStorage
{
    public static bool IsUsed(IReadOnlyList<string>? storageNames) =>
        storageNames?.Contains(DbPersistence.StorageName, StringComparer.OrdinalIgnoreCase) == true;
}
