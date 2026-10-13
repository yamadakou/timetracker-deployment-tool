using System.Globalization;
using FluentAssertions;
using Xunit;

namespace Timetracker.Controller.Cli.Tests;

public class CliArgsTests
{
    [Fact]
    public void Parse_Should_Read_Value_Options_And_Accept_Deploy_Command()
    {
        var a = CliArgs.Parse(new[] { "deploy", "--subscription", "sub1", "--resource-group", "rg1" });

        a.Get("--subscription", "").Should().Be("sub1");
        a.Get("--resource-group", "").Should().Be("rg1");
        a.Get("--location", "japaneast").Should().Be("japaneast");
    }

    [Fact]
    public void Parse_Should_Be_Case_Insensitive_For_Option_Names()
    {
        CliArgs.Parse(new[] { "--Subscription", "sub1" }).Get("--subscription", "").Should().Be("sub1");
    }

    [Theory]
    [InlineData(new[] { "--dry-run" }, true)]
    [InlineData(new[] { "--dry-run", "true" }, true)]
    [InlineData(new[] { "--dry-run", "false" }, false)]
    [InlineData(new[] { "--dry-run", "FALSE" }, false)]
    [InlineData(new[] { "--dry-run=false" }, false)]
    [InlineData(new[] { "--dry-run=true" }, true)]
    [InlineData(new string[0], false)]
    public void Parse_Should_Handle_Bool_Flag_Values(string[] args, bool expected)
    {
        CliArgs.Parse(args).GetBool("--dry-run").Should().Be(expected);
    }

    [Fact]
    public void Parse_Bool_Flag_Should_Not_Consume_Following_Option()
    {
        var a = CliArgs.Parse(new[] { "--dry-run", "--verbose" });
        a.GetBool("--dry-run").Should().BeTrue();
        a.GetBool("--verbose").Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_Allow_Values_Starting_With_Dashes_Via_Equals_Syntax()
    {
        CliArgs.Parse(new[] { "--db-password=--Secret1" }).Get("--db-password", "").Should().Be("--Secret1");
    }

    [Fact]
    public void Parse_Should_Reject_Unknown_Option()
    {
        var act = () => CliArgs.Parse(new[] { "--tt-cpuu", "1" });
        act.Should().Throw<ArgumentException>().WithMessage("*--tt-cpuu*");
    }

    [Fact]
    public void Parse_Should_Reject_Unexpected_Positional_Argument()
    {
        var act = () => CliArgs.Parse(new[] { "deploy", "extra" });
        act.Should().Throw<ArgumentException>().WithMessage("*extra*");
    }

    [Fact]
    public void Parse_Should_Reject_Missing_Value()
    {
        var act = () => CliArgs.Parse(new[] { "--subscription", "--resource-group", "rg" });
        act.Should().Throw<ArgumentException>().WithMessage("*--subscription*");
    }

    [Fact]
    public void Parse_Should_Reject_Invalid_Bool_Value_With_Equals()
    {
        var act = () => CliArgs.Parse(new[] { "--dry-run=yes" });
        act.Should().Throw<ArgumentException>().WithMessage("*--dry-run*");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parse_Should_Detect_Help(string arg)
    {
        CliArgs.Parse(new[] { arg }).IsHelp.Should().BeTrue();
    }

    [Fact]
    public void HelpText_Should_List_All_Options()
    {
        foreach (var opt in CliArgs.ValueOptions.Concat(CliArgs.BoolFlags))
        {
            CliArgs.HelpText.Should().Contain(opt);
        }
    }

    [Theory]
    [InlineData("de-DE")] // 小数点がカンマのロケール
    [InlineData("ja-JP")]
    [InlineData("")]      // InvariantCulture
    public void GetDouble_Should_Parse_Dot_Decimal_Regardless_Of_Culture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CliArgs.Parse(new[] { "--tt-cpu", "0.75" }).GetDouble("--tt-cpu", 1.0).Should().Be(0.75);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void GetDouble_Should_Return_Default_When_Not_Specified()
    {
        CliArgs.Parse(Array.Empty<string>()).GetDouble("--tt-cpu", 0.75).Should().Be(0.75);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0,75")]
    [InlineData("1,000")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("NaN")]
    public void GetDouble_Should_Throw_On_Invalid_Value(string value)
    {
        var act = () => CliArgs.Parse(new[] { "--tt-cpu", value }).GetDouble("--tt-cpu", 0.75);
        act.Should().Throw<ArgumentException>().WithMessage("*--tt-cpu*");
    }
}
