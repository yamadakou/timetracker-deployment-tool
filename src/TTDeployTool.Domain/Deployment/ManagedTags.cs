namespace TTDeployTool.Domain.Deployment;

/// <summary>
/// 本ツールが作成したリソースに付けるタグ。削除時は、名前が一致しかつこのタグが付いているリソースだけを対象にする
/// （同じ名前の別のリソースを誤って削除しないため）。
/// 本ツールの名前を TTDeployTool に改めた後も、以前の版で付けた値（<see cref="LegacyManagedByValue"/>）のリソースは本ツールのものとして扱う
/// （更新したときに新しい値に置き換わる）。
/// </summary>
public static class ManagedTags
{
    public const string ManagedByKey = "managed-by";
    public const string ManagedByValue = "ttdeploytool";

    /// <summary>本ツールの以前の名前（timetracker-deployment-tool）の版で付けた managed-by の値。</summary>
    public const string LegacyManagedByValue = "timetracker-deployment-tool";
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
        && TryGet(tags, ManagedByKey, out var managedBy) && IsOurValue(managedBy)
        && TryGet(tags, AppNameKey, out var taggedApp) && string.Equals(taggedApp, appName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 別のアプリ名、または本ツール以外の管理タグ（同じキーで値が異なるもの）が付いているか。
    /// 他の利用者やツールが管理しているリソースとみなし、--include-untagged でも削除の対象にしない。
    /// </summary>
    public static bool HasOtherOwner(IReadOnlyDictionary<string, string>? tags, string appName) =>
        tags != null
        && ((TryGet(tags, ManagedByKey, out var managedBy) && !IsOurValue(managedBy))
            || (TryGet(tags, AppNameKey, out var taggedApp) && !string.Equals(taggedApp, appName, StringComparison.OrdinalIgnoreCase)));

    // 値は大文字小文字を区別せずに比べる
    private static bool IsOurValue(string? managedBy) =>
        string.Equals(managedBy, ManagedByValue, StringComparison.OrdinalIgnoreCase)
        || string.Equals(managedBy, LegacyManagedByValue, StringComparison.OrdinalIgnoreCase);

    // Azure のタグ名は大文字小文字を区別しない
    private static bool TryGet(IReadOnlyDictionary<string, string> tags, string key, out string? value)
    {
        value = tags.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        return value != null;
    }
}
