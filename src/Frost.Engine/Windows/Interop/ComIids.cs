using System.Runtime.InteropServices;

namespace Frost.Engine.Windows.Interop;

/// <summary>
/// Interface IDs used by the raw-COM parts of the capture path.
/// </summary>
/// <remarks>
/// Only interfaces that genuinely cannot be reached through the CsWinRT
/// projection belong here. Everywhere the projection can hand us the right
/// interface pointer we let it, because a wrong IID literal is a runtime failure
/// the compiler cannot catch.
/// </remarks>
internal static class ComIids
{
    /// <summary>Windows.Foundation.IClosable — how a WGC frame is returned to its pool.</summary>
    internal static readonly Guid IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    /// <summary>IDirect3DDxgiInterfaceAccess — bridges a WinRT surface to its DXGI/D3D11 object.</summary>
    internal static readonly Guid IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    /// <summary>ID3D11Texture2D.</summary>
    internal static readonly Guid ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>IGraphicsCaptureItemInterop — the Windows 10 way to build a capture item from an HMONITOR/HWND.</summary>
    internal static readonly Guid IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>Windows.Graphics.Capture.IGraphicsCaptureItem.</summary>
    internal static readonly Guid IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
}

/// <summary>Thin helpers over the COM calling convention used by the hot path.</summary>
internal static class ComHelpers
{
    internal static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
            throw new InvalidOperationException($"{what} failed with HRESULT 0x{hr:X8}.");
        }
    }

    /// <summary>
    /// <c>IUnknown::QueryInterface</c> on a raw pointer. Returns 0 on failure
    /// rather than throwing, for probe-style use.
    /// </summary>
    internal static unsafe nint TryQueryInterface(nint unknown, in Guid iid)
    {
        if (unknown == 0)
        {
            return 0;
        }

        var vtable = *(nint**)unknown;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];

        nint result;
        Guid local = iid;
        var hr = queryInterface(unknown, &local, &result);
        return hr >= 0 ? result : 0;
    }

    internal static unsafe nint QueryInterface(nint unknown, in Guid iid, string what)
    {
        var result = TryQueryInterface(unknown, iid);
        if (result == 0)
        {
            throw new InvalidOperationException($"QueryInterface for {what} failed.");
        }

        return result;
    }

    internal static unsafe void Release(ref nint unknown)
    {
        if (unknown == 0)
        {
            return;
        }

        var vtable = *(nint**)unknown;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        release(unknown);
        unknown = 0;
    }

    /// <summary>Non-allocating release for the per-frame path.</summary>
    internal static unsafe void ReleaseRaw(nint unknown)
    {
        if (unknown == 0)
        {
            return;
        }

        var vtable = *(nint**)unknown;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        release(unknown);
    }
}
