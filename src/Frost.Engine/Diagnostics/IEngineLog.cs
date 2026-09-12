namespace Frost.Engine.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Minimal logging seam for the Engine.
/// </summary>
/// <remarks>
/// Deliberately not <c>ILogger</c>: structured logging with params arrays and
/// interpolated-string handlers allocates, and the Engine has threads that are
/// not allowed to. The rule is that capture and encode threads log only on state
/// changes and errors, never per frame.
/// </remarks>
public interface IEngineLog
{
    bool IsEnabled(LogLevel level);

    void Log(LogLevel level, string message, Exception? exception = null);
}

public static class EngineLogExtensions
{
    public static void Info(this IEngineLog log, string message) => log.Log(LogLevel.Info, message);

    public static void Warn(this IEngineLog log, string message, Exception? ex = null) =>
        log.Log(LogLevel.Warning, message, ex);

    public static void Error(this IEngineLog log, string message, Exception? ex = null) =>
        log.Log(LogLevel.Error, message, ex);

    public static void Debug(this IEngineLog log, string message)
    {
        if (log.IsEnabled(LogLevel.Debug))
        {
            log.Log(LogLevel.Debug, message);
        }
    }
}

/// <summary>Discards everything. Default so nothing is forced to wire up logging.</summary>
public sealed class NullEngineLog : IEngineLog
{
    public static NullEngineLog Instance { get; } = new();

    public bool IsEnabled(LogLevel level) => false;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
    }
}
