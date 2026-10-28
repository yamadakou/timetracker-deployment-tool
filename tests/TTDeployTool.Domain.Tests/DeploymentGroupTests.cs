using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Domain.Tests;

/// <summary>デプロイのグループ（docs/design/common/deployment-groups.md の 3・4）。</summary>
public class DeploymentGroupTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" 営業部 ", "営業部")]
    public void Input_Should_Be_Trimmed_And_Empty_Means_None(string? input, string? expected) =>
        DeploymentGroup.Normalize(input).Should().Be(expected);

    [Fact]
    public void Names_Up_To_64_Characters_Without_Control_Characters_Should_Be_Allowed()
    {
        DeploymentGroup.FormatError(null).Should().BeNull();
        DeploymentGroup.FormatError(" ").Should().BeNull("空は指定しない");
        DeploymentGroup.FormatError(" " + new string('あ', 64) + " ").Should().BeNull("前後の空白は数えない");
        DeploymentGroup.FormatError(new string('a', 65)).Should().Be("group（グループ名）は 64 文字までにしてください（65 文字）。");
        DeploymentGroup.FormatError("営業\n研修").Should().Be("group（グループ名）に、改行やタブなどの制御文字は使えません。");
        DeploymentGroup.FormatError("営業\t研修").Should().NotBeNull();
    }

    [Fact]
    public void The_Tag_Should_Be_Read_Regardless_Of_The_Case_Of_Its_Name()
    {
        DeploymentGroup.Read(new Dictionary<string, string> { ["TT-Group"] = " 研修 " }).Should().Be("研修");
        DeploymentGroup.Read(new Dictionary<string, string> { ["tt-app-name"] = "demo" }).Should().BeNull();
        DeploymentGroup.Read(new Dictionary<string, string> { ["tt-group"] = " " }).Should().BeNull();
        DeploymentGroup.Read(null).Should().BeNull();
    }

    [Fact]
    public void Values_Of_Several_Resources_Should_Be_Combined_In_Order()
    {
        DeploymentGroup.Combine([null, "営業", "研修"]).Should().Be("営業", "先に渡したもの（Timetracker）を優先する");
        DeploymentGroup.Combine([null, " "]).Should().BeNull();
        DeploymentGroup.Differ(["営業", " 営業 "]).Should().BeFalse();
        DeploymentGroup.Differ(["営業", null]).Should().BeTrue("付いていないリソースがある");
        DeploymentGroup.Differ(["Sales", "sales"]).Should().BeTrue("書き方の違いも食い違いとして示す");
        DeploymentGroup.Differ([]).Should().BeFalse();
        DeploymentGroup.Comparer.Equals("Sales", "sales").Should().BeTrue("一覧では同じグループとして束ねる");
    }

    [Fact]
    public void A_Deployment_Without_A_Group_Should_Keep_The_Current_One()
    {
        var inherited = DeploymentGroup.Resolve(null, " 営業 ");
        inherited.Should().Be(new GroupDecision("営業", "営業"));
        (inherited.Inherited, inherited.Changes).Should().Be((true, false));
        inherited.Describe().Should().Be("グループ: 営業（今のグループを引き継ぎます）");

        var changed = DeploymentGroup.Resolve(" 研修 ", "営業");
        (changed.Planned, changed.Inherited, changed.Changes).Should().Be(("研修", false, true));
        changed.Describe().Should().Be("グループ: 研修（今の 営業 から変えます）");

        var recased = DeploymentGroup.Resolve("sales", "Sales");
        recased.Changes.Should().BeTrue("大文字と小文字の違いも書き換える");

        var added = DeploymentGroup.Resolve("研修", null);
        (added.Inherited, added.Changes).Should().Be((false, true));
        added.Describe().Should().Be("グループ: 研修");

        var none = DeploymentGroup.Resolve(null, null);
        (none.Planned, none.Inherited, none.Changes).Should().Be((null, false, false));
        none.Describe().Should().Be("グループ: なし");

        DeploymentGroup.Resolve("営業", "営業").Should().Match<GroupDecision>(d => d.Inherited && !d.Changes, "同じ名前を指定したら引き継ぎと同じ");
    }

    [Fact]
    public void The_Resolver_And_Validator_Should_Handle_The_Group()
    {
        DeployOptionsResolver.Resolve(new DeployInput { Group = " 営業 " }).Group.Should().Be("営業");
        DeployOptionsResolver.Resolve(new DeployInput { Group = " " }).Group.Should().BeNull();

        var options = DeployOptionsResolver.Resolve(new DeployInput
        {
            Subscription = "sub", ResourceGroup = "rg", DbPassword = "DbPassw0rd!", TrackerPassword = "TrackerPass!", Group = new string('x', 65),
        });
        DeploymentValidator.GetErrors(options, ValidationTarget.Azure).Should().ContainSingle().Which.Should().StartWith("group（グループ名）は 64 文字まで");
        DeploymentValidator.GetErrors(options, ValidationTarget.Compose).Should().BeEmpty("Dry-run ではタグを使わない");
    }
}
