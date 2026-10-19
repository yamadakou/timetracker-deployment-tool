using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Domain.Tests;

public class AppNameValidatorTests
{
    [Theory]
    [InlineData("timetracker")]
    [InlineData("my-app")]
    [InlineData("app123")]
    [InlineData("a1")]
    [InlineData("myapp-v2")]
    [InlineData("ab")]
    [InlineData("a1234567890123456789012345")] // 26 chars - max length (32 - "-redis")
    public void IsValid_Should_Return_True_For_Valid_Names(string name)
    {
        AppNameValidator.IsValid(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("TimeTracker")] // Contains uppercase letters
    [InlineData("")]
    [InlineData("a")] // Too short (1 char)
    [InlineData("a12345678901234567890123456")] // 27 chars - too long ("-redis" を付けると 33 文字)
    [InlineData("a1234567890123456789012345678901")] // 32 chars - too long
    [InlineData("1app")] // Starts with digit
    [InlineData("-app")] // Starts with hyphen
    [InlineData("app-")] // Ends with hyphen
    [InlineData("my--app")] // Double hyphen
    [InlineData("app_name")] // Contains underscore
    [InlineData("app.name")] // Contains dot
    [InlineData("app name")] // Contains space
    [InlineData("MyApp")] // Contains uppercase
    public void IsValid_Should_Return_False_For_Invalid_Names(string name)
    {
        AppNameValidator.IsValid(name).Should().BeFalse();
    }

    [Fact]
    public void IsValid_Should_Return_False_For_Null()
    {
        AppNameValidator.IsValid(null!).Should().BeFalse();
    }

    [Theory]
    [InlineData("timetracker")]
    [InlineData("my-app")]
    [InlineData("app123")]
    public void GetError_Should_Return_Null_For_Valid_Names(string name)
    {
        AppNameValidator.GetError(name).Should().BeNull();
    }

    [Fact]
    public void GetError_Should_Return_Error_For_Empty_Name()
    {
        var error = AppNameValidator.GetError("");
        error.Should().NotBeNull();
        error.Should().Contain("空");
    }

    [Fact]
    public void GetError_Should_Return_Error_For_Short_Name()
    {
        var error = AppNameValidator.GetError("a");
        error.Should().NotBeNull();
        error.Should().Contain("短すぎ");
    }

    [Fact]
    public void GetError_Should_Return_Error_For_Long_Name()
    {
        var longName = new string('a', 27);
        var error = AppNameValidator.GetError(longName);
        error.Should().NotBeNull();
        error.Should().Contain("長すぎ");
    }

    [Fact]
    public void GetError_Should_Return_Error_For_Double_Hyphen()
    {
        var error = AppNameValidator.GetError("my--app");
        error.Should().NotBeNull();
        error.Should().Contain("連続するハイフン");
    }

    [Fact]
    public void GetError_Should_Return_Error_For_Starting_With_Digit()
    {
        var error = AppNameValidator.GetError("1app");
        error.Should().NotBeNull();
        error.Should().Contain("英小文字で始まる");
    }

    [Fact]
    public void GetError_Should_Return_Error_For_Starting_With_Hyphen()
    {
        var error = AppNameValidator.GetError("-app");
        error.Should().NotBeNull();
        error.Should().Contain("英小文字で始まる");
    }

    [Fact]
    public void GetError_Should_Return_Error_For_Ending_With_Hyphen()
    {
        var error = AppNameValidator.GetError("app-");
        error.Should().NotBeNull();
        error.Should().Contain("英小文字または数字で終わる");
    }

    [Fact]
    public void GetError_Should_Return_Error_For_Invalid_Characters()
    {
        var error = AppNameValidator.GetError("app_name");
        error.Should().NotBeNull();
        error.Should().Contain("無効な文字");
    }

    [Fact]
    public void GetError_Should_Return_Error_For_Uppercase_Characters()
    {
        var error = AppNameValidator.GetError("MyApp");
        error.Should().NotBeNull();
        error.Should().Contain("英小文字で始まる");
    }

    [Fact]
    public void MaxLength_Plus_Longest_Suffix_Should_Fit_Container_App_Name_Limit()
    {
        var name = new string('a', AppNameValidator.MaxLength);
        foreach (var suffix in AppNameValidator.ResourceSuffixes)
        {
            (name + suffix).Length.Should().BeLessThanOrEqualTo(AppNameValidator.ContainerAppNameMaxLength);
        }
    }
}
