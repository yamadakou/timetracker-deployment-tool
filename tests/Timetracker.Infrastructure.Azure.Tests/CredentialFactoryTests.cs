using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using Moq;
using Timetracker.Common;
using Timetracker.Infrastructure.Azure;
using Xunit;

namespace Timetracker.Infrastructure.Azure.Tests;

public class CredentialFactoryTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    [InlineData("Default")]
    public void Create_Default_Should_Return_DefaultAzureCredential(string authMode)
    {
        var logger = new SimpleLogger(verbose: false);
        var credential = CredentialFactory.Create(authMode, logger);
        credential.Should().BeOfType<DefaultAzureCredential>();
    }

    [Theory]
    [InlineData("azure-cli")]
    [InlineData("AZURE-CLI")]
    [InlineData("Azure-Cli")]
    public void Create_AzureCli_Should_Return_AzureCliCredential(string authMode)
    {
        var logger = new SimpleLogger(verbose: false);
        var credential = CredentialFactory.Create(authMode, logger);
        credential.Should().BeOfType<AzureCliCredential>();
    }

    [Theory]
    [InlineData("managed-identity")]
    [InlineData("MANAGED-IDENTITY")]
    [InlineData("Managed-Identity")]
    public void Create_ManagedIdentity_Should_Return_ManagedIdentityCredential(string authMode)
    {
        var logger = new SimpleLogger(verbose: false);
        var credential = CredentialFactory.Create(authMode, logger);
        credential.Should().BeOfType<ManagedIdentityCredential>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("33333333-3333-3333-3333-333333333333")]
    public void Create_ManagedIdentity_Should_Use_The_User_Assigned_Identity_When_A_Client_Id_Is_Set(string? clientId)
    {
        var logger = new Mock<IDeploymentLogger>();

        var credential = CredentialFactory.CreateManagedIdentityCredential(logger.Object, clientId);

        credential.Should().BeOfType<ManagedIdentityCredential>();
        logger.Verify(l => l.Verbose(string.IsNullOrWhiteSpace(clientId)
            ? "ManagedIdentityCredential を使用します（システム割り当て）"
            : $"ManagedIdentityCredential を使用します（ユーザー割り当て: {clientId}）"));
    }

    [Theory]
    [InlineData("device-code")]
    [InlineData("DEVICE-CODE")]
    [InlineData("Device-Code")]
    public void Create_DeviceCode_Should_Return_DeviceCodeCredential(string authMode)
    {
        var logger = new SimpleLogger(verbose: false);
        var credential = CredentialFactory.Create(authMode, logger);
        credential.Should().BeOfType<DeviceCodeCredential>();
    }

    [Fact]
    public void Create_SpEnv_Without_EnvVars_Should_Throw()
    {
        // sp-env を明示指定した場合は別の資格情報にフォールバックせず停止する
        var originalTenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var originalClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var originalClientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");

        try
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", null);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", null);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);

            var logger = new SimpleLogger(verbose: false);
            var action = () => CredentialFactory.Create("sp-env", logger);
            action.Should().Throw<InvalidOperationException>()
                .WithMessage("*AZURE_TENANT_ID*AZURE_CLIENT_ID*AZURE_CLIENT_SECRET*");
        }
        finally
        {
            // Restore env vars
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", originalTenantId);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", originalClientId);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", originalClientSecret);
        }
    }

    [Fact]
    public void Create_SpEnv_With_EnvVars_Should_Return_ClientSecretCredential()
    {
        var originalTenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var originalClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var originalClientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");

        try
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "test-tenant-id");
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "test-client-id");
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "test-client-secret");

            var logger = new SimpleLogger(verbose: false);
            var credential = CredentialFactory.Create("sp-env", logger);
            credential.Should().BeOfType<ClientSecretCredential>();
        }
        finally
        {
            // Restore env vars
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", originalTenantId);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", originalClientId);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", originalClientSecret);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("invalid")]
    [InlineData("")]
    public void Create_InvalidAuthMode_Should_Throw_ArgumentException(string authMode)
    {
        var logger = new SimpleLogger(verbose: false);
        var action = () => CredentialFactory.Create(authMode, logger);
        action.Should().Throw<ArgumentException>()
            .WithMessage("*不明な認証モード*");
    }

    [Fact]
    public async Task DeviceCodeCallback_Should_Show_Sign_In_Message()
    {
        var output = new StringWriter();
        var callback = CredentialFactory.DeviceCodeCallback(new SimpleLogger(false, output, new StringWriter()));

        await callback(default, TestContext.Current.CancellationToken);

        output.ToString().Should().Contain("[INFO] デバイスコード認証:");
    }

    [Fact]
    public void CreateOnBehalfOf_Should_Use_The_Client_Secret_Or_The_Managed_Identity()
    {
        var output = new StringWriter();
        var logger = new SimpleLogger(verbose: true, output, new StringWriter());

        // 作るだけでは Entra ID に接続しない
        CredentialFactory.CreateOnBehalfOf("tenant", "client", "secret", "user-token", logger).Should().BeOfType<OnBehalfOfCredential>();
        CredentialFactory.CreateOnBehalfOf("tenant", "client", null, "user-token", logger).Should().BeOfType<OnBehalfOfCredential>();

        output.ToString().Should().Contain("OnBehalfOfCredential を使用します（クライアントシークレット）")
            .And.Contain("OnBehalfOfCredential を使用します（マネージド ID のフェデレーション資格情報）")
            .And.NotContain("secret").And.NotContain("user-token");
    }

    [Fact]
    public async Task ClientAssertion_Should_Return_The_Managed_Identity_Token_For_The_Token_Exchange()
    {
        var managedIdentity = new Mock<TokenCredential>();
        managedIdentity
            .Setup(c => c.GetTokenAsync(It.Is<TokenRequestContext>(r => r.Scopes.SequenceEqual(new[] { CredentialFactory.TokenExchangeScope })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken("mi-token", DateTimeOffset.UtcNow.AddHours(1)));

        var assertion = await CredentialFactory.ClientAssertion(managedIdentity.Object)(TestContext.Current.CancellationToken);

        assertion.Should().Be("mi-token");
        CredentialFactory.TokenExchangeScope.Should().Be("api://AzureADTokenExchange/.default");
    }
}
