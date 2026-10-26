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
    private readonly TimeProvider _time;

    /// <param name="time">利用可能期限を決める時刻と時刻帯・デプロイした時刻（省略時はシステムの時刻）</param>
    public DeploymentRunner(IAzureDeploymentGateway gateway, TimeProvider? time = null)
    {
        _gateway = gateway;
        _time = time ?? TimeProvider.System;
    }

    /// <exception cref="ArgumentException">設定が不正な場合（今日より前の利用可能期限日を含む。Azure へは何も送らない）</exception>
    public async Task<DeploymentResult> RunAsync(
        DeployOptions opts, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // 呼び出し元で検証済みでも、不正な設定で Azure にリソースを作らないよう実行前に必ず検証する
        ComposeGenerator.Validate(opts);

        // 永続化の指定が無ければ、既存の DB アプリの今の状態を引き継ぐ（リソースグループが無ければ新規なので永続化しない）
        var persist = opts.PersistDb ?? await DeploymentPlanner.UsesDbStorageAsync(_gateway, opts, cancellationToken);
        opts = opts.WithPersistDb(persist);

        // 利用可能期限を、実行する時刻と今のデプロイのタグで決め、本ツールで作るリソース（ストレージアカウント・3 つの Container App。Environment は作るときだけ）に記録する
        // （docs/design/desktop-jobs.md の 6.1）
        var existingApps = await _gateway.GetResourceGroupAsync(opts.Subscription, opts.ResourceGroup, cancellationToken) == null
            ? []
            : await _gateway.ListContainerAppsAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        var (expiry, expiryError) = new DeploymentPlanner(_gateway, _time).ResolveExpiry(opts, existingApps);
        if (expiryError != null)
            throw new ArgumentException(expiryError);
        var lifecycleTags = new Dictionary<string, string>
        {
            [DeploymentExpiry.ExpiresAtKey] = expiry.TagValue,
            [DeploymentExpiry.DeployedAtKey] = DeploymentExpiry.FormatTag(_time.GetUtcNow()),
        };
        opts = opts.WithLifecycleTags(lifecycleTags);
        var total = persist ? 4 : 3;
        var index = 0;

        progress?.Report(new DeploymentProgress(DeploymentStep.ResourceGroup, ++index, total, $"リソースグループ {opts.ResourceGroup}"));
        await _gateway.EnsureResourceGroupAsync(opts.Subscription, opts.ResourceGroup, opts.Location, cancellationToken);

        var envName = ResourceNaming.Environment(opts.AppName);
        progress?.Report(new DeploymentProgress(DeploymentStep.Environment, ++index, total, $"Container Apps Environment {envName}"));
        var env = await _gateway.EnsureContainerAppEnvAsync(opts.Subscription, opts.ResourceGroup, opts.AppName, opts.Location, lifecycleTags, cancellationToken);

        string? storageAccount = null;
        if (persist)
        {
            storageAccount = ResourceNaming.StorageAccount(opts.Subscription, opts.ResourceGroup, opts.AppName);
            progress?.Report(new DeploymentProgress(DeploymentStep.DbStorage, ++index, total, $"DB のストレージ {storageAccount}（Azure Files）"));
            await _gateway.EnsureDbStorageAsync(opts.Subscription, opts.ResourceGroup, opts.AppName, opts.Location, env, cancellationToken);
            if (ManagedTags.IsManaged((await _gateway.GetStorageAccountAsync(opts.Subscription, opts.ResourceGroup, storageAccount, cancellationToken))?.Tags, opts.AppName))
                await _gateway.UpdateStorageAccountTagsAsync(opts.Subscription, opts.ResourceGroup, storageAccount, lifecycleTags, cancellationToken);
        }

        progress?.Report(new DeploymentProgress(DeploymentStep.ContainerApps, ++index, total, "Container Apps（DB / Redis / Timetracker）"));
        var apps = await _gateway.CreateOrUpdateContainerAppAsync(
            opts.Subscription, opts.ResourceGroup, opts.AppName, opts.Location, opts, env, cancellationToken);

        return new DeploymentResult(
            opts.ResourceGroup, envName, apps.TimetrackerAppName, apps.DbAppName, apps.RedisAppName, apps.TimetrackerUrl, storageAccount, expiry);
    }
}
