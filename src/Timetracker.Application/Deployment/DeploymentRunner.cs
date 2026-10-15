using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Application.Deployment;

/// <summary>
/// Azure Container Apps へのデプロイ手順（リソースグループ → Environment → DB / Redis / Timetracker）を実行する。
/// CLI など各フロントエンドから共通で使う。
/// </summary>
public sealed class DeploymentRunner
{
    private const int TotalSteps = 3;

    private readonly IAzureDeploymentGateway _gateway;

    public DeploymentRunner(IAzureDeploymentGateway gateway)
    {
        _gateway = gateway;
    }

    /// <exception cref="ArgumentException">設定が不正な場合（Azure へは何も送らない）</exception>
    public async Task<DeploymentResult> RunAsync(
        DeployOptions opts, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // 呼び出し元で検証済みでも、不正な設定で Azure にリソースを作らないよう実行前に必ず検証する
        ComposeGenerator.Validate(opts);

        progress?.Report(new DeploymentProgress(DeploymentStep.ResourceGroup, 1, TotalSteps, $"リソースグループ {opts.ResourceGroup}"));
        await _gateway.EnsureResourceGroupAsync(opts.Subscription, opts.ResourceGroup, opts.Location, cancellationToken);

        var envName = ResourceNaming.Environment(opts.AppName);
        progress?.Report(new DeploymentProgress(DeploymentStep.Environment, 2, TotalSteps, $"Container Apps Environment {envName}"));
        var env = await _gateway.EnsureContainerAppEnvAsync(opts.Subscription, opts.ResourceGroup, envName, opts.Location, cancellationToken);

        progress?.Report(new DeploymentProgress(DeploymentStep.ContainerApps, 3, TotalSteps, "Container Apps（DB / Redis / Timetracker）"));
        var apps = await _gateway.CreateOrUpdateContainerAppAsync(
            opts.Subscription, opts.ResourceGroup, opts.AppName, opts.Location, opts, env, cancellationToken);

        return new DeploymentResult(
            opts.ResourceGroup, envName, apps.TimetrackerAppName, apps.DbAppName, apps.RedisAppName, apps.TimetrackerUrl);
    }
}
