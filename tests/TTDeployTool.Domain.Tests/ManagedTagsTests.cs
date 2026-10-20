using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Domain.Tests;

public class ManagedTagsTests
{
    [Fact]
    public void For_Should_Return_Managed_By_And_App_Name()
    {
        ManagedTags.For("demo").Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["managed-by"] = "ttdeploytool",
            ["tt-app-name"] = "demo",
        });
    }

    [Fact]
    public void Resource_Group_Tags_Should_Show_That_The_Tool_Created_It()
    {
        ManagedTags.ForResourceGroup().Should().Equal(new Dictionary<string, string> { ["managed-by"] = "ttdeploytool" });
        ManagedTags.IsCreatedByTool(ManagedTags.ForResourceGroup()).Should().BeTrue();
        ManagedTags.IsCreatedByTool(new Dictionary<string, string> { ["Managed-By"] = "timetracker-deployment-tool" }).Should().BeTrue("以前の名前の値");
        ManagedTags.IsCreatedByTool(new Dictionary<string, string> { ["managed-by"] = "other" }).Should().BeFalse();
        ManagedTags.IsCreatedByTool(new Dictionary<string, string>()).Should().BeFalse();
        ManagedTags.IsCreatedByTool(null).Should().BeFalse();
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
        merged["managed-by"].Should().Be("ttdeploytool");
        merged["tt-app-name"].Should().Be("demo");
    }

    [Fact]
    public void Merge_Should_Replace_The_Value_Of_The_Previous_Tool_Name()
    {
        var merged = ManagedTags.Merge(new Dictionary<string, string> { ["managed-by"] = ManagedTags.LegacyManagedByValue, ["tt-app-name"] = "demo" }, "demo");

        merged["managed-by"].Should().Be("ttdeploytool");
        ManagedTags.LegacyManagedByValue.Should().Be("timetracker-deployment-tool");
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
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "ttdeploytool" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "ttdeploytool", ["tt-app-name"] = "other" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "ttdeploytool", ["tt-app-name"] = "demo" }, true };
        // Azure のタグ名は大文字小文字を区別しない。値も区別せずに比較する
        yield return new object?[] { new Dictionary<string, string> { ["Managed-By"] = "TTDeployTool", ["TT-APP-NAME"] = "Demo" }, true };
        // 本ツールの以前の名前の版で付けた値も、本ツールのものとして扱う
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "timetracker-deployment-tool", ["tt-app-name"] = "demo" }, true };
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
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "ttdeploytool" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["TT-APP-NAME"] = "Demo" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "ttdeploytool", ["tt-app-name"] = "demo" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "timetracker-deployment-tool", ["tt-app-name"] = "demo" }, false };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "terraform" }, true };
        yield return new object?[] { new Dictionary<string, string> { ["tt-app-name"] = "other" }, true };
        yield return new object?[] { new Dictionary<string, string> { ["managed-by"] = "ttdeploytool", ["tt-app-name"] = "other" }, true };
    }

    [Theory]
    [MemberData(nameof(HasOtherOwnerCases))]
    public void HasOtherOwner_Should_Detect_Management_Tags_Of_Others(Dictionary<string, string>? tags, bool expected)
    {
        ManagedTags.HasOtherOwner(tags, "demo").Should().Be(expected);
    }
}
