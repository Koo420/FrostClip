namespace Frost.Engine.Capture;

/// <summary>What the user picked to capture.</summary>
public enum CaptureTargetKind
{
    /// <summary>Whatever Windows currently reports as the primary display.</summary>
    PrimaryMonitor = 0,

    /// <summary>A specific display, identified by its DXGI output device name.</summary>
    Monitor = 1,

    /// <summary>A specific top-level window, identified by HWND.</summary>
    Window = 2,
}

/// <summary>
/// A capture target, in a form both processes can serialise and neither needs
/// Windows headers to describe.
/// </summary>
/// <param name="Kind">Monitor or window.</param>
/// <param name="DeviceName">
/// DXGI output device name (e.g. <c>\\.\DISPLAY1</c>) for <see cref="CaptureTargetKind.Monitor"/>.
/// </param>
/// <param name="Handle">HMONITOR or HWND, depending on <paramref name="Kind"/>.</param>
public readonly record struct CaptureTarget(
    CaptureTargetKind Kind,
    string? DeviceName = null,
    nint Handle = 0)
{
    public static CaptureTarget PrimaryMonitor { get; } = new(CaptureTargetKind.PrimaryMonitor);

    public static CaptureTarget ForMonitor(string deviceName, nint hMonitor) =>
        new(CaptureTargetKind.Monitor, deviceName, hMonitor);

    public static CaptureTarget ForWindow(nint hWnd) =>
        new(CaptureTargetKind.Window, null, hWnd);
}
