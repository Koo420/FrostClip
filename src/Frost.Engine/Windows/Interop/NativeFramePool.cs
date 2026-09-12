using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Frost.Engine.Windows.Interop;

/// <summary>
/// Raw-COM view of a <see cref="Direct3D11CaptureFramePool"/>, used by the
/// capture thread so that pulling a frame allocates nothing on the managed heap.
/// </summary>
/// <remarks>
/// Vtable layout of <c>IDirect3D11CaptureFramePool</c> (WinRT interfaces begin
/// with IInspectable's six slots): 6 Recreate, 7 TryGetNextFrame,
/// 8 add_FrameArrived, 9 remove_FrameArrived, 10 CreateCaptureSession,
/// 11 get_DispatcherQueue.
/// <para>
/// We poll <c>TryGetNextFrame</c> from our own paced thread rather than
/// subscribing to <c>FrameArrived</c>. Two reasons: the projected event handler
/// allocates a wrapper per frame, and polling on a waitable timer is exactly the
/// frame pacing the ring buffer wants anyway.
/// </para>
/// </remarks>
internal sealed unsafe class NativeFramePool : IDisposable
{
    private const int TryGetNextFrameSlot = 7;

    private nint _pool;
    private readonly delegate* unmanaged[Stdcall]<nint, nint*, int> _tryGetNextFrame;

    internal NativeFramePool(Direct3D11CaptureFramePool pool)
    {
        _pool = WinRtAbi.AddRefDefaultInterface(pool);
        var vtable = *(nint**)_pool;
        _tryGetNextFrame = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[TryGetNextFrameSlot];
    }

    /// <summary>
    /// Pulls the next queued frame, if any. Allocation-free.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the compositor has produced nothing new —
    /// the normal case for a static screen, not an error.
    /// </returns>
    internal bool TryGetNextFrame(out NativeCaptureFrame frame)
    {
        nint framePtr;
        var hr = _tryGetNextFrame(_pool, &framePtr);

        if (hr < 0)
        {
            frame = default;
            Marshal.ThrowExceptionForHR(hr);
            return false;
        }

        if (framePtr == 0)
        {
            frame = default;
            return false;
        }

        frame = new NativeCaptureFrame(framePtr);
        return true;
    }

    public void Dispose() => ComHelpers.Release(ref _pool);
}

/// <summary>
/// One frame owned by the caller. <see cref="Dispose"/> closes it, which is what
/// hands the underlying surface back to the compositor's pool — skipping that
/// starves capture within a few frames.
/// </summary>
internal readonly unsafe struct NativeCaptureFrame : IDisposable
{
    private const int SurfaceSlot = 6;
    private const int SystemRelativeTimeSlot = 7;
    private const int ContentSizeSlot = 8;
    private const int CloseSlot = 6;

    private readonly nint _frame;

    internal NativeCaptureFrame(nint frame) => _frame = frame;

    internal bool IsValid => _frame != 0;

    /// <summary>Capture time on the QPC-derived monotonic clock, in 100ns ticks.</summary>
    internal long SystemRelativeTimeTicks
    {
        get
        {
            var vtable = *(nint**)_frame;
            var get = (delegate* unmanaged[Stdcall]<nint, long*, int>)vtable[SystemRelativeTimeSlot];
            long ticks;
            ComHelpers.ThrowIfFailed(get(_frame, &ticks), "IDirect3D11CaptureFrame::get_SystemRelativeTime");
            return ticks;
        }
    }

    /// <summary>
    /// Size of the actual content inside the (possibly larger) surface. A window
    /// that has been resized smaller keeps the bigger surface until the pool is
    /// recreated, so this — not the texture's own dimensions — is what to copy.
    /// </summary>
    internal (int Width, int Height) ContentSize
    {
        get
        {
            var vtable = *(nint**)_frame;
            var get = (delegate* unmanaged[Stdcall]<nint, int*, int>)vtable[ContentSizeSlot];
            var size = stackalloc int[2];
            ComHelpers.ThrowIfFailed(get(_frame, size), "IDirect3D11CaptureFrame::get_ContentSize");
            return (size[0], size[1]);
        }
    }

    /// <summary>
    /// The frame's pixels as an <c>ID3D11Texture2D*</c>. Caller releases.
    /// </summary>
    internal nint AcquireTexture()
    {
        var vtable = *(nint**)_frame;
        var getSurface = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[SurfaceSlot];

        nint surface;
        ComHelpers.ThrowIfFailed(getSurface(_frame, &surface), "IDirect3D11CaptureFrame::get_Surface");

        try
        {
            var access = ComHelpers.QueryInterface(
                surface, ComIids.IDirect3DDxgiInterfaceAccess, "IDirect3DDxgiInterfaceAccess");
            try
            {
                var accessVtable = *(nint**)access;
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)accessVtable[3];

                var iid = ComIids.ID3D11Texture2D;
                nint texture;
                ComHelpers.ThrowIfFailed(
                    getInterface(access, &iid, &texture),
                    "IDirect3DDxgiInterfaceAccess::GetInterface(ID3D11Texture2D)");
                return texture;
            }
            finally
            {
                ComHelpers.ReleaseRaw(access);
            }
        }
        finally
        {
            ComHelpers.ReleaseRaw(surface);
        }
    }

    public void Dispose()
    {
        if (_frame == 0)
        {
            return;
        }

        // Direct3D11CaptureFrame's IClosable::Close is what recycles the surface.
        var closable = ComHelpers.TryQueryInterface(_frame, ComIids.IClosable);
        if (closable != 0)
        {
            var vtable = *(nint**)closable;
            var close = (delegate* unmanaged[Stdcall]<nint, int>)vtable[CloseSlot];
            close(closable);
            ComHelpers.ReleaseRaw(closable);
        }

        ComHelpers.ReleaseRaw(_frame);
    }
}
