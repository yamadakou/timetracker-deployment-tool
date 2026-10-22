using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Domain.Tests;

/// <summary>利用可能期限（docs/design/desktop-jobs.md の 6.1）。</summary>
public class DeploymentExpiryTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    // 日本時間の 2026-10-22 05:00（UTC では前の日の 2026-10-21 20:00）
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, string> Tags(string value) => new() { ["TT-Expires-At"] = value };

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("none")]
    [InlineData(" NONE ")]
    [InlineData("2026-11-01")]
    public void Dates_And_None_Should_Be_Accepted(string? input) => DeploymentExpiry.FormatError(input).Should().BeNull();

    [Theory]
    [InlineData("2026/11/01")]
    [InlineData("tomorrow")]
    [InlineData("2026-02-30")]
    public void Other_Values_Should_Be_Rejected(string input) =>
        DeploymentExpiry.FormatError(input).Should().Be($"expires-on には yyyy-MM-dd の日付か none を指定してください: {input}");

    [Fact]
    public void Dates_Before_Today_In_The_Time_Zone_Should_Be_Rejected()
    {
        DeploymentExpiry.PastDateError("2026-10-21", Now, Tokyo).Should().Be("利用可能期限日（2026-10-21）は、今日以降の日付を指定してください。");
        DeploymentExpiry.PastDateError("2026-10-22", Now, Tokyo).Should().BeNull("今日は使える");
        // 日本時間では 10-22 だが、UTC ではまだ 10-21
        DeploymentExpiry.PastDateError("2026-10-21", Now, TimeZoneInfo.Utc).Should().BeNull();
        DeploymentExpiry.Today(Now, TimeZoneInfo.Utc).Should().Be(new DateOnly(2026, 10, 21));
        DeploymentExpiry.PastDateError("none", Now, Tokyo).Should().BeNull();
        DeploymentExpiry.PastDateError(null, Now, Tokyo).Should().BeNull();
    }

    [Fact]
    public void A_Date_Should_Expire_At_The_End_Of_That_Day_In_The_Time_Zone()
    {
        var decision = DeploymentExpiry.Resolve("2026-11-01", Tags("none"), Now, Tokyo);

        decision.Should().Be(new ExpiryDecision(new DateTimeOffset(2026, 11, 1, 23, 59, 59, TimeSpan.FromHours(9)), ExpirySource.Specified));
        decision.TagValue.Should().Be("2026-11-01T14:59:59Z");
        decision.Describe(Tokyo).Should().Be("2026-11-01");
    }

    [Fact]
    public void None_Should_Mean_No_Expiry()
    {
        var decision = DeploymentExpiry.Resolve("None", Tags("2026-11-01T14:59:59Z"), Now, Tokyo);

        decision.Should().Be(new ExpiryDecision(null, ExpirySource.Specified));
        decision.TagValue.Should().Be("none");
        decision.Describe(Tokyo).Should().Be("期限なし");
    }

    [Fact]
    public void Omitting_Should_Inherit_The_Current_Expiry()
    {
        var inherited = DeploymentExpiry.Resolve(null, Tags("2026-11-01T14:59:59Z"), Now, Tokyo);
        var noExpiry = DeploymentExpiry.Resolve(null, Tags("NONE"), Now, Tokyo);

        inherited.Should().Be(new ExpiryDecision(new DateTimeOffset(2026, 11, 1, 14, 59, 59, TimeSpan.Zero), ExpirySource.Inherited));
        inherited.TagValue.Should().Be("2026-11-01T14:59:59Z");
        inherited.Describe(Tokyo).Should().Be("2026-11-01（今の期限を引き継ぎます）");
        noExpiry.Should().Be(new ExpiryDecision(null, ExpirySource.Inherited));
        noExpiry.Describe(Tokyo).Should().Be("期限なし（今の設定を引き継ぎます）");
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "broken")]
    public void Omitting_Without_A_Readable_Expiry_Should_Use_32_Days(bool hasTags, string? value)
    {
        IReadOnlyDictionary<string, string>? tags = !hasTags ? null : value == null ? new Dictionary<string, string> { ["owner"] = "alice" } : Tags(value);

        var decision = DeploymentExpiry.Resolve(" ", tags, Now, Tokyo);

        decision.Should().Be(new ExpiryDecision(new DateTimeOffset(2026, 11, 23, 23, 59, 59, TimeSpan.FromHours(9)), ExpirySource.Default));
        decision.Describe(Tokyo).Should().Be("2026-11-23（既定: 今日から 32 日後）");
        DeploymentExpiry.DefaultExpiry(Now, Tokyo).Should().Be(decision.ExpiresAt);
    }

    [Fact]
    public void Tags_Should_Be_Utc()
    {
        DeploymentExpiry.FormatTag(new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.FromHours(9))).Should().Be("2026-10-22T00:30:00Z");
        DeploymentExpiry.Today(Now, Tokyo).Should().Be(new DateOnly(2026, 10, 22));
    }

    [Fact]
    public void The_Input_Should_Be_Validated_And_Trimmed_For_Azure_Only()
    {
        var options = DeployOptionsResolver.Resolve(new DeployInput
        {
            Subscription = "sub", ResourceGroup = "rg", DbPassword = "Passw0rd!", TrackerPassword = "Passw0rd!", ExpiresOn = " 2026/11/01 ",
        });

        options.ExpiresOn.Should().Be("2026/11/01");
        DeploymentValidator.GetErrors(options, ValidationTarget.Azure).Should().Equal("expires-on には yyyy-MM-dd の日付か none を指定してください: 2026/11/01");
        DeploymentValidator.GetErrors(options, ValidationTarget.Compose).Should().BeEmpty();
        DeployOptionsResolver.Resolve(new DeployInput { ExpiresOn = " " }).ExpiresOn.Should().BeNull();
    }

    [Fact]
    public void Lifecycle_Tags_Should_Be_Set_On_A_Copy()
    {
        var options = DeployOptionsResolver.Resolve(new DeployInput());

        var tagged = options.WithLifecycleTags(new Dictionary<string, string> { [DeploymentExpiry.ExpiresAtKey] = "none" });

        tagged.LifecycleTags.Should().ContainKey("tt-expires-at");
        options.LifecycleTags.Should().BeNull();
    }
}
