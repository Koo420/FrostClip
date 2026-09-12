namespace Frost.Engine.Encoding;

/// <summary>
/// One encoded frame, as a window into a pre-allocated byte arena.
/// </summary>
/// <remarks>
/// A struct referring to arena storage rather than a <c>byte[]</c> per frame:
/// at 60fps a per-frame array would be 60 allocations a second forever, and the
/// ring buffer would spend its life collecting them. The arena owns the bytes;
/// this describes where they are.
/// </remarks>
public readonly struct EncodedSample
{
    public EncodedSample(
        long offset,
        int length,
        long timestampTicks,
        long durationTicks,
        bool isKeyFrame,
        long sequenceNumber)
    {
        Offset = offset;
        Length = length;
        TimestampTicks = timestampTicks;
        DurationTicks = durationTicks;
        IsKeyFrame = isKeyFrame;
        SequenceNumber = sequenceNumber;
    }

    /// <summary>Offset of the encoded bytes within the owning arena.</summary>
    public long Offset { get; }

    public int Length { get; }

    /// <summary>Presentation time in 100ns ticks, on the capture clock.</summary>
    public long TimestampTicks { get; }

    public long DurationTicks { get; }

    /// <summary>
    /// True for an IDR frame. A clip must start on one of these to be decodable
    /// without re-encoding, so this is what the ring buffer trims against.
    /// </summary>
    public bool IsKeyFrame { get; }

    public long SequenceNumber { get; }

    public long EndTimestampTicks => TimestampTicks + DurationTicks;

    public bool IsEmpty => Length == 0;
}
