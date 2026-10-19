using Xunit;

namespace TTDeployTool.Controller.Cli.Tests;

/// <summary>Console.Out / Console.Error を差し替えるテストをまとめ、他のテストと並列に実行しない。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ConsoleRedirectCollection
{
    public const string Name = "Console redirect";
}

/// <summary>Console.Out / Console.Error を一時的に差し替え、書かれた内容を取得する。</summary>
public sealed class ConsoleRedirect : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public ConsoleRedirect()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
    }

    public string Output => _out.ToString();
    public string Error => _error.ToString();

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }
}
