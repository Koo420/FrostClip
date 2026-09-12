namespace Frost.Engine.Capture;

/// <summary>
/// One frame on its way from capture to encode. A readonly struct passed by
/// <c>in</c> so that moving a frame through the pipeline costs nothing on the
/// managed heap.
/// </summary>
/// <remarks>
/// <see cref="Texture"/> is an opaque native handle (an <c>ID3D11Texture2D*</c> on
/// Windows). Portable code never dereferences it; it only routes it. The consumer
/// must hand <see cref="SlotIndex"/> back to the owning
/// <see cref="TexturePool"/> when it is done, or capture will starve.
/// </remarks>
public readonly struct CapturedFrame
{
    public CapturedFrame(
        nint texture,
        int slotIndex,
        int width,
        int height,
        long timestampTicks,
        long sequenceNumber,
        bool isFiller)
    {
        Texture = texture;
        SlotIndex = slotIndex;
        Width = width;
        Height = height;
        TimestampTicks = timestampTicks;
        SequenceNumber = sequenceNumber;
        IsFiller = isFiller;
    }

    /// <summary>Opaque GPU texture handle holding this frame's pixels.</summary>
    public nint Texture { get; }

    /// <summary>Pool slot to return once the frame has been consumed.</summary>
    public int SlotIndex { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Monotonic capture time in 100ns ticks, on the same clock for every frame
    /// in a session. Derived from WGC's <c>SystemRelativeTime</c> (QPC-based), not
    /// from wall time, so it never jumps when the system clock is adjusted.
    /// </summary>
    public long TimestampTicks { get; }

    /// <summary>Monotonically increasing per session. Gaps mean dropped frames.</summary>
    public long SequenceNumber { get; }

    /// <summary>
    /// True when this frame carries no new content and exists only to bound the
    /// gap between samples (see <see cref="CaptureConfiguration.MaxFrameInterval"/>).
    /// Downstream may encode it cheaply or extend the previous sample instead.
    /// </summary>
    public bool IsFiller { get; }

    public bool IsEmpty => Texture == 0;
}
