using Xunit;

namespace Timetracker.SystemTests;

/// <summary>
/// システムテストの設定（環境変数）。TT_SYSTEMTEST_SUBSCRIPTION_ID が無ければテストはスキップする
/// （通常の dotnet test / CI では Azure に接続しない）。手順は docs/SYSTEM_TESTS.md を参照。
/// </summary>
public static class SystemTestSettings
{
    public static string? SubscriptionId => Get("TT_SYSTEMTEST_SUBSCRIPTION_ID");
    public static string Location => Get("TT_SYSTEMTEST_LOCATION") ?? "japaneast";

    // 権限の少ないサービスプリンシパルのシナリオ用（Reader と Container Apps Contributor を LimitedResourceGroup にだけ割り当てたもの）
    public static string LimitedResourceGroup => Get("TT_SYSTEMTEST_LIMITED_RESOURCE_GROUP") ?? "rg-ttst-limited";
    public static string? LimitedTenantId => Get("TT_SYSTEMTEST_LIMITED_TENANT_ID");
    public static string? LimitedClientId => Get("TT_SYSTEMTEST_LIMITED_CLIENT_ID");
    public static string? LimitedClientSecret => Get("TT_SYSTEMTEST_LIMITED_CLIENT_SECRET");

    /// <summary>Azure に接続するテストの前提。設定が無ければスキップする。</summary>
    public static string RequireSubscription()
    {
        Assert.SkipWhen(SubscriptionId == null,
            "TT_SYSTEMTEST_SUBSCRIPTION_ID が未設定のためスキップします（docs/SYSTEM_TESTS.md を参照）。");
        return SubscriptionId!;
    }

    public static (string TenantId, string ClientId, string ClientSecret) RequireLimitedServicePrincipal()
    {
        Assert.SkipWhen(LimitedTenantId == null || LimitedClientId == null || LimitedClientSecret == null,
            "TT_SYSTEMTEST_LIMITED_TENANT_ID / _CLIENT_ID / _CLIENT_SECRET が未設定のためスキップします（docs/SYSTEM_TESTS.md を参照）。");
        return (LimitedTenantId!, LimitedClientId!, LimitedClientSecret!);
    }

    private static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
