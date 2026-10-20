namespace TTDeployTool.Domain.Azure;

/// <summary>本ツールが使う Azure の権限（Action）。</summary>
public static class AzurePermissions
{
    public const string ContainerAppRead = "Microsoft.App/containerApps/read";
    public const string ContainerAppWrite = "Microsoft.App/containerApps/write";
    public const string ContainerAppDelete = "Microsoft.App/containerApps/delete";
    public const string EnvironmentRead = "Microsoft.App/managedEnvironments/read";
    public const string EnvironmentWrite = "Microsoft.App/managedEnvironments/write";
    public const string EnvironmentDelete = "Microsoft.App/managedEnvironments/delete";
    public const string EnvironmentJoin = "Microsoft.App/managedEnvironments/join/action";
    public const string ContainerAppStop = "Microsoft.App/containerApps/stop/action";
    public const string ContainerAppStart = "Microsoft.App/containerApps/start/action";
    public const string EnvironmentStorageRead = "Microsoft.App/managedEnvironments/storages/read";
    public const string EnvironmentStorageWrite = "Microsoft.App/managedEnvironments/storages/write";

    // DB のデータの永続化（Azure Files）
    public const string StorageAccountRead = "Microsoft.Storage/storageAccounts/read";
    public const string StorageAccountWrite = "Microsoft.Storage/storageAccounts/write";
    public const string StorageAccountDelete = "Microsoft.Storage/storageAccounts/delete";
    public const string StorageAccountListKeys = "Microsoft.Storage/storageAccounts/listkeys/action";
    public const string FileShareRead = "Microsoft.Storage/storageAccounts/fileServices/shares/read";
    public const string FileShareWrite = "Microsoft.Storage/storageAccounts/fileServices/shares/write";

    /// <summary>リソースグループの削除（中のリソースを個別に削除する権限は要らない）。</summary>
    public const string ResourceGroupDelete = "Microsoft.Resources/subscriptions/resourceGroups/delete";

    /// <summary>デプロイで常に必要な権限（Environment を新規作成する場合は <see cref="EnvironmentWrite"/> も必要）。</summary>
    public static readonly IReadOnlyList<string> Deploy = [ContainerAppRead, ContainerAppWrite, EnvironmentRead, EnvironmentJoin];
}
