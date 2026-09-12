using System.Runtime.InteropServices;
using Frost.Engine.Capture;
using Frost.Engine.Diagnostics;
using Frost.Engine.Windows.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;

namespace Frost.Engine.Windows;

/// <summary>
/// Turns a <see cref="CaptureTarget"/> into a <see cref="GraphicsCaptureItem"/>.
/// </summary>
/// <remarks>
/// Two routes. On Windows 11 (and late Windows 10 builds) the projection exposes
/// <c>TryCreateFromDisplayId</c>/<c>TryCreateFromWindowId</c>, which is
/// preferable because it needs no hand-written IIDs. Older builds — back to
/// Windows 10 1903, where WGC first shipped — need
/// <c>IGraphicsCaptureItemInterop</c>. Neither route involves a picker UI: Frost
/// captures what the user configured, with no per-launch prompt.
/// </remarks>
internal static unsafe class CaptureItemFactory
{
    internal static GraphicsCaptureItem Create(CaptureTarget target, IEngineLog log)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows.Graphics.Capture is not available on this system. " +
                "Frost needs Windows 10 version 1903 or later.");
        }

        var item = target.Kind switch
        {
            CaptureTargetKind.Window => CreateForWindow(target.Handle, log),
            CaptureTargetKind.Monitor => CreateForMonitor(ResolveMonitor(target), log),
            CaptureTargetKind.PrimaryMonitor => CreateForMonitor(PrimaryMonitor(), log),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "Unknown capture target kind."),
        };

        log.Info($"Capture item '{item.DisplayName}' at {item.Size.Width}x{item.Size.Height}.");
        return item;
    }

    private static GraphicsCaptureItem CreateForMonitor(nint hMonitor, IEngineLog log)
    {
        if (hMonitor == 0)
        {
            throw new InvalidOperationException("Could not resolve the capture target to a monitor.");
        }

        if (ApiInformation.IsMethodPresent(
                "Windows.Graphics.Capture.GraphicsCaptureItem", "TryCreateFromDisplayId"))
        {
            var item = GraphicsCaptureItem.TryCreateFromDisplayId(new DisplayId((ulong)hMonitor));
            if (item is not null)
            {
                return item;
            }

            log.Warn("TryCreateFromDisplayId returned null; falling back to IGraphicsCaptureItemInterop.");
        }

        return CreateViaInterop(hMonitor, forMonitor: true);
    }

    private static GraphicsCaptureItem CreateForWindow(nint hWnd, IEngineLog log)
    {
        if (hWnd == 0 || !IsWindow(hWnd))
        {
            throw new InvalidOperationException(
                $"Window 0x{hWnd:X} no longer exists. Pick the capture target again.");
        }

        if (ApiInformation.IsMethodPresent(
                "Windows.Graphics.Capture.GraphicsCaptureItem", "TryCreateFromWindowId"))
        {
            var item = GraphicsCaptureItem.TryCreateFromWindowId(new global::Windows.UI.WindowId((ulong)hWnd));
            if (item is not null)
            {
                return item;
            }

            log.Warn("TryCreateFromWindowId returned null; falling back to IGraphicsCaptureItemInterop.");
        }

        return CreateViaInterop(hWnd, forMonitor: false);
    }

    private static GraphicsCaptureItem CreateViaInterop(nint handle, bool forMonitor)
    {
        var interop = GetCaptureItemInterop();
        try
        {
            var vtable = *(nint**)interop;

            // IGraphicsCaptureItemInterop: 3 CreateForWindow, 4 CreateForMonitor.
            var slot = forMonitor ? 4 : 3;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[slot];

            var iid = ComIids.IGraphicsCaptureItem;
            nint raw;
            ComHelpers.ThrowIfFailed(
                create(interop, handle, &iid, &raw),
                forMonitor
                    ? "IGraphicsCaptureItemInterop::CreateForMonitor"
                    : "IGraphicsCaptureItemInterop::CreateForWindow");

            try
            {
                // FromAbi takes its own reference.
                return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(raw);
            }
            finally
            {
                Marshal.Release(raw);
            }
        }
        finally
        {
            ComHelpers.ReleaseRaw(interop);
        }
    }

    private static nint GetCaptureItemInterop()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        var hr = WindowsCreateString(className, className.Length, out var hstring);
        ComHelpers.ThrowIfFailed(hr, "WindowsCreateString");

        try
        {
            var iid = ComIids.IGraphicsCaptureItemInterop;
            hr = RoGetActivationFactory(hstring, &iid, out var factory);
            ComHelpers.ThrowIfFailed(hr, "RoGetActivationFactory(IGraphicsCaptureItemInterop)");
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static nint ResolveMonitor(CaptureTarget target)
    {
        if (target.Handle != 0)
        {
            return target.Handle;
        }

        if (target.DeviceName is { Length: > 0 } deviceName)
        {
            var resolved = DisplayEnumerator.FindMonitorByDeviceName(deviceName);
            if (resolved != 0)
            {
                return resolved;
            }
        }

        // The configured display is gone (unplugged, or a dock changed). Falling
        // back to the primary display is better than refusing to record.
        return PrimaryMonitor();
    }

    private static nint PrimaryMonitor() => MonitorFromWindow(GetDesktopWindow(), MonitorDefaultToPrimary);

    private const uint MonitorDefaultToPrimary = 1;

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint activatableClassId, Guid* iid, out nint factory);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);
}
