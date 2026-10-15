using System.Text.RegularExpressions;

namespace Timetracker.Domain.Deployment;

/// <summary>
/// Validates Azure Container Apps naming rules.
/// Rules:
/// - Allowed characters: lowercase letters (a-z), digits (0-9), and hyphen (-)
/// - Must start with a lowercase letter
/// - Must end with a lowercase letter or digit
/// - Cannot contain "--" (double hyphen)
/// - Length must be between 2 and 26 characters (inclusive)
///   Container App 名の上限は 32 文字だが、本ツールはアプリ名に接尾辞（最長 "-redis" の 6 文字）を
///   付けてリソースを作成するため、アプリ名自体は 32 - 6 = 26 文字までとする。
/// </summary>
public static partial class AppNameValidator
{
    /// <summary>Azure Container Apps のリソース名の最大長。</summary>
    public const int ContainerAppNameMaxLength = 32;

    /// <summary>アプリ名に付けて作成するリソース名の接尾辞。</summary>
    public static readonly IReadOnlyList<string> ResourceSuffixes = ResourceNaming.Suffixes;

    public const int MinLength = 2;
    public static readonly int MaxLength = ContainerAppNameMaxLength - ResourceSuffixes.Max(s => s.Length);

    // Pattern: ^[a-z]([a-z0-9-]*[a-z0-9])?$ (length is checked separately)
    // - For single char: matches [a-z] with optional group absent
    // - For 2+ chars: matches [a-z] followed by optional middle chars and final [a-z0-9]
    private static readonly Regex ValidPattern = CreateValidPattern();

    /// <summary>使える文字と先頭・末尾の規則（長さと連続するハイフンは別に確認する）。</summary>
    public const string Pattern = @"^[a-z]([a-z0-9-]*[a-z0-9])?$";

    [GeneratedRegex(Pattern, RegexOptions.Compiled)]
    private static partial Regex CreateValidPattern();

    /// <summary>
    /// Checks if the given app name is valid according to Azure Container Apps naming rules.
    /// </summary>
    /// <param name="name">The app name to validate (should already be lowercased).</param>
    /// <returns>True if the name is valid; otherwise, false.</returns>
    public static bool IsValid(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        if (name.Length < MinLength || name.Length > MaxLength)
            return false;

        if (name.Contains("--"))
            return false;

        return ValidPattern.IsMatch(name);
    }

    /// <summary>
    /// Returns an error message describing why the name is invalid, or null if valid.
    /// </summary>
    /// <param name="name">The app name to validate.</param>
    /// <returns>An error message if invalid; null if valid.</returns>
    public static string? GetError(string name)
    {
        if (string.IsNullOrEmpty(name))
            return $"アプリ名が空です。アプリ名は{MinLength}〜{MaxLength}文字の英小文字・数字・ハイフンで指定してください。";

        if (name.Length < MinLength)
            return $"アプリ名 '{name}' は短すぎます。最低{MinLength}文字必要です。";

        if (name.Length > MaxLength)
            return $"アプリ名 '{name}' は長すぎます。最大{MaxLength}文字まで指定可能です（リソース名に接尾辞 \"-redis\" 等が付くため）。";

        if (name.Contains("--"))
            return $"アプリ名 '{name}' に連続するハイフン ('--') が含まれています。連続するハイフンは使用できません。";

        if (!char.IsAsciiLetterLower(name[0]))
            return $"アプリ名 '{name}' は英小文字で始まる必要があります。";

        if (!char.IsAsciiLetterLower(name[^1]) && !char.IsAsciiDigit(name[^1]))
            return $"アプリ名 '{name}' は英小文字または数字で終わる必要があります。";

        if (!ValidPattern.IsMatch(name))
            return $"アプリ名 '{name}' に無効な文字が含まれています。使用できる文字は英小文字(a-z)、数字(0-9)、ハイフン(-)のみです。";

        return null;
    }
}
