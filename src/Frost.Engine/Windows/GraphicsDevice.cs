using System.Runtime.InteropServices;
using Frost.Engine.Capture;
using Frost.Engine.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace Frost.Engine.Windows;

/// <summary>
/// The D3D11 device the whole Engine shares: WGC composites into it, texture
/// copies happen on it, and the hardware encoder MFT is bound to it so encoded
/// frames never make a round trip through system memory.
/// </summary>
internal sealed unsafe partial class GraphicsDevice : IDisposable
{
    private readonly IEngineLog _log;
    private ID3D11Multithread? _multithread;
    private nint _winRtDevicePointer;

    private GraphicsDevice(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIAdapter1 adapter,
        string adapterDescription,
        IEngineLog log)
    {
        Device = device;
        Context = context;
        Adapter = adapter;
        AdapterDescription = adapterDescription;
        _log = log;
    }

    internal ID3D11Device Device { get; }

    /// <summary>
    /// Cached immediate context. Cached deliberately: fetching it per frame would
    /// allocate a wrapper on the capture thread.
    /// </summary>
    internal ID3D11DeviceContext Context { get; }

    internal IDXGIAdapter1 Adapter { get; }

    internal string AdapterDescription { get; }

    /// <summary>
    /// PCI vendor ID of the adapter capture runs on, so the encode stage can
    /// prefer an encoder on the same GPU and avoid a cross-adapter copy per frame.
    /// </summary>
    internal uint AdapterVendorId => Adapter.Description1.VendorId;

    /// <summary>
    /// The same device as a WinRT <c>IDirect3DDevice</c>, which is what
    /// <c>Direct3D11CaptureFramePool</c> takes.
    /// </summary>
    internal IDirect3DDevice WinRtDevice { get; private set; } = null!;

    /// <summary>
    /// Creates a device on the adapter driving <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// On a hybrid laptop the game may be rendering on the discrete GPU while the
    /// desktop is composited by the integrated one. WGC handles that for us, but
    /// putting our device on the adapter that owns the output keeps the capture
    /// copy local and lets us pick that adapter's hardware encoder later.
    /// </remarks>
    internal static GraphicsDevice Create(CaptureTarget target, IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var (adapter, description) = SelectAdapter(target, log);

        try
        {
            // BgraSupport is mandatory: WGC surfaces are B8G8R8A8.
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

            ID3D11Device? device;
            ID3D11DeviceContext? context;
            var hr = D3D11.D3D11CreateDevice(
                adapter, DriverType.Unknown, flags, levels,
                out device, out context);

            if (hr.Failure)
            {
                // VideoSupport is only needed for the hardware colour conversion
                // in the encode stage; retry without it so capture still works on
                // drivers that refuse the flag.
                log.Warn($"D3D11CreateDevice with VideoSupport failed (0x{hr.Code:X8}); retrying without it.");
                hr = D3D11.D3D11CreateDevice(
                    adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
                    out device, out context);
            }

            hr.CheckError();

            var instance = new GraphicsDevice(device!, context!, adapter, description, log);
            instance.Initialise();
            log.Info($"D3D11 device created on '{description}' (feature level {device!.FeatureLevel}).");
            return instance;
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
    }

    private void Initialise()
    {
        // WGC delivers frames on a compositor thread while our capture thread is
        // issuing copies on the same device. Without multithread protection that
        // is a data race inside the D3D11 runtime.
        _multithread = Device.QueryInterface<ID3D11Multithread>();
        _multithread.SetMultithreadProtected(true);

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();

        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winRtDevice);
        Interop.ComHelpers.ThrowIfFailed(hr, "CreateDirect3D11DeviceFromDXGIDevice");

        try
        {
            // FromAbi takes its own reference, so ours is released below. This is
            // the same ownership dance the official WGC samples do.
            WinRtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winRtDevice);
        }
        finally
        {
            Marshal.Release(winRtDevice);
        }

        _winRtDevicePointer = 0;
    }

    /// <summary>
    /// Takes the device-wide lock. Held only around the handful of D3D calls that
    /// touch shared state; never around anything that waits.
    /// </summary>
    internal void EnterDeviceLock() => _multithread?.Enter();

    internal void LeaveDeviceLock() => _multithread?.Leave();

    private static (IDXGIAdapter1 Adapter, string Description) SelectAdapter(CaptureTarget target, IEngineLog log)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        IDXGIAdapter1? fallback = null;
        string fallbackDescription = "unknown adapter";

        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var description = adapter.Description1;
            var name = description.Description;

            // Skip the software renderer: a "hardware encoding only" app has no
            // business running its capture device on WARP.
            var isSoftware = (description.Flags & AdapterFlags.Software) != 0;

            if (isSoftware)
            {
                adapter.Dispose();
                continue;
            }

            var matches = target.Kind switch
            {
                CaptureTargetKind.Monitor => OwnsMonitor(adapter, target.Handle, target.DeviceName),
                CaptureTargetKind.PrimaryMonitor => OwnsPrimaryMonitor(adapter),
                _ => false,
            };

            if (matches)
            {
                log.Debug($"Adapter '{name}' drives the capture target.");
                fallback?.Dispose();
                return (adapter, name);
            }

            if (fallback is null)
            {
                fallback = adapter;
                fallbackDescription = name;
            }
            else
            {
                adapter.Dispose();
            }
        }

        if (fallback is not null)
        {
            // Window capture, or a target we could not attribute to an output:
            // the first hardware adapter is the right default.
            return (fallback, fallbackDescription);
        }

        throw new InvalidOperationException(
            "No hardware graphics adapter was found. Frost needs a GPU with a hardware video encoder.");
    }

    private static bool OwnsMonitor(IDXGIAdapter1 adapter, nint hMonitor, string? deviceName)
    {
        for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
        {
            using (output)
            {
                var description = output.Description;
                if ((hMonitor != 0 && description.Monitor == hMonitor) ||
                    (deviceName is not null && string.Equals(description.DeviceName, deviceName, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool OwnsPrimaryMonitor(IDXGIAdapter1 adapter)
    {
        var primary = MonitorFromPoint(default, MonitorDefaultToPrimary);
        return primary != 0 && OwnsMonitor(adapter, primary, null);
    }

    public void Dispose()
    {
        // The WinRT device wrapper holds its own reference to the underlying
        // device; dropping it before the D3D objects keeps the teardown ordered.
        WinRtDevice = null!;
        if (_winRtDevicePointer != 0)
        {
            Marshal.Release(_winRtDevicePointer);
            _winRtDevicePointer = 0;
        }

        _multithread?.Dispose();
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
        _log.Debug("D3D11 device disposed.");
    }

    private const uint MonitorDefaultToPrimary = 1;

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
