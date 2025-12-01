using Azure.Identity;
using FluentAssertions;
using Timetracker.Common;
using Timetracker.Controller.Cli;
using Xunit;

namespace Timetracker.Controller.Cli.Tests;

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
    public void Create_SpEnv_Without_EnvVars_Should_Fallback_To_DefaultAzureCredential()
    {
        // Clear env vars to ensure fallback
        var originalTenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var originalClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var originalClientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");

        try
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", null);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", null);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);

            var logger = new SimpleLogger(verbose: false);
            var credential = CredentialFactory.Create("sp-env", logger);
            credential.Should().BeOfType<DefaultAzureCredential>();
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
}
