using System.Runtime.InteropServices;
using Frost.Engine.Capture;
using Frost.Engine.Diagnostics;

namespace Frost.Engine.Windows;

/// <summary>
/// Windows process host for the Engine.
/// </summary>
/// <remarks>
/// Normal launch is tray-resident with no console. The diagnostic verbs
/// (<c>--soak</c>, and the encoder probes added in Phase 2) attach to the parent
/// console so they can be driven from a terminal.
/// </remarks>
internal static partial class EngineHost
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    internal static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            // The resident tray Engine. Wired up in Phase 4 (hotkeys + IPC) and
            // Phase 8 (tray icon); until then there is nothing to sit resident
            // for, so say so rather than spinning.
            return RunDiagnostic(log =>
            {
                log.Info("Frost Engine has no resident mode yet. Try --soak [minutes] or --help.");
                return 0;
            });
        }

        return args[0] switch
        {
            "--soak" => RunDiagnostic(log => Soak(args, log)),
            "--displays" => RunDiagnostic(ListDisplays),
            "--windows" => RunDiagnostic(ListWindows),
            "--help" or "-h" or "/?" => RunDiagnostic(PrintUsage),
            _ => RunDiagnostic(log =>
            {
                log.Error($"Unknown argument '{args[0]}'.");
                PrintUsage(log);
                return 1;
            }),
        };
    }

    private static int Soak(string[] args, IEngineLog log)
    {
        var minutes = 10.0;
        if (args.Length > 1 && !double.TryParse(args[1], out minutes))
        {
            log.Error($"'{args[1]}' is not a number of minutes.");
            return 1;
        }

        if (minutes is <= 0 or > 720)
        {
            log.Error("Soak duration must be between 0 and 720 minutes.");
            return 1;
        }

        var config = new CaptureConfiguration
        {
            Target = CaptureTarget.PrimaryMonitor,
            TargetFps = 60,
        };

        return CaptureSoakTest.Run(config, TimeSpan.FromMinutes(minutes), log);
    }

    private static int ListDisplays(IEngineLog log)
    {
        foreach (var display in DisplayEnumerator.Displays())
        {
            log.Info(
                $"{display.DeviceName}  {display.Width}x{display.Height}  " +
                $"{display.FriendlyName}{(display.IsPrimary ? "  (primary)" : string.Empty)}");
        }

        return 0;
    }

    private static int ListWindows(IEngineLog log)
    {
        foreach (var window in DisplayEnumerator.Windows())
        {
            log.Info($"0x{window.Handle:X}  [{window.ProcessName}]  {window.Title}");
        }

        return 0;
    }

    private static int PrintUsage(IEngineLog log)
    {
        log.Info(
            """
            Frost.Engine.exe — capture/encode background process.

              (no arguments)      run resident (tray)
              --soak [minutes]    capture soak test, default 10 minutes; exit 0 if memory is flat
              --displays          list capture-able displays
              --windows           list capture-able windows
              --help              this text
            """);
        return 0;
    }

    /// <summary>
    /// Runs a console verb. The Engine is a WinExe so that the resident process
    /// has no console window; a diagnostic run borrows the caller's.
    /// </summary>
    private static int RunDiagnostic(Func<IEngineLog, int> body)
    {
        AttachConsole(AttachParentProcess);
        var log = new ConsoleEngineLog(LogLevel.Debug);

        try
        {
            return body(log);
        }
        catch (Exception ex)
        {
            log.Error("Engine failed.", ex);
            return 1;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);
}
