namespace Frost.Engine;

/// <summary>
/// Entry point for the always-on background Engine process (tray only).
/// The Engine owns capture, encode, the ring buffer, hotkeys and the IPC server;
/// it must keep running when the Shell is closed, hung or crashed.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
#if WINDOWS
        return Frost.Engine.Windows.EngineHost.Run(args);
#else
        Console.Error.WriteLine(
            "Frost.Engine requires Windows 10 1903 or later: capture uses " +
            "Windows.Graphics.Capture and encoding uses Media Foundation hardware MFTs.");
        return 1;
#endif
    }
}
