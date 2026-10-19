using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Application.Deployment;

/// <summary>
/// Azure Container Apps へのデプロイ手順（リソースグループ → Environment →（永続化する場合）DB のストレージ → DB / Redis / Timetracker）を実行する。
/// CLI など各フロントエンドから共通で使う。
/// </summary>
public sealed class DeploymentRunner
{
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

        // 永続化の指定が無ければ、既存の DB アプリの今の状態を引き継ぐ（リソースグループが無ければ新規なので永続化しない）
        var persist = opts.PersistDb ?? await DeploymentPlanner.UsesDbStorageAsync(_gateway, opts, cancellationToken);
        opts = opts.WithPersistDb(persist);
        var total = persist ? 4 : 3;
        var index = 0;

        progress?.Report(new DeploymentProgress(DeploymentStep.ResourceGroup, ++index, total, $"リソースグループ {opts.ResourceGroup}"));
        await _gateway.EnsureResourceGroupAsync(opts.Subscription, opts.ResourceGroup, opts.Location, cancellationToken);

        var envName = ResourceNaming.Environment(opts.AppName);
        progress?.Report(new DeploymentProgress(DeploymentStep.Environment, ++index, total, $"Container Apps Environment {envName}"));
        var env = await _gateway.EnsureContainerAppEnvAsync(opts.Subscription, opts.ResourceGroup, opts.AppName, opts.Location, cancellationToken);

        string? storageAccount = null;
        if (persist)
        {
            storageAccount = ResourceNaming.StorageAccount(opts.Subscription, opts.ResourceGroup, opts.AppName);
            progress?.Report(new DeploymentProgress(DeploymentStep.DbStorage, ++index, total, $"DB のストレージ {storageAccount}（Azure Files）"));
            await _gateway.EnsureDbStorageAsync(opts.Subscription, opts.ResourceGroup, opts.AppName, opts.Location, env, cancellationToken);
        }

        progress?.Report(new DeploymentProgress(DeploymentStep.ContainerApps, ++index, total, "Container Apps（DB / Redis / Timetracker）"));
        var apps = await _gateway.CreateOrUpdateContainerAppAsync(
            opts.Subscription, opts.ResourceGroup, opts.AppName, opts.Location, opts, env, cancellationToken);

        return new DeploymentResult(
            opts.ResourceGroup, envName, apps.TimetrackerAppName, apps.DbAppName, apps.RedisAppName, apps.TimetrackerUrl, storageAccount);
    }
}
