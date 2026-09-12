using Frost.Engine.Capture;

namespace Frost.Engine.Pipeline;

/// <summary>
/// The GPU copy the router needs, factored out of the platform implementation.
/// </summary>
public interface IFrameCopier
{
    /// <summary>Copies freshly captured content into a pooled texture.</summary>
    void CopyFromSource(nint sourceTexture, nint destinationTexture, int width, int height);

    /// <summary>Copies one pooled texture into another, for a filler frame.</summary>
    void CopyPooled(nint sourceTexture, nint destinationTexture, int width, int height);
}

/// <summary>
/// Decides what happens to each captured frame: pace it, get it a pooled
/// texture, copy it, hand it to the sink, and account for everything that got
/// dropped along the way.
/// </summary>
/// <remarks>
/// Lives here rather than in the Windows capture implementation so the policy —
/// which is where the interesting bugs are — is testable without a GPU or a
/// compositor. The platform code is left with WGC plumbing and one copy call.
/// <para>
/// Allocation-free on every path. Called only from the capture thread.
/// </para>
/// </remarks>
public sealed class FrameRouter
{
    private readonly FramePacer _pacer;
    private readonly IFrameCopier _copier;
    private readonly IFrameSink _sink;

    private TexturePool _pool;
    private int _width;
    private int _height;
    private int _lastEmittedSlot = -1;
    private long _sequence;
    private long _framesEmitted;
    private long _framesDropped;

    public FrameRouter(
        TexturePool pool,
        FramePacer pacer,
        IFrameCopier copier,
        IFrameSink sink,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(pacer);
        ArgumentNullException.ThrowIfNull(copier);
        ArgumentNullException.ThrowIfNull(sink);

        _pool = pool;
        _pacer = pacer;
        _copier = copier;
        _sink = sink;
        _width = width;
        _height = height;
    }

    public long FramesEmitted => Interlocked.Read(ref _framesEmitted);

    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Frames emitted that carried no new content.</summary>
    public long FillerFrames => _pacer.FillerCount;

    /// <summary>Last sequence number handed to the sink.</summary>
    public long LastSequence => Interlocked.Read(ref _sequence);

    /// <summary>
    /// Offers a newly captured frame. Returns <see langword="true"/> if it
    /// reached the sink.
    /// </summary>
    public bool OfferCaptured(nint sourceTexture, long timestampTicks)
    {
        if (_pacer.Consider(timestampTicks) == PacingDecision.DropTooSoon)
        {
            Interlocked.Increment(ref _framesDropped);
            return false;
        }

        if (!_pool.TryRent(out var slot))
        {
            // Everything is still downstream: the encoder is behind. Dropping is
            // the right answer; blocking here would stall the compositor.
            Interlocked.Increment(ref _framesDropped);
            return false;
        }

        _copier.CopyFromSource(sourceTexture, slot.Handle, _width, _height);
        return Publish(slot, timestampTicks, isFiller: false);
    }

    /// <summary>
    /// Emits a filler frame if the gap since the last emitted frame has reached
    /// the ceiling, so the encoded timeline never contains an arbitrarily long
    /// sample.
    /// </summary>
    public bool OfferFillerIfDue(long nowTicks)
    {
        if (_lastEmittedSlot < 0)
        {
            return false;
        }

        if (!_pacer.TryPlanFiller(nowTicks, out var timestampTicks))
        {
            return false;
        }

        var previousSlot = _lastEmittedSlot;

        // Ask for the slot we emitted last. Only this thread writes to the pool,
        // so if that slot is free again its pixels are still the previous frame's
        // and the filler needs no GPU work at all.
        if (!_pool.TryRentPreferring(previousSlot, out var slot))
        {
            Interlocked.Increment(ref _framesDropped);
            return false;
        }

        if (slot.SlotIndex != previousSlot)
        {
            _copier.CopyPooled(_pool.HandleAt(previousSlot), slot.Handle, _width, _height);
        }

        return Publish(slot, timestampTicks, isFiller: true);
    }

    /// <summary>Counts a frame the platform layer threw away before the router saw it.</summary>
    public void NoteSourceDrop() => Interlocked.Increment(ref _framesDropped);

    /// <summary>
    /// Swaps in a new pool and geometry after the capture target changed size.
    /// </summary>
    public void Rebind(TexturePool pool, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pool);

        _pool = pool;
        _width = width;
        _height = height;
        _lastEmittedSlot = -1;
        _pacer.Reset();
    }

    private bool Publish(PooledTexture slot, long timestampTicks, bool isFiller)
    {
        var frame = new CapturedFrame(
            slot.Handle,
            slot.SlotIndex,
            _width,
            _height,
            timestampTicks,
            Interlocked.Increment(ref _sequence),
            isFiller);

        if (_sink.TryAccept(frame))
        {
            _lastEmittedSlot = slot.SlotIndex;
            Interlocked.Increment(ref _framesEmitted);
            return true;
        }

        _pool.Return(slot.SlotIndex);
        Interlocked.Increment(ref _framesDropped);
        return false;
    }
}
