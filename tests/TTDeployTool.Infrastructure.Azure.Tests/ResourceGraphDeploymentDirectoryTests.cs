using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph.Mocking;
using Azure.ResourceManager.ResourceGraph.Models;
using Azure.ResourceManager.Resources;
using FluentAssertions;
using Moq;
using Xunit;

namespace TTDeployTool.Infrastructure.Azure.Tests;

public class ResourceGraphDeploymentDirectoryTests
{
    private readonly Mock<ArmClient> _arm = new();
    private readonly Mock<MockableResourceGraphTenantResource> _graph = new();
    private readonly List<ResourceQueryContent> _queries = [];

    public ResourceGraphDeploymentDirectoryTests()
    {
        var tenant = new Mock<TenantResource>();
        tenant.Setup(t => t.GetCachedClient(It.IsAny<Func<ArmClient, MockableResourceGraphTenantResource>>())).Returns(_graph.Object);
        var tenants = new Mock<TenantCollection>();
        tenants.Setup(t => t.GetAllAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<TenantResource>.FromPages([Page<TenantResource>.FromValues([tenant.Object], null, Mock.Of<Response>())]));
        _arm.Setup(a => a.GetTenants()).Returns(tenants.Object);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Pages(params (string Json, string? SkipToken)[] pages)
    {
        var queue = new Queue<(string Json, string? SkipToken)>(pages);
        _graph.Setup(g => g.GetResourcesAsync(It.IsAny<ResourceQueryContent>(), It.IsAny<CancellationToken>()))
            .Callback<ResourceQueryContent, CancellationToken>((c, _) => _queries.Add(c))
            .ReturnsAsync(() =>
            {
                var (json, token) = queue.Dequeue();
                return Response.FromValue(
                    ArmResourceGraphModelFactory.ResourceQueryResult(1, 1, ResultTruncated.False, token, BinaryData.FromString(json), []),
                    Mock.Of<Response>());
            });
    }

    [Fact]
    public async Task List_Should_Read_Managed_Resources_Of_The_Subscriptions_With_Paging()
    {
        Pages(
            ("""
             [
               { "id": "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.App/containerApps/demo-db", "name": "demo-db",
                 "type": "microsoft.app/containerapps", "subscriptionId": "s1", "resourceGroup": "rg", "location": "japaneast",
                 "tags": { "managed-by": "ttdeploytool", "tt-app-name": "demo", "tt-expires-at": "none", "n": 1 },
                 "provisioningState": "Succeeded", "runningStatus": "Running", "image": "postgres:16", "cpu": 1, "memory": "2Gi", "fqdn": "",
                 "volumes": [ { "name": "data", "storageName": "dbdata" }, { "name": "tmp" }, 3 ] }
             ]
             """, "next"),
            ("""
             [
               { "id": "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.App/containerApps/demo-tt", "name": "demo-tt",
                 "type": "microsoft.app/containerapps", "subscriptionId": "s1", "resourceGroup": "rg", "location": "japaneast",
                 "tags": { "managed-by": "ttdeploytool" }, "cpu": "1.5", "fqdn": "demo-tt.example.io", "volumes": null },
               { "id": "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.App/containerApps/demo-redis", "name": "demo-redis",
                 "type": "microsoft.app/containerapps", "subscriptionId": "s1", "resourceGroup": "rg", "location": "japaneast",
                 "tags": {}, "cpu": null },
               { "id": "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/demo-env", "name": "demo-env",
                 "type": "microsoft.app/managedenvironments", "subscriptionId": "s1", "resourceGroup": "rg", "location": "japaneast",
                 "tags": null, "cpu": "x" }
             ]
             """, null));

        var result = await new ResourceGraphDeploymentDirectory(_arm.Object).ListAsync(["s1", "s2"], Ct);

        result.Should().HaveCount(4);
        var db = result[0];
        (db.Name, db.Type, db.SubscriptionId, db.ResourceGroup, db.Location).Should().Be(("demo-db", "microsoft.app/containerapps", "s1", "rg", "japaneast"));
        db.Tags.Should().Equal(new Dictionary<string, string> { ["managed-by"] = "ttdeploytool", ["tt-app-name"] = "demo", ["tt-expires-at"] = "none" });
        db.Tags["MANAGED-BY"].Should().Be("ttdeploytool");
        db.App.Should().BeEquivalentTo(new ContainerAppSnapshot("Succeeded", "Running", "postgres:16", 1, "2Gi", null, ["dbdata"]));
        db.App!.UsesDbStorage.Should().BeTrue();
        result[1].App.Should().BeEquivalentTo(new ContainerAppSnapshot(null, null, null, 1.5, null, "demo-tt.example.io", []));
        result[2].App!.Cpu.Should().BeNull();
        result[3].App.Should().BeNull();
        result[3].Tags.Should().BeEmpty();
        _queries.Should().HaveCount(2);
        _queries[0].Subscriptions.Should().Equal("s1", "s2");
        _queries[0].Options.ResultFormat.Should().Be(ResultFormat.ObjectArray);
        _queries[0].Options.Top.Should().Be(1000);
        _queries[0].Options.SkipToken.Should().BeNull();
        _queries[1].Options.SkipToken.Should().Be("next");
        _queries[0].Query.Should().Contain("order by id asc").And.Contain("'ttdeploytool', 'timetracker-deployment-tool'");
    }

    [Fact]
    public async Task List_Should_Not_Query_Without_Subscriptions()
    {
        var result = await new ResourceGraphDeploymentDirectory(_arm.Object).ListAsync([], Ct);

        result.Should().BeEmpty();
        _arm.Verify(a => a.GetTenants(), Times.Never);
    }
}
