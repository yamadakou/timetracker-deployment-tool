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
    public void Parse_Should_Reject_Missing_Value_At_End_Of_Arguments()
    {
        var act = () => CliArgs.Parse(new[] { "--resource-group", "rg", "--subscription" });
        act.Should().Throw<ArgumentException>().WithMessage("*--subscription*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Get_Should_Return_Null_For_Blank_Value(string value)
    {
        var cli = CliArgs.Parse(new[] { $"--location={value}" });

        cli.Get("--location").Should().BeNull();
        cli.Get("--location", "japaneast").Should().Be("japaneast");
    }

    [Fact]
    public void GetDouble_Should_Return_Null_When_Not_Specified()
    {
        CliArgs.Parse(Array.Empty<string>()).GetDouble("--tt-cpu").Should().BeNull();
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

    [Theory]
    [InlineData(new string[0], CliCommand.Deploy)]
    [InlineData(new[] { "deploy" }, CliCommand.Deploy)]
    [InlineData(new[] { "delete" }, CliCommand.Delete)]
    [InlineData(new[] { "DELETE" }, CliCommand.Delete)]
    public void Parse_Should_Read_Command(string[] args, CliCommand expected)
    {
        CliArgs.Parse(args).Command.Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]       // 数値は列挙値として解釈しない
    [InlineData("destroy")]
    public void Parse_Should_Reject_Unknown_Command(string command)
    {
        var act = () => CliArgs.Parse(new[] { command });
        act.Should().Throw<ArgumentException>().WithMessage($"*{command}*");
    }

    [Fact]
    public void Parse_Should_Reject_Command_After_Options()
    {
        var act = () => CliArgs.Parse(new[] { "--subscription", "sub", "delete" });
        act.Should().Throw<ArgumentException>().WithMessage("*delete*");
    }

    [Fact]
    public void Parse_Should_Read_Delete_Options()
    {
        var cli = CliArgs.Parse(new[]
        {
            "delete", "--subscription", "sub", "--resource-group", "rg", "--app-name", "demo", "--auth-mode", "azure-cli",
            "--include-untagged", "--yes", "--dry-run", "false", "--verbose",
        });

        cli.Get("--subscription").Should().Be("sub");
        cli.Get("--app-name").Should().Be("demo");
        cli.GetBool("--include-untagged").Should().BeTrue();
        cli.GetBool("--yes").Should().BeTrue();
        cli.GetBool("--dry-run").Should().BeFalse();
    }

    [Theory]
    [InlineData("delete", "--db-password", "delete")]
    [InlineData("delete", "--tt-cpu", "delete")]
    [InlineData("deploy", "--yes", "deploy")]
    [InlineData("deploy", "--include-untagged", "deploy")]
    public void Parse_Should_Reject_Option_Of_Other_Command(string command, string option, string expectedCommand)
    {
        var act = () => CliArgs.Parse(new[] { command, option, "x" });
        act.Should().Throw<ArgumentException>().WithMessage($"*'{option}' は {expectedCommand} では使えません*");
    }

    [Fact]
    public void Parse_Should_Reject_Unknown_Option_For_Delete()
    {
        var act = () => CliArgs.Parse(new[] { "delete", "--unknown", "x" });
        act.Should().Throw<ArgumentException>().WithMessage("*不明なオプション*--unknown*");
    }

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "--persist-db" }, true)]
    [InlineData(new[] { "--persist-db", "true" }, true)]
    [InlineData(new[] { "--persist-db=false" }, false)]
    public void GetBoolOrNull_Should_Return_Null_Only_When_Not_Specified(string[] args, bool? expected)
    {
        CliArgs.Parse(args).GetBoolOrNull("--persist-db").Should().Be(expected);
    }

    [Fact]
    public void Keep_Db_Data_Should_Be_A_Delete_Flag_Only()
    {
        CliArgs.Parse(["delete", "--keep-db-data"]).GetBool("--keep-db-data").Should().BeTrue();
        var act = () => CliArgs.Parse(["deploy", "--keep-db-data"]);
        act.Should().Throw<ArgumentException>().WithMessage("*'--keep-db-data' は deploy では使えません*");
    }
}
