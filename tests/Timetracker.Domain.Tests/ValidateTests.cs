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

    [Fact]
    public void Valid_Options_Should_Pass()
    {
        var act = () => ComposeGenerator.Validate(CreateOptions());
        act.Should().NotThrow();
    }

    public static IEnumerable<object[]> InvalidCases()
    {
        yield return new object[] { (Action<DeployOptions>)(o => o.DbType = "mysql"), "*db-type*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.DbType = "PostgreSQL"), "*db-type*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.Subscription = " "), "*subscription*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.ResourceGroup = ""), "*resource-group*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.TrackerPassword = "short"), "*tracker-password*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.DbPassword = "short"), "*db-password*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.TimetrackerTag = ""), "*tt-tag*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.TimetrackerTag = "11.0.1-linux-mssql"), "*不適切な組み合わせ*" };
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Invalid_Options_Should_Throw(Action<DeployOptions> mutate, string expectedMessage)
    {
        var o = CreateOptions();
        mutate(o);
        var act = () => ComposeGenerator.Validate(o);
        act.Should().Throw<ArgumentException>().WithMessage(expectedMessage);
    }

    [Theory]
    [InlineData("sqlserver", "11.0.1-linux-postgres")]
    [InlineData("postgresql", "11.0.1-linux-sqlserver")]
    public void Mismatched_DbType_And_Tag_Should_Throw(string dbType, string tag)
    {
        var o = CreateOptions(dbType);
        o.TimetrackerTag = tag;
        var act = () => ComposeGenerator.Validate(o);
        act.Should().Throw<ArgumentException>().WithMessage("*不適切な組み合わせ*");
    }
}
