using System.Globalization;

namespace TTDeployTool.Domain.Deployment;

/// <summary>利用可能期限の決まり方。</summary>
public enum ExpirySource
{
    /// <summary>日付または「期限なし」を指定した。</summary>
    Specified,

    /// <summary>指定せず、今の期限（タグ）を引き継いだ。</summary>
    Inherited,

    /// <summary>指定せず、今の期限も無いため、既定（今日から <see cref="DeploymentExpiry.DefaultDays"/> 日後）にした。</summary>
    Default,
}

/// <summary>決めた利用可能期限。<see cref="ExpiresAt"/> が null なら期限なし。</summary>
public sealed record ExpiryDecision(DateTimeOffset? ExpiresAt, ExpirySource Source)
{
    /// <summary>タグ <see cref="DeploymentExpiry.ExpiresAtKey"/> の値（UTC の ISO 8601。期限なしは none）。</summary>
    public string TagValue => ExpiresAt is { } at ? DeploymentExpiry.FormatTag(at) : DeploymentExpiry.None;

    /// <summary>画面・ログに出す説明（期限日は、指定した時刻帯の日付）。</summary>
    public string Describe(TimeZoneInfo timeZone)
    {
        if (ExpiresAt is not { } at)
            return Source == ExpirySource.Inherited ? "期限なし（今の設定を引き継ぎます）" : "期限なし";
        var date = TimeZoneInfo.ConvertTime(at, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Source switch
        {
            ExpirySource.Inherited => $"{date}（今の期限を引き継ぎます）",
            ExpirySource.Default => $"{date}（既定: 今日から {DeploymentExpiry.DefaultDays} 日後）",
            _ => date,
        };
    }
}

/// <summary>
/// デプロイの利用可能期限（docs/design/desktop-jobs.md の 6.1。D9・D11・D12・D17）。
/// 入力は「省略（既定）」「yyyy-MM-dd」「none（期限なし）」。期限は期限日の終わり（その日の 23:59:59、実行する PC・サーバーの時刻帯）で、
/// タグには UTC で記録する。省略した場合は、今の期限（タグ）を引き継ぎ、無ければ今日から 32 日後にする。
/// </summary>
public static class DeploymentExpiry
{
    /// <summary>利用可能期限のタグ。</summary>
    public const string ExpiresAtKey = "tt-expires-at";

    /// <summary>最後にデプロイした時刻のタグ。</summary>
    public const string DeployedAtKey = "tt-deployed-at";

    /// <summary>期限なしの値（入力とタグで同じ）。</summary>
    public const string None = "none";

    /// <summary>既定の期限（今日から何日後か）。</summary>
    public const int DefaultDays = 32;

    private const string DateFormat = "yyyy-MM-dd";
    private const string TagFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>入力の形の誤り（日付・none 以外）。誤りが無ければ null。</summary>
    public static string? FormatError(string? input) =>
        string.IsNullOrWhiteSpace(input) || IsNone(input) || TryParseDate(input, out _)
            ? null
            : $"expires-on には yyyy-MM-dd の日付か none を指定してください: {input}";

    /// <summary>指定した日付が今日（指定した時刻帯）より前なら、その誤り。誤りが無ければ null。</summary>
    public static string? PastDateError(string? input, DateTimeOffset now, TimeZoneInfo timeZone) =>
        TryParseDate(input, out var date) && date < Today(now, timeZone)
            ? $"利用可能期限日（{date.ToString(DateFormat, CultureInfo.InvariantCulture)}）は、今日以降の日付を指定してください。"
            : null;

    /// <summary>
    /// 期限を決める。<paramref name="existingTags"/> は、今のデプロイ（Container App）のタグ（新しくデプロイする場合は null）。
    /// </summary>
    public static ExpiryDecision Resolve(string? input, IReadOnlyDictionary<string, string>? existingTags, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        if (IsNone(input))
            return new ExpiryDecision(null, ExpirySource.Specified);
        if (TryParseDate(input, out var date))
            return new ExpiryDecision(EndOfDay(date, timeZone), ExpirySource.Specified);
        if (existingTags?.FirstOrDefault(t => string.Equals(t.Key, ExpiresAtKey, StringComparison.OrdinalIgnoreCase)).Value is { } current)
        {
            if (IsNone(current))
                return new ExpiryDecision(null, ExpirySource.Inherited);
            if (TryParseTag(current, out var at))
                return new ExpiryDecision(at, ExpirySource.Inherited);
        }
        // 今の期限が無い（この機能より前にデプロイした）・読めない場合も、既定にする
        return new ExpiryDecision(DefaultExpiry(now, timeZone), ExpirySource.Default);
    }

    /// <summary>新しくデプロイした場合の既定の期限（今日から <see cref="DefaultDays"/> 日後の終わり）。</summary>
    public static DateTimeOffset DefaultExpiry(DateTimeOffset now, TimeZoneInfo timeZone) => EndOfDay(Today(now, timeZone).AddDays(DefaultDays), timeZone);

    /// <summary>期限日の終わり（その日の 23:59:59、指定した時刻帯）。</summary>
    public static DateTimeOffset EndOfDay(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    /// <summary>デプロイした時刻・期限のタグの値（UTC の ISO 8601）。</summary>
    public static string FormatTag(DateTimeOffset value) => value.UtcDateTime.ToString(TagFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 複数のリソースの <see cref="ExpiresAtKey"/> の値をまとめる（オーナーの判断、2026-10-23）。<see cref="None"/> が 1 つでもあれば期限なし、
    /// ほかは最も遅い期限（誤って早く削除しないため）。読める値が無ければ null。
    /// </summary>
    public static string? Combine(IEnumerable<string?> values)
    {
        var list = values.OfType<string>().ToList();
        if (list.Any(IsNone))
            return None;
        var dates = list.Select(v => TryParseTag(v, out var at) ? at : (DateTimeOffset?)null).OfType<DateTimeOffset>().ToList();
        return dates.Count == 0 ? null : FormatTag(dates.Max());
    }

    /// <summary>リソースごとの <see cref="ExpiresAtKey"/> の値が食い違っているか（期限の変更の途中の失敗、ポータルでの書き換えなど）。</summary>
    public static bool Differ(IEnumerable<string?> values) =>
        values.OfType<string>().Select(v => IsNone(v) ? None : v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    /// <summary>タグの時刻（<see cref="FormatTag"/> の形式）を読む。読めなければ false。</summary>
    public static bool TryParseTag(string? value, out DateTimeOffset at) =>
        DateTimeOffset.TryParseExact(value, TagFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at);

    /// <summary>今日（指定した時刻帯）。</summary>
    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo timeZone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);

    private static bool IsNone(string? value) => string.Equals(value?.Trim(), None, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseDate(string? input, out DateOnly date) =>
        DateOnly.TryParseExact(input?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
