namespace Timetracker.Domain.Deployment;

/// <summary>
/// 本ツールが作成したリソースに付けるタグ。削除時は、名前が一致しかつこのタグが付いているリソースだけを対象にする
/// （同じ名前の別のリソースを誤って削除しないため）。
/// </summary>
public static class ManagedTags
{
    public const string ManagedByKey = "managed-by";
    public const string ManagedByValue = "timetracker-deployment-tool";
    public const string AppNameKey = "tt-app-name";

    /// <summary>アプリ名に対応する管理タグ。</summary>
    public static IReadOnlyDictionary<string, string> For(string appName) => new Dictionary<string, string>
    {
        [ManagedByKey] = ManagedByValue,
        [AppNameKey] = appName,
    };

    /// <summary>既存のタグに管理タグを上書き追加する（利用者が付けた他のタグは残す）。</summary>
    public static IReadOnlyDictionary<string, string> Merge(IEnumerable<KeyValuePair<string, string>>? existing, string appName)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in existing ?? [])
        {
            merged[key] = value;
        }
        foreach (var (key, value) in For(appName))
        {
            merged[key] = value;
        }
        return merged;
    }

    /// <summary>指定したアプリ名の一式として本ツールが管理しているリソースか。</summary>
    public static bool IsManaged(IReadOnlyDictionary<string, string>? tags, string appName) =>
        tags != null
        && TryGet(tags, ManagedByKey, out var managedBy) && string.Equals(managedBy, ManagedByValue, StringComparison.OrdinalIgnoreCase)
        && TryGet(tags, AppNameKey, out var taggedApp) && string.Equals(taggedApp, appName, StringComparison.OrdinalIgnoreCase);

    // Azure のタグ名は大文字小文字を区別しない
    private static bool TryGet(IReadOnlyDictionary<string, string> tags, string key, out string? value)
    {
        value = tags.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        return value != null;
    }
}
