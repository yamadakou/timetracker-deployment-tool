using Xunit;

namespace Timetracker.SystemTests.E2E;

/// <summary>
/// ブラウザーでのセットアップ・ログインのテスト（E2E）の設定。TT_E2E_LICENSE_KEY（Timetracker のライセンスキー）と、
/// サンプルデータベースに入っているユーザー（TT_E2E_SAMPLE_LOGIN / TT_E2E_SAMPLE_PASSWORD）が無ければスキップする。
/// これらの値はテストの出力に出さない。手順は docs/SYSTEM_TESTS.md を参照。
/// </summary>
public static class E2ESettings
{
    public static string? LicenseKey => Get("TT_E2E_LICENSE_KEY");

    /// <summary>ローカルの docker compose のテストで確かめる DB 種別（省略時はすべて）。</summary>
    public static string? DbType => Get("TT_E2E_DB_TYPE");

    /// <summary>使う Chromium の実行ファイル（省略時は playwright install で入れたもの）。</summary>
    public static string? ChromiumPath => Get("TT_E2E_CHROMIUM_PATH");

    /// <summary>
    /// サンプルデータベースに入っている、ログインに使うユーザー。パスワードが空のユーザーは、TT_E2E_SAMPLE_PASSWORD に "" と書く
    /// （GitHub のシークレットは空にできず、未登録のシークレットは空の文字列として渡されるため、空の値は未設定とみなす）。
    /// </summary>
    public static LoginUser? SampleUser =>
        Get("TT_E2E_SAMPLE_LOGIN") is { } login && Get("TT_E2E_SAMPLE_PASSWORD") is { } password
            ? new LoginUser(login, password == EmptyPassword ? "" : password)
            : null;

    /// <summary>空のパスワードを表す値。</summary>
    public const string EmptyPassword = "\"\"";

    /// <summary>設定がそろっていればライセンスキーとユーザーを返す（そろっていなければ null）。</summary>
    public static (string LicenseKey, LoginUser User)? Configured =>
        LicenseKey is { } key && SampleUser is { } user ? (key, user) : null;

    public static (string LicenseKey, LoginUser User) Require()
    {
        Assert.SkipWhen(Configured == null,
            "TT_E2E_LICENSE_KEY / TT_E2E_SAMPLE_LOGIN / TT_E2E_SAMPLE_PASSWORD が未設定のためスキップします（docs/SYSTEM_TESTS.md を参照）。");
        return Configured!.Value;
    }

    private static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
