using Azure;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Timetracker.Domain.Deployment;

namespace Timetracker.Controller.Mcp.Tools;

/// <summary>複数のツールで使う処理（Azure のエラーの扱い、パスワードのマスク、結果の組み立て）。</summary>
internal static class ToolSupport
{
    public const string MaskedPassword = "********";

    // 認証に失敗したときの対処（認証モードごと）
    private static readonly IReadOnlyDictionary<string, string> AuthHints = new Dictionary<string, string>
    {
        ["azure-cli"] = "az login でサインインしてから、もう一度試してください。",
        ["default"] = "az login などでサインインしてから、もう一度試してください（DefaultAzureCredential が使える資格情報が見つかりませんでした）。",
        ["sp-env"] = "サーバーの環境変数 AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET を確認してください。",
        ["device-code"] = "サーバーのログ（stderr）に表示されるコードで、ブラウザーからサインインしてください。",
        ["managed-identity"] = "マネージド ID が使える Azure 上の環境で実行しているか（ユーザー割り当ての場合は環境変数 AZURE_CLIENT_ID）を確認してください。",
    };

    /// <summary>
    /// Azure を呼ぶ処理を実行し、よくある失敗（認証・Azure のエラー・資格情報を作れない）を、対処を添えたツールのエラーにする。
    /// </summary>
    public static async Task<T> CallAzureAsync<T>(McpServerSettings settings, ILogger logger, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (AuthenticationFailedException ex)
        {
            logger.LogWarning(ex, "Azure の認証に失敗しました");
            throw new McpException($"Azure の認証に失敗しました（認証モード: {settings.AzureAuthMode}）。{AuthHints[settings.AzureAuthMode]}");
        }
        catch (RequestFailedException ex)
        {
            throw new McpException($"Azure がエラーを返しました（{ex.Status} {ex.ErrorCode}）: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // 資格情報を作れない場合（sp-env で環境変数が足りないなど）。メッセージに秘密情報は含まれない
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    /// 既存の環境を読む・削除するツールの対象（サブスクリプション・リソースグループ・アプリ名）を決める。
    /// サーバーの既定値も無い場合、アプリ名が無効な場合、操作してよい範囲に無い場合は、Azure を読まずにツールのエラーにする。
    /// </summary>
    public static DeployOptions ResolveTarget(
        McpServerSettings settings, IAzureAccessPolicy policy, string? subscription, string? resourceGroup, string? appName)
    {
        var o = new DeploymentParameters { Subscription = subscription, ResourceGroup = resourceGroup, AppName = appName }.Resolve(settings).Options;
        var missing = new[] { (o.Subscription, "subscription"), (o.ResourceGroup, "resource_group") }.Where(p => p.Item1.Length == 0).Select(p => p.Item2).ToList();
        if (missing.Count > 0)
            throw new McpException($"{string.Join(" と ", missing)} を指定してください（サーバーの既定値も設定されていません）。");
        if (AppNameValidator.GetError(o.AppName) is { } appNameError)
            throw new McpException(appNameError);
        if (policy.ResourceGroupBlocker(o.Subscription, o.ResourceGroup) is { } blocker)
            throw new McpException(blocker);
        return o;
    }

    /// <summary>
    /// パスワードが無い場合に、値を会話に出さずに済む方法（サーバーの環境変数）を案内する。
    /// HTTP では環境変数を読まない（全利用者で共有になるため）ので、引数での指定を求める。
    /// </summary>
    public static IEnumerable<string> PasswordHints(ResolvedParameters resolved, McpServerSettings settings)
    {
        if (settings.Http != null)
        {
            if (resolved.DbPasswordSource == PasswordSource.None)
                yield return "db_password が未設定です。引数で指定してください（HTTP のサーバーでは、パスワードの環境変数は使えません）。";
            if (resolved.TrackerPasswordSource == PasswordSource.None)
                yield return "tracker_password が未設定です。引数で指定してください（HTTP のサーバーでは、パスワードの環境変数は使えません）。";
            yield break;
        }
        if (resolved.DbPasswordSource == PasswordSource.None)
            yield return "db_password が未設定です。サーバーの環境変数 TT_MCP_DB_PASSWORD で設定するか（推奨。値が会話に残らない）、引数で指定してください。";
        if (resolved.TrackerPasswordSource == PasswordSource.None)
            yield return "tracker_password が未設定です。サーバーの環境変数 TT_MCP_TRACKER_PASSWORD で設定するか（推奨。値が会話に残らない）、引数で指定してください。";
    }

    public static ResolvedSettings ToResolvedSettings(ResolvedParameters resolved)
    {
        var o = resolved.Options;
        return new ResolvedSettings(
            o.Subscription, o.ResourceGroup, o.Location, o.AppName, o.DbType, o.DbName, o.TimetrackerTag,
            Mask(resolved.DbPasswordSource), Mask(resolved.TrackerPasswordSource),
            o.TimetrackerCpu, o.TimetrackerMemoryGi, o.DbCpu, o.DbMemoryGi, o.RedisCpu, o.RedisMemoryGi);
    }

    public static ResourceNames ToResourceNames(string appName) => new(
        ResourceNaming.Environment(appName), ResourceNaming.TimetrackerApp(appName), ResourceNaming.DbApp(appName), ResourceNaming.RedisApp(appName));

    private static string Mask(PasswordSource source) => source switch
    {
        PasswordSource.Argument => $"{MaskedPassword}（引数）",
        PasswordSource.Environment => $"{MaskedPassword}（環境変数）",
        _ => "（未設定）",
    };
}
