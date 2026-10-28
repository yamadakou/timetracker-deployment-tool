using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Application.Inventory;

/// <summary>Timetracker アプリのシークレットに入っているパスワード（無ければ null）。</summary>
public sealed record DeploymentPasswords(string? DbPassword, string? TrackerPassword);

/// <summary>
/// デプロイの一覧の行の操作のうち、Azure を直接変える・読むもの（docs/design/desktop/desktop-jobs.md の 6.4）。
/// どちらも、本ツールで作った（管理タグが一致する）Container App だけを対象にする。
/// </summary>
public sealed class DeploymentMaintenance
{
    private readonly IAzureDeploymentGateway _gateway;
    private readonly TimeProvider _time;

    /// <param name="time">今日より前の日付かを決める時刻と時刻帯（省略時はシステムの時刻）</param>
    public DeploymentMaintenance(IAzureDeploymentGateway gateway, TimeProvider? time = null)
    {
        _gateway = gateway;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 利用可能期限を変える（<paramref name="expiresOn"/> は yyyy-MM-dd か none）。本ツールで作ったリソース（3 つの Container App・
    /// ストレージアカウント）のうち、あって管理タグが一致するものの <see cref="DeploymentExpiry.ExpiresAtKey"/> を書き換える。1 つも無ければ変えない。
    /// Container Apps Environment は、タグの変更でも数分「更新中」になるため書き換えない（期限は作ったときの値のまま。docs/design/desktop/desktop-jobs.md の 6.1）。
    /// </summary>
    /// <exception cref="ArgumentException">入力の誤り・今日より前の日付</exception>
    /// <exception cref="InvalidOperationException">本ツールで作ったリソースが無い</exception>
    public async Task<ExpiryDecision> ChangeExpiryAsync(
        string subscriptionId, string resourceGroup, string appName, string expiresOn, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        if (string.IsNullOrWhiteSpace(expiresOn))
            throw new ArgumentException("利用可能期限日（yyyy-MM-dd）か none を指定してください。", nameof(expiresOn));
        if ((DeploymentExpiry.FormatError(expiresOn) ?? DeploymentExpiry.PastDateError(expiresOn, now, _time.LocalTimeZone)) is { } error)
            throw new ArgumentException(error, nameof(expiresOn));
        var decision = DeploymentExpiry.Resolve(expiresOn, null, now, _time.LocalTimeZone);

        var names = await ManagedAppsAsync(subscriptionId, resourceGroup, appName, cancellationToken);
        var account = ResourceNaming.StorageAccount(subscriptionId, resourceGroup, appName);
        var storage = ManagedTags.IsManaged((await _gateway.GetStorageAccountAsync(subscriptionId, resourceGroup, account, cancellationToken))?.Tags, appName);
        if (names.Count == 0 && !storage)
            throw new InvalidOperationException(
                $"リソースグループ {resourceGroup} に、アプリ名 {appName} の、本ツールで作った Container App とストレージアカウントがありません" +
                "（削除された可能性があります）。一覧を読み直してください。");
        var tags = new Dictionary<string, string> { [DeploymentExpiry.ExpiresAtKey] = decision.TagValue };
        foreach (var name in names)
            await _gateway.UpdateContainerAppTagsAsync(subscriptionId, resourceGroup, name, tags, cancellationToken);
        if (storage)
            await _gateway.UpdateStorageAccountTagsAsync(subscriptionId, resourceGroup, account, tags, cancellationToken);
        return decision;
    }

    /// <summary>Timetracker アプリのシークレットから、DB と Timetracker のパスワードを読む（値はログに書かない）。</summary>
    /// <exception cref="InvalidOperationException">本ツールで作った Timetracker アプリが無い</exception>
    public async Task<DeploymentPasswords> ReadPasswordsAsync(
        string subscriptionId, string resourceGroup, string appName, CancellationToken cancellationToken = default)
    {
        var tt = ResourceNaming.TimetrackerApp(appName);
        var secrets = (await ManagedAppsAsync(subscriptionId, resourceGroup, appName, cancellationToken)).Contains(tt)
            ? await _gateway.GetContainerAppSecretsAsync(subscriptionId, resourceGroup, tt, cancellationToken)
            : null;
        if (secrets == null)
            throw new InvalidOperationException(
                $"リソースグループ {resourceGroup} に、本ツールで作った Timetracker の Container App（{tt}）がありません。一覧を読み直してください。");
        return new DeploymentPasswords(
            secrets.GetValueOrDefault(ContainerAppSpecBuilder.DbPasswordSecretName),
            secrets.GetValueOrDefault(ContainerAppSpecBuilder.AppPasswordSecretName));
    }

    /// <summary>
    /// 今の利用可能期限のタグを Azure から読み直す（期限での削除の直前の確認。docs/design/desktop/desktop-jobs.md の 6.5）。
    /// 本ツールで作った 3 つの Container App とストレージアカウントの値を <see cref="DeploymentExpiry.Combine"/> でまとめ、どちらも無ければ
    /// Environment の値を使う（Environment のタグは作ったときの値のまま。6.1）。読める値が無ければ null。
    /// </summary>
    public async Task<string?> ReadExpiryAsync(string subscriptionId, string resourceGroup, string appName, CancellationToken cancellationToken = default)
    {
        var names = new[] { ResourceNaming.TimetrackerApp(appName), ResourceNaming.DbApp(appName), ResourceNaming.RedisApp(appName) };
        var tags = (await _gateway.ListContainerAppsAsync(subscriptionId, resourceGroup, cancellationToken))
            .Where(a => names.Contains(a.Name, StringComparer.OrdinalIgnoreCase) && ManagedTags.IsManaged(a.Tags, appName))
            .Select(a => a.Tags)
            .ToList();
        var storage = await _gateway.GetStorageAccountAsync(subscriptionId, resourceGroup, ResourceNaming.StorageAccount(subscriptionId, resourceGroup, appName), cancellationToken);
        if (ManagedTags.IsManaged(storage?.Tags, appName))
            tags.Add(storage!.Tags);
        if (tags.Count == 0)
        {
            var env = await _gateway.GetEnvironmentAsync(subscriptionId, resourceGroup, ResourceNaming.Environment(appName), cancellationToken);
            if (ManagedTags.IsManaged(env?.Tags, appName))
                tags.Add(env!.Tags);
        }
        return DeploymentExpiry.Combine(tags.Select(t => t.FirstOrDefault(x => string.Equals(x.Key, DeploymentExpiry.ExpiresAtKey, StringComparison.OrdinalIgnoreCase)).Value));
    }

    // 3 つの Container App のうち、あって管理タグが一致するもの
    private async Task<IReadOnlyList<string>> ManagedAppsAsync(string subscriptionId, string resourceGroup, string appName, CancellationToken cancellationToken)
    {
        var apps = await _gateway.ListContainerAppsAsync(subscriptionId, resourceGroup, cancellationToken);
        return new[] { ResourceNaming.TimetrackerApp(appName), ResourceNaming.DbApp(appName), ResourceNaming.RedisApp(appName) }
            .Where(name => apps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase) && ManagedTags.IsManaged(a.Tags, appName)))
            .ToList();
    }
}
