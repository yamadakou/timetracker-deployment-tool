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

    [Fact]
    public void Verbose_Should_Be_Written_Only_When_Verbose()
    {
        var verboseOutput = new StringWriter();
        new SimpleLogger(true, verboseOutput, new StringWriter()).Verbose("detail");
        var quietOutput = new StringWriter();
        new SimpleLogger(false, quietOutput, new StringWriter()).Verbose("detail");

        verboseOutput.ToString().Should().Contain("[VERBOSE] detail");
        quietOutput.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Info_Warn_Success_Should_Be_Written_To_Output()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var logger = new SimpleLogger(false, output, error);

        logger.Info("info");
        logger.Warn("warn");
        logger.Success("success");

        output.ToString().Should().Contain("[INFO] info").And.Contain("[WARN] warn").And.Contain("[SUCCESS] success");
        error.ToString().Should().BeEmpty();
    }
}

/// <summary>出力先を省略した場合に Console に書くことの確認（Console を差し替えるため並列実行しない）。</summary>
[Collection(ConsoleRedirectCollection.Name)]
public class SimpleLoggerConsoleTests
{
    [Fact]
    public void Should_Write_To_Console_When_Writers_Are_Omitted()
    {
        using var console = new ConsoleRedirect();

        var logger = new SimpleLogger(false);
        logger.Info("to-out");
        logger.Error("to-err");

        console.Output.Should().Contain("[INFO] to-out");
        console.Error.Should().Contain("[ERROR] to-err");
    }
}
