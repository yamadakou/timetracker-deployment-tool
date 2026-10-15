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
        yield return new object[] { (Action<DeployOptions>)(o => o.AppName = "bad_name"), "*bad_name*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.AppName = "Upper"), "*Upper*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.TrackerPassword = "short"), "*tracker-password*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.TrackerPassword = " "), "*tracker-password*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.TrackerPassword = null!), "*tracker-password*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.DbPassword = "short"), "*db-password*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.DbPassword = ""), "*db-password*" };
        yield return new object[] { (Action<DeployOptions>)(o => o.DbPassword = null!), "*db-password*" };
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

public class ValidationRuleTests
{
    [Theory]
    [InlineData("Passw0rd", true)]   // 8 文字・3 種類
    [InlineData("Pa0!", false)]      // 4 種類だが 8 文字未満
    [InlineData("password", false)]  // 1 種類
    [InlineData("Password", false)]  // 2 種類
    public void MeetsSqlServerPasswordPolicy_Should_Require_Length_And_Three_Categories(string password, bool expected)
    {
        ComposeGenerator.MeetsSqlServerPasswordPolicy(password).Should().Be(expected);
    }

    [Theory]
    [InlineData("postgresql", "11.0.1-linux-postgres", true)]
    [InlineData("postgresql", "11.0.1-linux-mssql", false)]
    [InlineData("sqlserver", "11.0.1-linux-mssql", true)]
    [InlineData("sqlserver", "7.0-linux-SQLServer", true)]
    [InlineData("sqlserver", "11.0.1-linux-postgres", false)]
    [InlineData("mysql", "11.0.1-linux-postgres", false)] // 未対応の DB 種別はどのタグとも一致しない
    public void TagMatchesDbType_Should_Check_Tag_For_Db_Type(string dbType, string tag, bool expected)
    {
        ComposeGenerator.TagMatchesDbType(dbType, tag).Should().Be(expected);
    }
}

public class ValidateResourceSpecTests
{
    private static DeployOptions CreateOptions() => new()
    {
        Subscription = "sub",
        ResourceGroup = "rg",
        DbPassword = "Passw0rd!x",
        TrackerPassword = "Passw0rd!y",
    };

    public static IEnumerable<object[]> InvalidSpecs()
    {
        var setters = new (string Name, Action<DeployOptions, double> Set)[]
        {
            ("tt-cpu", (o, v) => o.TimetrackerCpu = v),
            ("tt-memory", (o, v) => o.TimetrackerMemoryGi = v),
            ("db-cpu", (o, v) => o.DbCpu = v),
            ("db-memory", (o, v) => o.DbMemoryGi = v),
            ("redis-cpu", (o, v) => o.RedisCpu = v),
            ("redis-memory", (o, v) => o.RedisMemoryGi = v),
        };
        foreach (var (name, set) in setters)
        {
            foreach (var value in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            {
                yield return new object[] { name, set, value };
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidSpecs))]
    public void Should_Reject_Non_Positive_Or_Non_Finite_Spec(string name, Action<DeployOptions, double> set, double value)
    {
        var o = CreateOptions();
        set(o, value);

        var act = () => ComposeGenerator.Validate(o);

        act.Should().Throw<ArgumentException>().WithMessage($"*{name}*");
    }

    [Fact]
    public void Should_Accept_Small_Positive_Spec()
    {
        var o = CreateOptions();
        o.RedisCpu = 0.25;

        var act = () => ComposeGenerator.Validate(o);

        act.Should().NotThrow();
    }
}
