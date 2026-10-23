using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Application.Inventory;

/// <summary>利用可能期限の記録の状態。</summary>
public enum ExpiryKind
{
    /// <summary>期限のタグが無い・読めない（この機能より前にデプロイした、または Container App が無い）。</summary>
    Missing,

    /// <summary>期限なし。</summary>
    None,

    /// <summary>期限がある（<see cref="DeploymentEntry.ExpiresAt"/>）。</summary>
    Date,
}

/// <summary>一覧の 1 行の Container App（Resource Graph の結果）。</summary>
public sealed record AppEntry(string Name, string? ProvisioningState, string? RunningStatus, string? Image, double? Cpu, string? Memory);

/// <summary>
/// デプロイの一覧の 1 行（サブスクリプション・リソースグループ・アプリ名の組。docs/design/desktop-jobs.md の 6.3・6.4）。
/// 無いリソースは null（作りかけ・消しかけ）。<see cref="ExpiryDiffers"/> は、リソースごとの期限のタグが食い違っているか。
/// </summary>
public sealed record DeploymentEntry(
    string SubscriptionId,
    string ResourceGroup,
    string AppName,
    string? Location,
    string? Url,
    DateTimeOffset? DeployedAt,
    ExpiryKind ExpiryKind,
    DateTimeOffset? ExpiresAt,
    AppEntry? Timetracker,
    AppEntry? Db,
    AppEntry? Redis,
    string? EnvironmentName,
    string? StorageAccountName,
    bool DbPersisted,
    bool ExpiryDiffers = false)
{
    /// <summary>DB 種別（DB アプリのイメージから。分からなければ null）。</summary>
    public string? DbType => Db?.Image switch
    {
        { } image when image.Contains("mssql", StringComparison.OrdinalIgnoreCase) => "sqlserver",
        { } image when image.Contains("postgres", StringComparison.OrdinalIgnoreCase) => "postgresql",
        _ => null,
    };

    /// <summary>3 つの Container App と Environment がそろっているか。</summary>
    public bool IsComplete => Missing.Count == 0;

    /// <summary>無いもの（作りかけ・消しかけの環境）。</summary>
    public IReadOnlyList<string> Missing =>
    [
        .. EnvironmentName == null ? ["Container Apps Environment"] : Array.Empty<string>(),
        .. Timetracker == null ? ["Timetracker"] : Array.Empty<string>(),
        .. Db == null ? ["DB"] : Array.Empty<string>(),
        .. Redis == null ? ["Redis"] : Array.Empty<string>(),
    ];

    /// <summary>同じ対象か（大文字・小文字を区別しない）。</summary>
    public bool IsFor(string subscriptionId, string resourceGroup, string appName) =>
        string.Equals(SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ResourceGroup, resourceGroup, StringComparison.OrdinalIgnoreCase)
        && string.Equals(AppName, appName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Resource Graph で読んだリソースを、デプロイごとにまとめる。</summary>
public static class DeploymentCatalog
{
    /// <summary>
    /// サブスクリプション・リソースグループ・アプリ名（タグ <see cref="ManagedTags.AppNameKey"/>）の組ごとにまとめる。アプリ名のタグが無いものは出さない。
    /// 並びはサブスクリプション・リソースグループ・アプリ名の順。
    /// </summary>
    public static IReadOnlyList<DeploymentEntry> Build(IEnumerable<ManagedResourceRecord> records) =>
        records
            .Select(r => (Record: r, AppName: r.Tags.TryGetValue(ManagedTags.AppNameKey, out var name) ? name.ToLowerInvariant() : null))
            .Where(x => x.AppName != null)
            .GroupBy(x => (Sub: x.Record.SubscriptionId.ToLowerInvariant(), Rg: x.Record.ResourceGroup.ToLowerInvariant(), App: x.AppName!))
            .Select(g => ToEntry(g.Key.App, g.Select(x => x.Record).ToList()))
            .OrderBy(e => e.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ResourceGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.AppName, StringComparer.Ordinal)
            .ToList();

    private static DeploymentEntry ToEntry(string appName, IReadOnlyList<ManagedResourceRecord> records)
    {
        ManagedResourceRecord? Find(string type, string? name = null) => records.FirstOrDefault(r =>
            r.Type == type && (name == null || string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));
        var tt = Find(ManagedResourceRecord.ContainerAppType, ResourceNaming.TimetrackerApp(appName));
        var db = Find(ManagedResourceRecord.ContainerAppType, ResourceNaming.DbApp(appName));
        var redis = Find(ManagedResourceRecord.ContainerAppType, ResourceNaming.RedisApp(appName));
        var env = Find(ManagedResourceRecord.EnvironmentType, ResourceNaming.Environment(appName));
        var storage = Find(ManagedResourceRecord.StorageAccountType);
        var first = records[0];
        // 期限とデプロイ日は、本ツールで作るリソースのタグ（期限は、期限なしが 1 つでもあれば期限なし、ほかは最も遅いもの。デプロイ日は最も新しいもの）
        var resources = new[] { tt, db, redis, env, storage }.OfType<ManagedResourceRecord>().ToList();
        var expiresTags = resources.Select(r => r.Tags.GetValueOrDefault(DeploymentExpiry.ExpiresAtKey)).ToList();
        var expiresTag = DeploymentExpiry.Combine(expiresTags);
        var deployedTag = resources.Select(r => DeploymentExpiry.TryParseTag(r.Tags.GetValueOrDefault(DeploymentExpiry.DeployedAtKey), out var at) ? at : (DateTimeOffset?)null)
            .Max();
        // Combine は、期限なし（none）・読める期限・null（読めるものが無い）のどれかを返す
        var kind = expiresTag == null ? ExpiryKind.Missing : expiresTag == DeploymentExpiry.None ? ExpiryKind.None : ExpiryKind.Date;
        DateTimeOffset? expiresAt = DeploymentExpiry.TryParseTag(expiresTag, out var at) ? at : null;
        return new DeploymentEntry(
            first.SubscriptionId, first.ResourceGroup, appName,
            (tt ?? env ?? first).Location is { Length: > 0 } location ? location : null,
            tt?.App?.Fqdn is { } fqdn ? $"https://{fqdn}" : null,
            deployedTag,
            kind, expiresAt,
            App(tt), App(db), App(redis),
            env?.Name, storage?.Name,
            db?.App?.UsesDbStorage == true,
            DeploymentExpiry.Differ(expiresTags));
    }

    private static AppEntry? App(ManagedResourceRecord? record) => record == null
        ? null
        : new AppEntry(record.Name, record.App?.ProvisioningState, record.App?.RunningStatus, record.App?.Image, record.App?.Cpu, record.App?.Memory);
}
