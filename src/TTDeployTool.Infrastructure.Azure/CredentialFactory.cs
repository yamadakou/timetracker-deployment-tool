using Azure.Core;
using Azure.Identity;
using TTDeployTool.Common;

namespace TTDeployTool.Infrastructure.Azure;

/// <summary>
/// Factory class that creates Azure TokenCredential based on the specified AuthMode.
/// </summary>
public static class CredentialFactory
{
    /// <summary>指定できる認証モード。</summary>
    public static readonly IReadOnlyList<string> AuthModes = ["default", "azure-cli", "sp-env", "device-code", "managed-identity"];

    /// <summary>
    /// Creates a TokenCredential based on the specified authentication mode.
    /// </summary>
    /// <param name="authMode">The authentication mode: default, azure-cli, sp-env, device-code, or managed-identity</param>
    /// <param name="logger">Logger for verbose output and error messages</param>
    /// <returns>A TokenCredential instance appropriate for the specified mode</returns>
    public static TokenCredential Create(string authMode, IDeploymentLogger logger)
    {
        logger.Verbose($"認証モード: {authMode}");

        return authMode.ToLowerInvariant() switch
        {
            "default" => CreateDefaultCredential(logger),
            "azure-cli" => CreateAzureCliCredential(logger),
            "sp-env" => CreateServicePrincipalCredential(logger),
            "device-code" => CreateDeviceCodeCredential(logger),
            "managed-identity" => CreateManagedIdentityCredential(logger),
            _ => throw new ArgumentException($"不明な認証モード: {authMode}。有効な値は default, azure-cli, sp-env, device-code, managed-identity です。")
        };
    }

    private static TokenCredential CreateDefaultCredential(IDeploymentLogger logger)
    {
        logger.Verbose("DefaultAzureCredential を使用します");
        return new DefaultAzureCredential();
    }

    private static TokenCredential CreateAzureCliCredential(IDeploymentLogger logger)
    {
        logger.Verbose("AzureCliCredential を使用します（az login が必要）");
        return new AzureCliCredential();
    }

    private static TokenCredential CreateServicePrincipalCredential(IDeploymentLogger logger)
    {
        var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            // 明示的に sp-env が指定された場合、別の資格情報（例: 開発者の az login）で意図せずデプロイされないよう停止する
            throw new InvalidOperationException(
                "sp-env モードで必要な環境変数（AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET）が不足しています。" +
                "環境変数を設定するか、別の --auth-mode を指定してください。");
        }

        logger.Verbose("ClientSecretCredential を使用します（Service Principal 環境変数より）");
        return new ClientSecretCredential(tenantId, clientId, clientSecret);
    }

    private static TokenCredential CreateDeviceCodeCredential(IDeploymentLogger logger)
    {
        logger.Verbose("DeviceCodeCredential を使用します（デバイスコード認証）");
        return new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            DeviceCodeCallback = DeviceCodeCallback(logger)
        });
    }

    /// <summary>デバイスコード認証の案内（サインイン用の URL とコード）をログに出すコールバック。</summary>
    internal static Func<DeviceCodeInfo, CancellationToken, Task> DeviceCodeCallback(IDeploymentLogger logger) =>
        (code, cancellation) =>
        {
            logger.Info($"デバイスコード認証: {code.Message}");
            return Task.CompletedTask;
        };

    private static TokenCredential CreateManagedIdentityCredential(IDeploymentLogger logger) =>
        CreateManagedIdentityCredential(logger, Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"));

    /// <summary>
    /// 環境変数 AZURE_CLIENT_ID があればユーザー割り当て、無ければシステム割り当てのマネージド ID を使う
    /// （Container Apps などで、ユーザー割り当ての ID を割り当てた場合）。
    /// </summary>
    internal static TokenCredential CreateManagedIdentityCredential(IDeploymentLogger logger, string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.Verbose("ManagedIdentityCredential を使用します（システム割り当て）");
            return new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        }
        logger.Verbose($"ManagedIdentityCredential を使用します（ユーザー割り当て: {clientId}）");
        return new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }

    /// <summary>フェデレーション資格情報（マネージド ID のトークンをクライアントアサーションにする）で要求するスコープ。</summary>
    public const string TokenExchangeScope = "api://AzureADTokenExchange/.default";

    /// <summary>
    /// OBO（On-Behalf-Of）の資格情報: 利用者のアクセストークン（このアプリ向け）を、利用者本人の権限の Azure 用のトークンに交換する
    /// （MCP サーバーの user 方式。詳細設計 Phase 4b）。<paramref name="clientSecret"/> が null なら、アプリ登録の資格情報として
    /// マネージド ID（AZURE_CLIENT_ID があればユーザー割り当て）をフェデレーション資格情報で使う（シークレットを持たずに済む）。
    /// </summary>
    public static TokenCredential CreateOnBehalfOf(
        string tenantId, string clientId, string? clientSecret, string userAssertion, IDeploymentLogger logger)
    {
        if (clientSecret != null)
        {
            logger.Verbose("OnBehalfOfCredential を使用します（クライアントシークレット）");
            return new OnBehalfOfCredential(tenantId, clientId, clientSecret, userAssertion);
        }
        logger.Verbose("OnBehalfOfCredential を使用します（マネージド ID のフェデレーション資格情報）");
        return new OnBehalfOfCredential(
            tenantId, clientId, ClientAssertion(CreateManagedIdentityCredential(logger)), userAssertion, new OnBehalfOfCredentialOptions());
    }

    /// <summary>マネージド ID のトークン（対象 api://AzureADTokenExchange）を、アプリ登録のクライアントアサーションとして返す。</summary>
    internal static Func<CancellationToken, Task<string>> ClientAssertion(TokenCredential managedIdentity) =>
        async cancellationToken => (await managedIdentity.GetTokenAsync(new TokenRequestContext([TokenExchangeScope]), cancellationToken)).Token;
}
