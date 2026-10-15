using System.ClientModel.Primitives;
using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using FluentAssertions;
using Moq;
using Xunit;

namespace Timetracker.Infrastructure.Azure.Tests;

public class AzureSubscriptionDirectoryTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private static SubscriptionResource Subscription(string json)
    {
        var sub = new Mock<SubscriptionResource>();
        sub.SetupGet(s => s.HasData).Returns(true);
        sub.SetupGet(s => s.Data).Returns(ModelReaderWriter.Read<SubscriptionData>(BinaryData.FromString(json))!);
        return sub.Object;
    }

    [Fact]
    public async Task ListAsync_Should_Return_Id_Name_State_And_Tenant()
    {
        var arm = new Mock<ArmClient>();
        var subs = new Mock<SubscriptionCollection>();
        arm.Setup(a => a.GetSubscriptions()).Returns(subs.Object);
        subs.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).Returns(AsyncPageable<SubscriptionResource>.FromPages(
        [
            Page<SubscriptionResource>.FromValues(
            [
                Subscription($$"""{ "id": "/subscriptions/aaa", "subscriptionId": "aaa", "displayName": "開発", "state": "Enabled", "tenantId": "{{TenantId}}" }"""),
                // 状態とテナントが返らない場合もある
                Subscription("""{ "id": "/subscriptions/bbb", "subscriptionId": "bbb", "displayName": "検証" }"""),
            ], null, Mock.Of<Response>()),
        ]));

        var result = await new AzureSubscriptionDirectory(arm.Object).ListAsync(TestContext.Current.CancellationToken);

        result.Should().Equal(
            new SubscriptionInfo("aaa", "開発", "Enabled", TenantId),
            new SubscriptionInfo("bbb", "検証", null, null));
    }
}
