using System;

namespace Timetracker.Common;

public class SimpleLogger
{
    private readonly bool _verbose;
    public SimpleLogger(bool verbose = false) => _verbose = verbose;

    private static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static void Write(string level, string message, bool toError = false)
    {
        var line = $