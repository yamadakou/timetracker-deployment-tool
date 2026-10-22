using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Application.Deployment;

/// <summary>
/// デプロイ結果。<see cref="TimetrackerUrl"/> は Ingress の FQDN を取得できなかった場合 null。
/// <see cref="StorageAccountName"/> は DB のデータを置いたストレージアカウント（永続化しない場合は null）。
/// <see cref="Expiry"/> は、記録した利用可能期限。
/// </summary>
public sealed record DeploymentResult(
    string ResourceGroup,
    string EnvironmentName,
    string TimetrackerAppName,
    string DbAppName,
    string RedisAppName,
    string? TimetrackerUrl,
    string? StorageAccountName = null,
    ExpiryDecision? Expiry = null);
