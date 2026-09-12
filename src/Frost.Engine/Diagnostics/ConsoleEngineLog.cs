namespace Frost.Engine.Diagnostics;

/// <summary>
/// Writes to stderr. For the diagnostic entry points (the capture smoke test,
/// encoder enumeration) — not for the tray-resident Engine, whose logging goes
/// to a file.
/// </summary>
public sealed class ConsoleEngineLog(LogLevel minimum = LogLevel.Info) : IEngineLog
{
    private readonly object _gate = new();

    public bool IsEnabled(LogLevel level) => level >= minimum;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        lock (_gate)
        {
            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{Tag(level)}] {message}");
            if (exception is not null)
            {
                Console.Error.WriteLine(exception);
            }
        }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "dbg",
        LogLevel.Info => "inf",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };
}
