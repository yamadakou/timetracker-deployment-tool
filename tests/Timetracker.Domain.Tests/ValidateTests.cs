using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Domain.Tests;

public class ValidateTests
{
    private static DeployOptions CreateOptions(string dbType = "postgresql", string dbPassword = "Passw0rd!x") => new()
    {
        Subscription = "sub",
        ResourceGroup = "rg",
        DbType = dbType,
        DbPassword = dbPassword,
        TrackerPassword = "Passw0rd!y",
        TimetrackerTag = Defaults.GetDefaultTimetrackerTag(dbType),
    };

    [Fact]
    public void DbName_Default_Should_Be_Consistent()
    {
        Defaults.DbName.Should().Be("timetracker");
        CreateOptions().DbName.Should().Be(Defaults.DbName);
    }

    [Theory]
    [InlineData("Passw0rd")]    // 大文字・小文字・数字
    [InlineData("passw0rd!")]   // 小文字・数字・記号
    [InlineData("PASSWORD1!")]  // 大文字・数字・記号
    [InlineData("Password!")]   // 大文字・小文字・記号
    public void SqlServer_Should_Accept_Complex_Password(string password)
    {
        var act = () => ComposeGenerator.Validate(CreateOptions("sqlserver", password));
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("password")]    // 小文字のみ
    [InlineData("password1")]   // 2種
    [InlineData("PASSWORD!!")]  // 2種
    [InlineData("12345678")]    // 数字のみ
    public void SqlServer_Should_Reject_Weak_Password(string password)
    {
        var act = () => ComposeGenerator.Validate(CreateOptions("sqlserver", password));
        act.Should().Throw<ArgumentException>().WithMessage("*db-password*");
    }

    [Fact]
    public void PostgreSql_Should_Not_Require_Complexity()
    {
        var act = () => ComposeGenerator.Validate(CreateOptions("postgresql", "password"));
        act.Should().NotThrow();
    }
}
