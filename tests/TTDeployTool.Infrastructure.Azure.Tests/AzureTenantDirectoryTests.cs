using System.ClientModel.Primitives;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using FluentAssertions;
using Moq;
using Xunit;

namespace TTDeployTool.Infrastructure.Azure.Tests;

public class AzureTenantDirectoryTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private static TenantResource Tenant(string json)
    {
        var tenant = new Mock<TenantResource>();
        tenant.SetupGet(t => t.Data).Returns(ModelReaderWriter.Read<TenantData>(BinaryData.FromString(json))!);
        return tenant.Object;
    }

    [Fact]
    public async Task ListAsync_Should_Return_Tenants_With_An_Id()
    {
        var arm = new Mock<ArmClient>();
        var tenants = new Mock<TenantCollection>();
        arm.Setup(a => a.GetTenants()).Returns(tenants.Object);
        tenants.Setup(t => t.GetAllAsync(It.IsAny<CancellationToken>())).Returns(AsyncPageable<TenantResource>.FromPages(
        [
            Page<TenantResource>.FromValues(
            [
                Tenant($$"""{ "id": "/tenants/{{TenantId}}", "tenantId": "{{TenantId}}", "displayName": "開発", "defaultDomain": "dev.example.com" }"""),
                Tenant("""{ "id": "/tenants/x", "displayName": "ID なし" }"""),
            ], null, Mock.Of<Response>()),
        ]));

        var result = await new AzureTenantDirectory(arm.Object).ListAsync(TestContext.Current.CancellationToken);

        result.Should().Equal(new TenantInfo(TenantId, "開発", "dev.example.com"));
    }

    [Fact]
    public async Task TenantScopedCredential_Should_Request_Tokens_For_The_Tenant_And_Keep_Claims()
    {
        var inner = new Mock<TokenCredential>();
        var token = new AccessToken("t", DateTimeOffset.MaxValue);
        var requested = new List<TokenRequestContext>();
        inner.Setup(c => c.GetToken(Capture.In(requested), It.IsAny<CancellationToken>())).Returns(token);
        inner.Setup(c => c.GetTokenAsync(Capture.In(requested), It.IsAny<CancellationToken>())).ReturnsAsync(token);
        var credential = new TenantScopedCredential(inner.Object, TenantId);
        var context = new TokenRequestContext(["https://management.azure.com/.default"], parentRequestId: "p", claims: "{\"mfa\":1}", tenantId: "other", isCaeEnabled: true);

        credential.GetToken(context, TestContext.Current.CancellationToken).Should().Be(token);
        (await credential.GetTokenAsync(context, TestContext.Current.CancellationToken)).Should().Be(token);

        credential.TenantId.Should().Be(TenantId);
        requested.Should().HaveCount(2).And.OnlyContain(r =>
            r.TenantId == TenantId && r.Claims == "{\"mfa\":1}" && r.ParentRequestId == "p" && r.IsCaeEnabled
            && r.Scopes.SequenceEqual(new[] { "https://management.azure.com/.default" }));
    }
}
