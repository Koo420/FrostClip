using System.Diagnostics;
using Frost.Engine.Capture;
using Frost.Engine.Diagnostics;
using Frost.Engine.Pipeline;
using Frost.Engine.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Frost.Engine.Windows;

/// <summary>
/// Captures a monitor or window with Windows.Graphics.Capture into a
/// pre-allocated D3D11 texture pool, on its own paced thread.
/// </summary>
/// <remarks>
/// WGC rather than BitBlt or Desktop Duplication: it is the only route that
/// survives fullscreen-exclusive games and hybrid-GPU laptops without taking
/// frames away from the game, and it needs no injection into the game process.
/// <para>
/// The thread polls <c>TryGetNextFrame</c> on a high-resolution waitable timer
/// instead of subscribing to <c>FrameArrived</c>. Polling is both cheaper — the
/// projected event handler would allocate a wrapper object per frame — and
/// exactly the pacing the ring buffer needs. Everything the loop touches is
/// allocated before the thread starts.
/// </para>
/// </remarks>
internal sealed class WgcCaptureSource : ICaptureSource, IFrameCopier
{
    private readonly CaptureConfiguration _config;
    private readonly GraphicsDevice _device;
    private readonly IEngineLog _log;
    private readonly FramePacer _pacer;

    // Pre-resolved managed wrappers, indexed by pool slot, so the copy call in
    // the loop never does a dictionary lookup or allocates a wrapper.
    private ID3D11Texture2D[] _slotTextures = [];

    // WGC rotates through a fixed, small set of surfaces, so caching wrappers by
    // raw pointer converges after the first few frames and then allocates nothing.
    private readonly nint[] _sourceHandles;
    private readonly ID3D11Texture2D?[] _sourceTextures;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private NativeFramePool? _nativePool;
    private D3D11TextureAllocator? _allocator;
    private TexturePool? _texturePool;
    private WaitableTimer? _timer;
    private Thread? _thread;

    private FrameRouter? _router;

    private volatile bool _stopRequested;
    private volatile bool _itemClosed;
    private volatile int _pendingWidth;
    private volatile int _pendingHeight;

    private int _width;
    private int _height;

    internal WgcCaptureSource(CaptureConfiguration config, GraphicsDevice device, IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(log);
        config.Validate();

        _config = config;
        _device = device;
        _log = log;
        _pacer = new FramePacer(config.TargetFps, config.MaxFrameInterval);

        // One wrapper slot per WGC buffer, plus slack for a pool recreate.
        var sourceCacheSize = config.CaptureQueueDepth + 2;
        _sourceHandles = new nint[sourceCacheSize];
        _sourceTextures = new ID3D11Texture2D?[sourceCacheSize];
    }

    public (int Width, int Height) FrameSize => (_width, _height);

    public long FramesEmitted => _router?.FramesEmitted ?? 0;

    public long FramesDropped => _router?.FramesDropped ?? 0;

    public bool IsRunning => _thread is { IsAlive: true } && !_stopRequested;

    /// <summary>True once the captured window or display went away.</summary>
    internal bool TargetClosed => _itemClosed;

    /// <summary>Filler frames emitted because the screen was static.</summary>
    internal long FillerFrames => _router?.FillerFrames ?? 0;

    /// <summary>Texture pool slots free right now. Persistently zero means the encoder is behind.</summary>
    internal int PoolSlotsAvailable => _texturePool?.Available ?? 0;

    /// <summary>
    /// Pool that currently owns the frames being emitted. Consumers need this to
    /// return slots, and it changes if the capture target is resized.
    /// </summary>
    internal TexturePool? CurrentTexturePool => _texturePool;

    public void Start(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (_thread is not null)
        {
            throw new InvalidOperationException("Capture is already started.");
        }

        _item = CaptureItemFactory.Create(_config.Target, _log);
        _item.Closed += OnItemClosed;

        _width = _item.Size.Width;
        _height = _item.Size.Height;
        if (_width <= 0 || _height <= 0)
        {
            throw new InvalidOperationException(
                $"Capture target reported a {_width}x{_height} size; nothing to record.");
        }

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            _config.CaptureQueueDepth,
            new SizeInt32 { Width = _width, Height = _height });

