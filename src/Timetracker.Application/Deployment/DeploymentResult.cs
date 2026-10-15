namespace Timetracker.Application.Deployment;

/// <summary>デプロイ結果。<see cref="TimetrackerUrl"/> は Ingress の FQDN を取得できなかった場合 null。</summary>
public sealed record DeploymentResult(
    string ResourceGroup,
    string EnvironmentName,
    string TimetrackerAppName,
    string DbAppName,
    string RedisAppName,
    string? TimetrackerUrl);
