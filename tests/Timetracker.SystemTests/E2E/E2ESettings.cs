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

    /// <summary>使う Chromium の実行ファイル（省略時は playwright install で入れたもの）。</summary>
    public static string? ChromiumPath => Get("TT_E2E_CHROMIUM_PATH");

    /// <summary>サンプルデータベースに入っている、ログインに使うユーザー。</summary>
    public static LoginUser? SampleUser =>
        Get("TT_E2E_SAMPLE_LOGIN") is { } login && Get("TT_E2E_SAMPLE_PASSWORD") is { } password ? new LoginUser(login, password) : null;

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
