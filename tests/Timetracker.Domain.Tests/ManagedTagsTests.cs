using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Domain.Tests;

public class ManagedTagsTests
{
    [Fact]
    public void For_Should_Return_Managed_By_And_App_Name()
    {
        ManagedTags.For("demo").Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["managed-by"] = "timetracker-deployment-tool",
            ["tt-app-name"] = "demo",
        });
    }

    [Fact]
    public void Merge_Should_Keep_User_Tags_And_Overwrite_Managed_Tags()
    {
        var existing = new Dictionary<string, string>
        {
            ["owner"] = "alice",
            ["Managed-By"] = "someone-else", // キーの大文字小文字が違っても上書きする
            ["tt-app-name"] = "old",
        };

        var merged = ManagedTags.Merge(existing, "demo");

        merged.Should().HaveCount(3);
        merged["owner"].Should().Be("alice");
        merged["managed-by"].Should().Be("timetracker-deployment-tool");
        merged["tt-app-name"].Should().Be("demo");
    }

    [Fact]
    public void Merge_Should_Accept_No_Existing_Tags()
    {
        ManagedTags.Merge(null, "demo").Should().BeEquivalentTo(ManagedTags.For("demo"));
    }

    public static IEnumerable<object?[]> IsManagedCases()
    {
        yield return new object?[] { null, false };
        yield return new object?[] { new Dictionary<string, string>(), false };
        yield return new object?[] { new Dictionary<string, string> { ["tt-app-name"] = "demo" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "other", ["tt-app-name"] = "demo" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "timetracker-deployment-tool" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "timetracker-deployment-tool", ["tt-app-name"] = "other" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "timetracker-deployment-tool", ["tt-app-name"] = "demo" }, true };
        // Azure のタグ名は大文字小文字を区別しない。値も区別せずに比較する
        yield return new object?[] { new Dictionary<string, string> { ["Managed-By"] = "Timetracker-Deployment-Tool", ["TT-APP-NAME"] = "Demo" }, true };
    }

    [Theory]
    [MemberData(nameof(IsManagedCases))]
    public void IsManaged_Should_Require_Both_Tags(Dictionary<string, string>? tags, bool expected)
    {
        ManagedTags.IsManaged(tags, "demo").Should().Be(expected);
    }

    public static IEnumerable<object?[]> HasOtherOwnerCases()
    {
        yield return new object?[] { null, false };
        yield return new object?[] { new Dictionary<string, string> { ["owner"] = "alice" }, false };
        // 本ツールのタグの一部だけが残っているものは、他の管理者がいるとはみなさない
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "timetracker-deployment-tool" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["TT-APP-NAME"] = "Demo" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "timetracker-deployment-tool", ["tt-app-name"] = "demo" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "terraform" }, true };
        yield return new object?[] { new Dictionary<string, string> { ["tt-app-name"] = "other" }, true };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "timetracker-deployment-tool", ["tt-app-name"] = "other" }, true };
    }

    [Theory]
    [MemberData(nameof(HasOtherOwnerCases))]
    public void HasOtherOwner_Should_Detect_Management_Tags_Of_Others(Dictionary<string, string>? tags, bool expected)
    {
        ManagedTags.HasOtherOwner(tags, "demo").Should().Be(expected);
    }
}
