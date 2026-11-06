namespace NodeWatch;

/// <summary>
/// Minimal logger. Under systemd the output goes to the journal, and the "&lt;N&gt;" prefix sets the line's priority
/// (SyslogLevelPrefix), so <c>journalctl -u nodewatch -p warning</c> works. Interactive runs print without the prefix.
/// </summary>
internal static class Log
{
    public static bool Verbose { get; set; }
    public static bool SyslogPrefix { get; set; } = Console.IsOutputRedirected;
    private static readonly object Gate = new();

    public static void Debug(string message)
    {
        if (Verbose) Write(7, "DEBUG", message);
    }

    public static void Info(string message) => Write(6, "INFO", message);
    public static void Warn(string message) => Write(4, "WARN", message);
    public static void Error(string message) => Write(3, "ERROR", message);

    private static void Write(int priority, string level, string message)
    {
        lock (Gate)
        {
            if (SyslogPrefix) Console.Out.WriteLine($"<{priority}>{message}");
            else Console.Out.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-5} {message}");
            Console.Out.Flush();
        }
    }
}
