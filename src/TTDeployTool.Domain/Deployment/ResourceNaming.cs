using System.Security.Cryptography;
using System.Text;

namespace TTDeployTool.Domain.Deployment;

/// <summary>
/// アプリ名から作成するリソース名を組み立てる。リソース名の規則はここに集約する。
/// </summary>
public static class ResourceNaming
{
    public const string EnvironmentSuffix = "-env";
    public const string DbSuffix = "-db";
    public const string RedisSuffix = "-redis";
    public const string TimetrackerSuffix = "-tt";

    /// <summary>ストレージアカウント名の接頭辞。</summary>
    public const string StorageAccountPrefix = "tt";

    /// <summary>ストレージアカウント名に含めるアプリ名の最大文字数（接頭辞 2 ＋ 14 ＋ ハッシュ 8 = 24 文字以内）。</summary>
    public const int StorageAccountAppNameLength = 14;

    /// <summary>ストレージアカウント名の最大長（Azure の規則）。</summary>
    public const int StorageAccountNameMaxLength = 24;

    /// <summary>アプリ名に付けて作成するリソース名の接尾辞。</summary>
    public static readonly IReadOnlyList<string> Suffixes = new[] { EnvironmentSuffix, DbSuffix, RedisSuffix, TimetrackerSuffix };

    public static string Environment(string appName) => appName + EnvironmentSuffix;
    public static string DbApp(string appName) => appName + DbSuffix;
    public static string RedisApp(string appName) => appName + RedisSuffix;
    public static string TimetrackerApp(string appName) => appName + TimetrackerSuffix;

    /// <summary>DB のデータを置くファイル共有の名前（ストレージアカウントの中。アプリ名の規則のため、共有名の規則も満たす）。</summary>
    public static string DbFileShare(string appName) => appName + DbSuffix;

    /// <summary>
    /// DB のデータを置くストレージアカウントの名前。名前は Azure 全体で一意・英小文字と数字の 3〜24 文字のため、
    /// アプリ名の英数字に、サブスクリプション・リソースグループ・アプリ名から作るハッシュを付ける（同じ入力からは常に同じ名前）。
    /// </summary>
    public static string StorageAccount(string subscriptionId, string rgName, string appName)
    {
        var alphanumeric = new string(appName.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        var key = $"{subscriptionId}/{rgName}/{appName}".ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        return StorageAccountPrefix + alphanumeric[..Math.Min(alphanumeric.Length, StorageAccountAppNameLength)] + hash;
    }
}
