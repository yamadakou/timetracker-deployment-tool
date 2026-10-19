using FluentAssertions;
using Xunit;

namespace TTDeployTool.SystemTests;

/// <summary>
/// 中断されたテストが残したリソースの判定（掃除の対象）のテスト。Azure には接続しないため、通常の dotnet test でも実行する。
/// 判定を誤ると、実行中の別のテストのリソースを削除するか、料金が発生するリソースが残り続ける。
/// </summary>
public sealed class LeftoverDetectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = Now - AzureTestEnvironment.LeftoverAge;
    private static readonly DateTimeOffset Recent = Old.AddSeconds(1);

    public static TheoryData<string, Dictionary<string, string>, bool> ResourceGroupCases() => new()
    {
        // ちょうど LeftoverAge たったものは対象
        { "rg-ttst-202610150900-abcd", new() { [AzureTestEnvironment.CreatedAtTag] = Old.ToString("o") }, true },
        { "RG-TTST-202610150900-abcd", new() { [AzureTestEnvironment.CreatedAtTag] = Old.ToString("o") }, true },
        // 作成から間もないもの（実行中の別のテスト）は対象外
        { "rg-ttst-202610150900-abcd", new() { [AzureTestEnvironment.CreatedAtTag] = Recent.ToString("o") }, false },
        // 接頭辞が違うもの・作成日時のタグが無い／読めないものは、テストが作ったと確かめられないため対象外
        { "rg-other", new() { [AzureTestEnvironment.CreatedAtTag] = Old.ToString("o") }, false },
        { "rg-ttst-limited", new() { ["purpose"] = "ttdeploytool-systemtest" }, false },
        { "rg-ttst-202610150900-abcd", new() { [AzureTestEnvironment.CreatedAtTag] = "not-a-date" }, false },
    };

    [Theory]
    [MemberData(nameof(ResourceGroupCases))]
    public void IsLeftoverResourceGroup_Should_Select_Only_Old_Test_Resource_Groups(string name, Dictionary<string, string> tags, bool expected)
    {
        AzureTestEnvironment.IsLeftoverResourceGroup(name, tags, Now).Should().Be(expected);
    }

    [Fact]
    public void IsLeftoverResourceGroup_Should_Treat_Created_At_Without_Offset_As_Utc()
    {
        var tags = new Dictionary<string, string> { [AzureTestEnvironment.CreatedAtTag] = Recent.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss") };

        AzureTestEnvironment.IsLeftoverResourceGroup("rg-ttst-x", tags, Now).Should().BeFalse();
    }

    public static TheoryData<Dictionary<string, string>, DateTimeOffset?, string?> AppCases() => new()
    {
        { new() { ["tt-app-name"] = "ttstabc123" }, Old, "ttstabc123" },
        // 作成日時が取得できないものは古いものとみなす（残り続けないように）
        { new() { ["tt-app-name"] = "ttstabc123" }, null, "ttstabc123" },
        // 作成から間もないもの（実行中の別のテスト）は対象外
        { new() { ["tt-app-name"] = "ttstabc123" }, Recent, null },
        // テストのアプリでないもの（接頭辞が違う・大文字・タグが無い）は対象外
        { new() { ["tt-app-name"] = "timetracker" }, Old, null },
        { new() { ["tt-app-name"] = "TTSTabc123" }, Old, null },
        { new() { ["managed-by"] = "ttdeploytool" }, Old, null },
    };

    [Theory]
    [MemberData(nameof(AppCases))]
    public void LeftoverTestAppName_Should_Select_Only_Old_Test_Apps(Dictionary<string, string> tags, DateTimeOffset? createdOn, string? expected)
    {
        AzureTestEnvironment.LeftoverTestAppName(tags, createdOn, Now).Should().Be(expected);
    }
}
