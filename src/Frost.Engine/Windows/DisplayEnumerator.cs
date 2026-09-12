using System.Runtime.InteropServices;
using System.Text;
using Frost.Engine.Capture;

namespace Frost.Engine.Windows;

/// <summary>A display the user can pick as a capture target.</summary>
public sealed record DisplayInfo(
    string DeviceName,
    string FriendlyName,
    nint MonitorHandle,
    int Width,
    int Height,
    bool IsPrimary);

/// <summary>A top-level window the user can pick as a capture target.</summary>
public sealed record WindowInfo(nint Handle, string Title, string ProcessName);

/// <summary>
/// Enumerates capture targets for the settings UI. Called on demand from the IPC
/// thread, never from capture or encode.
/// </summary>
internal static partial class DisplayEnumerator
{
    internal static List<DisplayInfo> Displays()
    {
        var displays = new List<DisplayInfo>(4);

        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                var deviceName = info.DeviceNameString;
                displays.Add(new DisplayInfo(
                    DeviceName: deviceName,
                    FriendlyName: FriendlyNameFor(deviceName) ?? deviceName,
                    MonitorHandle: monitor,
                    Width: info.Monitor.Right - info.Monitor.Left,
                    Height: info.Monitor.Bottom - info.Monitor.Top,
                    IsPrimary: (info.Flags & MonitorInfoFPrimary) != 0));
            }

            return true;
        }, 0);

        return displays;
    }

    internal static nint FindMonitorByDeviceName(string deviceName)
    {
        foreach (var display in Displays())
        {
            if (string.Equals(display.DeviceName, deviceName, StringComparison.Ordinal))
            {
                return display.MonitorHandle;
            }
        }

        return 0;
    }

    /// <summary>
    /// Visible, non-cloaked top-level windows with a title — the set a user would
    /// recognise as "a window", which is also the set WGC can actually capture.
    /// </summary>
    internal static List<WindowInfo> Windows()
    {
        var windows = new List<WindowInfo>(32);
        var title = new StringBuilder(512);

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || GetAncestor(hWnd, GaRoot) != hWnd)
            {
                return true;
            }

            // Cloaked windows are the invisible UWP shells that otherwise litter
            // any window list on Windows 10+.
            if (DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            if ((GetWindowLongPtrW(hWnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
            {
                return true;
            }

            title.Clear();
            var length = GetWindowTextW(hWnd, title, title.Capacity);
            if (length == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var processId);
            windows.Add(new WindowInfo(hWnd, title.ToString(), ProcessNameFor(processId)));
            return true;
        }, 0);

        return windows;
    }

    internal static CaptureTarget DefaultTarget() => CaptureTarget.PrimaryMonitor;

    private static string ProcessNameFor(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string? FriendlyNameFor(string deviceName)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevicesW(deviceName, 0, ref device, 0)
            ? device.DeviceStringValue
            : null;
    }

    private const int MonitorInfoFPrimary = 1;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const uint GaRoot = 2;
    private const uint DwmwaCloaked = 14;

    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);

    private delegate bool EnumWindowsProc(nint hWnd, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint deviceIndex, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetWindowLongPtrW(nint hWnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hWnd, uint attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public char[] DeviceName;

        public string DeviceNameString =>
            DeviceName is null ? string.Empty : new string(DeviceName).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public char[] DeviceName;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public char[] DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public char[] DeviceID;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public char[] DeviceKey;

        public string DeviceStringValue =>
            DeviceString is null ? string.Empty : new string(DeviceString).TrimEnd('\0');
    }
}
