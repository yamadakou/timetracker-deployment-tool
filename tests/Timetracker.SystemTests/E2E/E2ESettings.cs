using Xunit;

namespace Timetracker.SystemTests.E2E;

/// <summary>
/// ブラウザーでのセットアップ・ログインのテスト（E2E）の設定。TT_E2E_LICENSE_KEY（Timetracker のライセンスキー）が無ければスキップする。
/// キーはテストの出力に出さない。手順は docs/SYSTEM_TESTS.md を参照。
/// </summary>
public static class E2ESettings
{
    public static string? LicenseKey => Get("TT_E2E_LICENSE_KEY");

    /// <summary>使う Chromium の実行ファイル（省略時は playwright install で入れたもの）。</summary>
    public static string? ChromiumPath => Get("TT_E2E_CHROMIUM_PATH");

    public static string RequireLicenseKey()
    {
        Assert.SkipWhen(LicenseKey == null, "TT_E2E_LICENSE_KEY が未設定のためスキップします（docs/SYSTEM_TESTS.md を参照）。");
        return LicenseKey!;
    }

    /// <summary>テストごとの管理ユーザー（パスワードは毎回作る）。</summary>
    public static AdminUser NewAdmin() => new("ttadmin", "システムテスト管理者", AzureTestEnvironment.NewPassword());

    private static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
