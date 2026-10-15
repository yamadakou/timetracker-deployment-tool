using System.Text.RegularExpressions;

namespace Timetracker.Domain.Azure;

/// <summary>
/// Azure RBAC の評価規則に従い、権限セットの一覧で操作（Action）が許可されているかを判定する。
/// いずれかの権限セットで actions に一致し、かつ同じセットの notActions に一致しなければ許可。
/// ワイルドカード "*" は任意の文字列に一致し、大文字小文字は区別しない。拒否割り当ては考慮しない。
/// 事前確認に使うため、判定に迷う場合は許可側に倒す（<see cref="Matches"/>）。
/// </summary>
public static class PermissionEvaluator
{
    public static bool IsAllowed(IEnumerable<PermissionSet> permissions, string action) =>
        permissions.Any(p => p.Actions.Any(a => Matches(a, action)) && !p.NotActions.Any(n => Matches(n, action)));

    /// <summary>許可されていない操作を、指定した順に返す。</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<PermissionSet> permissions, IEnumerable<string> actions)
    {
        var list = permissions.ToList();
        return actions.Where(a => !IsAllowed(list, a)).ToList();
    }

    // "/*/" は 0 階層にも一致させる（例: containerApps/*/write は containerApps/write にも一致）。
    // Azure の判定と厳密に同じかを確認できていないため、許可側に倒す（事前確認が誤って処理を止めないように。
    // 実際に権限が無ければ Azure 側でエラーになる）
    internal static bool Matches(string pattern, string action)
    {
        var regex = Regex.Escape(pattern).Replace(@"/\*/", "/(.*/)?").Replace(@"\*", ".*");
        return Regex.IsMatch(action, "^" + regex + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
