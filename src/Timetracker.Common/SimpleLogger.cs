namespace Timetracker.Common;

public class SimpleLogger
{
    private readonly bool _verbose;
    private readonly TextWriter? _out;

    /// <param name="verbose">true の場合 Verbose ログを出力する</param>
    /// <param name="output">出力先（省略時は Console.Out。テストでの出力検証用）</param>
    public SimpleLogger(bool verbose, TextWriter? output = null)
    {
        _verbose = verbose;
        _out = output;
    }

    private TextWriter Out => _out ?? Console.Out;

    public void Info(string message)
    {
        Out.WriteLine($"[INFO] {message}");
    }

    public void Verbose(string message)
    {
        if (_verbose)
        {
            Out.WriteLine($"[VERBOSE] {message}");
        }
    }

    public void Warn(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Out.WriteLine($"[WARN] {message}");
        Console.ResetColor();
    }

    public void Success(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Out.WriteLine($"[SUCCESS] {message}");
        Console.ResetColor();
    }

    public void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Out.WriteLine($"[ERROR] {message}");
        Console.ResetColor();
    }
}
