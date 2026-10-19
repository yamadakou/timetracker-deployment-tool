namespace TTDeployTool.Common;

public class SimpleLogger : IDeploymentLogger
{
    private readonly bool _verbose;
    private readonly TextWriter? _out;
    private readonly TextWriter? _err;

    /// <param name="verbose">true の場合 Verbose ログおよび例外のスタックトレースを出力する</param>
    /// <param name="output">出力先（省略時は Console.Out。テストでの出力検証用）</param>
    /// <param name="error">エラーの出力先（省略時は Console.Error。テストでの出力検証用）</param>
    public SimpleLogger(bool verbose, TextWriter? output = null, TextWriter? error = null)
    {
        _verbose = verbose;
        _out = output;
        _err = error;
    }

    private TextWriter Out => _out ?? Console.Out;
    private TextWriter Err => _err ?? Console.Error;

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
        Err.WriteLine($"[ERROR] {message}");
        Console.ResetColor();
    }

    /// <summary>
    /// 例外をエラーとして出力する。Verbose 時はスタックトレースも出力する。
    /// </summary>
    public void Exception(Exception ex)
    {
        Error($"エラー: {ex.Message}");
        if (_verbose)
        {
            Err.WriteLine(ex.ToString());
        }
    }
}
