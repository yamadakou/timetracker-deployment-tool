using FluentAssertions;
using Timetracker.Common;
using Xunit;

namespace Timetracker.Controller.Cli.Tests;

public class SimpleLoggerTests
{
    private static Exception CreateThrownException()
    {
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void Error_Should_Be_Written_To_Error_Output()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        new SimpleLogger(false, output, error).Error("failed");

        error.ToString().Should().Contain("[ERROR] failed");
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Exception_Should_Write_Message_Only_When_Not_Verbose()
    {
        var error = new StringWriter();
        new SimpleLogger(false, new StringWriter(), error).Exception(CreateThrownException());

        error.ToString().Should().Contain("boom").And.NotContain(nameof(CreateThrownException));
    }

    [Fact]
    public void Exception_Should_Write_Stack_Trace_When_Verbose()
    {
        var error = new StringWriter();
        new SimpleLogger(true, new StringWriter(), error).Exception(CreateThrownException());

        error.ToString().Should().Contain("boom").And.Contain(nameof(CreateThrownException));
    }
}