        _nativePool = new NativeFramePool(_framePool);

        _allocator = new D3D11TextureAllocator(_device);
        _texturePool = new TexturePool(_allocator, _width, _height, _config.TexturePoolSize);
        ResolveSlotTextures();
        _router = new FrameRouter(_texturePool, _pacer, this, sink, _width, _height);

        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _config.CaptureCursor;

        // Win11 21H2 and later let us drop the capture border. Older builds never
        // drew one, so absence of the API is not a problem.
        if (ApiInformation.IsPropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try
            {
                _session.IsBorderRequired = _config.ShowCaptureBorder;
            }
            catch (Exception ex)
            {
                // Requires a capability the user can revoke in Privacy settings;
                // a border we did not want is not worth failing a recording over.
                _log.Warn("Could not turn off the capture border; continuing with it.", ex);
            }
        }

        _timer = WaitableTimer.Create();
        if (!_timer.IsHighResolution)
        {
            _log.Warn("High-resolution waitable timers are unavailable; frame pacing will be coarse.");
        }

        _stopRequested = false;
        _thread = new Thread(CaptureLoop)
        {
            Name = "frost-capture",
            IsBackground = true,

            // Above normal so frame pacing survives a busy machine, but not
            // Highest: this thread must never outrank the game's render thread.
            Priority = ThreadPriority.AboveNormal,
        };

        _session.StartCapture();
        _thread.Start();

