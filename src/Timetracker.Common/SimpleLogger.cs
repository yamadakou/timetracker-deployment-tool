namespace Timetracker.Common;

public class SimpleLogger
{
    private readonly bool _verbose;

    public SimpleLogger(bool verbose)
    {
        _verbose = verbose;
    }

    public void Info(string message)
    {
        Console.WriteLine($"[INFO] {message}");
    }

    public void Warn(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARN] {message}");
        Console.ResetColor();
    }

    public void Success(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[SUCCESS] {message}");
        Console.ResetColor();
    }

    public void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {message}");
        Console.ResetColor();
    }
}
