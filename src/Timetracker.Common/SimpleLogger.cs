using System;

namespace Timetracker.Common;

public class SimpleLogger
{
    private readonly bool _verbose;
    public SimpleLogger(bool verbose = false) => _verbose = verbose;

    private static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static void Write(string level, string message, bool toError = false)
    {
        var line = $"[{level}] {Timestamp()} {message}";
        if (toError)
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
    }

    public void Info(string message)    => Write("INFO",    message);
    public void Warn(string message)    => Write("WARN",    message);
    public void Error(string message)   => Write("ERROR",   message, toError: true);
    public void Success(string message) => Write("SUCCESS", message);
    public void Verbose(string message)
    {
        if (_verbose) Write("VERBOSE", message);
    }
}