        _log.Info(
            $"Capture started: {_width}x{_height} @ {_config.TargetFps}fps, " +
            $"{_config.TexturePoolSize} pooled textures, {_config.CaptureQueueDepth} WGC buffers.");
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopRequested = true;
        _timer?.Cancel();

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _log.Warn("Capture thread did not stop within 5s.");
        }

        _thread = null;

        try
        {
            _session?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn("Disposing the capture session threw.", ex);
        }

        _session = null;

        _nativePool?.Dispose();
        _nativePool = null;

        _framePool?.Dispose();
        _framePool = null;

        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
            _item = null;
        }

        ReleaseSourceCache();

        _texturePool?.Dispose();
        _texturePool = null;
        _slotTextures = [];

        _allocator?.Dispose();
        _allocator = null;

        _timer?.Dispose();
        _timer = null;

        _log.Info(
            $"Capture stopped: {FramesEmitted} frames emitted " +
            $"({FillerFrames} filler), {FramesDropped} dropped.");
        _router = null;
    }

    private void CaptureLoop()
    {
        var pool = _nativePool!;
        var timer = _timer!;
        var intervalTicks = _pacer.IntervalTicks;
        var nextWakeTicks = MonotonicTicks();

        try
        {
            while (!_stopRequested)
            {
                if (_itemClosed)
                {
                    _log.Info("Capture target closed; ending capture loop.");
                    break;
                }

                if (_pendingWidth != 0)
                {
                    RecreateForSize(_pendingWidth, _pendingHeight);
                    _pendingWidth = 0;
                }

                PumpOneTick(pool);

                nextWakeTicks += intervalTicks;
                var now = MonotonicTicks();

                if (nextWakeTicks <= now)
                {
                    // We are behind (a stall, or the encoder held the device
                    // lock). Resynchronise rather than spinning to catch up.
                    nextWakeTicks = now + intervalTicks;
                    continue;
                }

                timer.Wait(nextWakeTicks - now);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Capture thread faulted; capture has stopped.", ex);
            _stopRequested = true;
        }
    }

    /// <summary>
    /// One poll: take the newest queued frame if there is one, otherwise let the
    /// router consider a filler. Allocation-free once the source-wrapper cache
    /// has warmed up.
    /// </summary>
    private void PumpOneTick(NativeFramePool pool)
    {
        var router = _router!;
        var newest = default(NativeCaptureFrame);

        // Drain to the newest frame. Encoding a stale frame while a newer one
        // sits in the queue would add latency for no benefit, so older frames are
        // dropped, and counted.
        while (pool.TryGetNextFrame(out var frame))
        {
            if (newest.IsValid)
            {
                newest.Dispose();
                router.NoteSourceDrop();
            }

            newest = frame;
        }

        if (!newest.IsValid)
        {
            router.OfferFillerIfDue(MonotonicTicks());
            return;
        }

        try
        {
            var (contentWidth, contentHeight) = newest.ContentSize;

            if (contentWidth != _width || contentHeight != _height)
            {
                // The window was resized or the display mode changed. Note it and
                // let the loop recreate the pools; this frame is not usable.
                if (contentWidth > 0 && contentHeight > 0)
                {
                    _pendingWidth = contentWidth;
                    _pendingHeight = contentHeight;
                }

                router.NoteSourceDrop();
                return;
            }

            var sourcePointer = newest.AcquireTexture();
            _pendingSourceTexture = ResolveSourceTexture(sourcePointer);
            router.OfferCaptured(sourcePointer, newest.SystemRelativeTimeTicks);
        }
        finally
        {
            // Closing the frame is what returns the surface to the compositor.
            newest.Dispose();
        }
    }

    /// <summary>
    /// Wrapper for the surface the current <see cref="PumpOneTick"/> is copying.
    /// The router hands back a raw pointer, and we already resolved a cached
    /// managed wrapper for it, so this avoids a second lookup in the copy call.
    /// Capture-thread-only state.
    /// </summary>
    private ID3D11Texture2D? _pendingSourceTexture;

    void IFrameCopier.CopyFromSource(nint sourceTexture, nint destinationTexture, int width, int height)
    {
        _ = width;
        _ = height;
        var source = _pendingSourceTexture ?? ResolveSourceTexture(sourceTexture);
        CopyTexture(source, _slotTextures[SlotIndexOf(destinationTexture)]);
    }

    void IFrameCopier.CopyPooled(nint sourceTexture, nint destinationTexture, int width, int height)
    {
        _ = width;
        _ = height;
        CopyTexture(
            _slotTextures[SlotIndexOf(sourceTexture)],
            _slotTextures[SlotIndexOf(destinationTexture)]);
    }

    /// <summary>
    /// Whole-texture GPU copy.
    /// </summary>
    /// <remarks>
    /// <c>CopyResource</c> rather than <c>CopySubresourceRegion</c>: source and
    /// destination are always identical in size and format here — frames whose
    /// content size does not match the pool geometry are skipped in
    /// <see cref="PumpOneTick"/> and trigger a pool recreate — so there is no
    /// region to describe, and the call takes no struct arguments at all.
    /// The invariant is checked once per distinct source surface in
    /// <see cref="ResolveSourceTexture"/>, not per frame.
    /// </remarks>
    private void CopyTexture(ID3D11Texture2D source, ID3D11Texture2D destination)
    {
        // The device lock is held for exactly this copy. WGC composites on its own
        // thread and the encoder shares the device, so the copy has to be
        // serialised; nothing that can wait happens inside the lock.
        _device.EnterDeviceLock();
        try
        {
            _device.Context.CopyResource(destination, source);
        }
        finally
        {
            _device.LeaveDeviceLock();
        }
    }

    /// <summary>Pool slot for a texture handle. Linear over a handful of slots.</summary>
    private int SlotIndexOf(nint handle)
    {
        var pool = _texturePool!;
        for (var i = 0; i < pool.Capacity; i++)
        {
            if (pool.HandleAt(i) == handle)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Texture 0x{handle:X} does not belong to the capture pool.");
    }

    /// <summary>
    /// Managed wrapper for a WGC surface, cached by pointer. WGC cycles through a
    /// fixed set of surfaces, so this stops allocating after the first few frames.
    /// </summary>
    private ID3D11Texture2D ResolveSourceTexture(nint pointer)
    {
        for (var i = 0; i < _sourceHandles.Length; i++)
        {
            if (_sourceHandles[i] == pointer)
            {
                // Already holding a reference through the cached wrapper; drop the
                // one AcquireTexture just handed us.
                ComHelpers.ReleaseRaw(pointer);
                return _sourceTextures[i]!;
            }
        }

        for (var i = 0; i < _sourceHandles.Length; i++)
        {
            if (_sourceHandles[i] == 0)
            {
                // The wrapper takes over the reference from AcquireTexture.
                var texture = new ID3D11Texture2D(pointer);
                VerifyCopyCompatible(texture);
                _sourceHandles[i] = pointer;
                _sourceTextures[i] = texture;
                return texture;
            }
        }

        // More distinct surfaces than expected (a driver with a deeper rotation).
        // Rebuilding the cache is the safe response; it happens at most rarely.
        _log.Debug("Capture surface cache full; rebuilding.");
        ReleaseSourceCache();
        var rebuilt = new ID3D11Texture2D(pointer);
        VerifyCopyCompatible(rebuilt);
        _sourceHandles[0] = pointer;
        _sourceTextures[0] = rebuilt;
        return rebuilt;
    }

    /// <summary>
    /// Confirms a newly seen WGC surface really matches the pool geometry, so the
    /// per-frame <c>CopyResource</c> is safe. Runs once per distinct surface — a
    /// few times per session — not per frame.
    /// </summary>
    private void VerifyCopyCompatible(ID3D11Texture2D source)
    {
        var description = source.Description;
        if (description.Width != (uint)_width ||
            description.Height != (uint)_height ||
            description.Format != Format.B8G8R8A8_UNorm)
        {
            throw new InvalidOperationException(
                $"Capture surface is {description.Width}x{description.Height} {description.Format}, " +
                $"but the texture pool is {_width}x{_height} B8G8R8A8_UNorm.");
        }
    }

    private void ReleaseSourceCache()
    {
        for (var i = 0; i < _sourceHandles.Length; i++)
        {
            _sourceTextures[i]?.Dispose();
            _sourceTextures[i] = null;
            _sourceHandles[i] = 0;
        }
    }

    private void ResolveSlotTextures()
    {
        var pool = _texturePool!;
        var allocator = _allocator!;
        _slotTextures = new ID3D11Texture2D[pool.Capacity];
        for (var i = 0; i < pool.Capacity; i++)
        {
            _slotTextures[i] = allocator.Resolve(pool.HandleAt(i));
        }
    }

    private void RecreateForSize(int width, int height)
    {
        _log.Info($"Capture target resized to {width}x{height}; recreating pools.");

        _width = width;
        _height = height;

        ReleaseSourceCache();

        _framePool!.Recreate(
            _device.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            _config.CaptureQueueDepth,
            new SizeInt32 { Width = width, Height = height });

        _nativePool!.Dispose();
        _nativePool = new NativeFramePool(_framePool);

        var previousPool = _texturePool!;
        _texturePool = null;
        previousPool.Dispose();

        _allocator!.Dispose();
        _allocator = new D3D11TextureAllocator(_device);
        _texturePool = new TexturePool(_allocator, width, height, _config.TexturePoolSize);
        ResolveSlotTextures();
        _router!.Rebind(_texturePool, width, height);
        _pendingSourceTexture = null;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => _itemClosed = true;

    /// <summary>
    /// Monotonic time in 100ns ticks on the same base as WGC's
    /// <c>SystemRelativeTime</c> (both derive from QPC). Split into whole seconds
    /// plus remainder so the multiply cannot overflow on a long-running machine.
    /// </summary>
    private static long MonotonicTicks()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var frequency = Stopwatch.Frequency;

        if (frequency == TimeSpan.TicksPerSecond)
        {
            return timestamp;
        }

        var seconds = timestamp / frequency;
        var remainder = timestamp % frequency;
        return (seconds * TimeSpan.TicksPerSecond) + (remainder * TimeSpan.TicksPerSecond / frequency);
    }

    public void Dispose() => Stop();
}
