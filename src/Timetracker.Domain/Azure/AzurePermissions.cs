namespace Timetracker.Domain.Azure;

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

    /// <summary>デプロイで常に必要な権限（Environment を新規作成する場合は <see cref="EnvironmentWrite"/> も必要）。</summary>
    public static readonly IReadOnlyList<string> Deploy = [ContainerAppRead, ContainerAppWrite, EnvironmentRead, EnvironmentJoin];
}
