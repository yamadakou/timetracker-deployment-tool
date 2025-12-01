using Azure.Core;
using Azure.Identity;
using Timetracker.Common;

namespace Timetracker.Controller.Cli;

/// <summary>
/// Factory class that creates Azure TokenCredential based on the specified AuthMode.
/// </summary>
public static class CredentialFactory
{
    /// <summary>
    /// Creates a TokenCredential based on the specified authentication mode.
    /// </summary>
    /// <param name="authMode">The authentication mode: default, azure-cli, sp-env, device-code, or managed-identity</param>
    /// <param name="logger">Logger for verbose output and error messages</param>
    /// <returns>A TokenCredential instance appropriate for the specified mode</returns>
    public static TokenCredential Create(string authMode, SimpleLogger logger)
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

    private static TokenCredential CreateDefaultCredential(SimpleLogger logger)
    {
        logger.Verbose("DefaultAzureCredential を使用します");
        return new DefaultAzureCredential();
    }

    private static TokenCredential CreateAzureCliCredential(SimpleLogger logger)
    {
        logger.Verbose("AzureCliCredential を使用します（az login が必要）");
        return new AzureCliCredential();
    }

    private static TokenCredential CreateServicePrincipalCredential(SimpleLogger logger)
    {
        var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            logger.Error("sp-env モードで必要な環境変数（AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET）が不足しています。DefaultAzureCredential にフォールバックします。");
            return new DefaultAzureCredential();
        }

        logger.Verbose("ClientSecretCredential を使用します（Service Principal 環境変数より）");
        return new ClientSecretCredential(tenantId, clientId, clientSecret);
    }

    private static TokenCredential CreateDeviceCodeCredential(SimpleLogger logger)
    {
        logger.Verbose("DeviceCodeCredential を使用します（デバイスコード認証）");
        return new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            DeviceCodeCallback = (code, cancellation) =>
            {
                logger.Info($"デバイスコード認証: {code.Message}");
                return Task.CompletedTask;
            }
        });
    }

    private static TokenCredential CreateManagedIdentityCredential(SimpleLogger logger)
    {
        logger.Verbose("ManagedIdentityCredential を使用します");
        return new ManagedIdentityCredential();
    }
}
