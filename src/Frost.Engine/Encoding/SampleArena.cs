namespace Frost.Engine.Encoding;

/// <summary>
/// Fixed-size circular byte storage for encoded samples, with a matching ring of
/// descriptors.
/// </summary>
/// <remarks>
/// The whole point is that the byte storage is allocated once. Encoded frames
/// vary from a few hundred bytes to a few hundred kilobytes, and allocating a
/// <c>byte[]</c> per frame at 60fps would keep the GC busy forever and make the
/// ring buffer's memory use unpredictable — the opposite of what a
/// "trailing 30 seconds" guarantee needs.
/// <para>
/// A sample may wrap past the end of the buffer, so reads go through
/// <see cref="CopyTo"/> / <see cref="Read"/> rather than handing out a single
/// span. Offsets are monotonically increasing logical positions; the physical
/// index is the offset masked to the capacity, which is why capacity is rounded
/// up to a power of two.
/// </para>
/// <para>
/// Not thread-safe. Callers that share one across threads (the muxer's write
/// queue) hold a lock for the copy and do their I/O outside it.
/// </para>
/// </remarks>
public sealed class SampleArena
{
    private readonly byte[] _bytes;
    private readonly long _byteMask;
    private readonly EncodedSample[] _descriptors;
    private readonly int _descriptorMask;

    private long _writeOffset;
    private long _readOffset;
    private long _head;
    private long _tail;
    private long _sequence;

    /// <param name="byteCapacity">
    /// Bytes of storage. Rounded up to a power of two. Size it from
    /// <see cref="BitrateCalculator.BytesFor"/> for the duration to be held.
    /// </param>
    /// <param name="maxSamples">
    /// Descriptor slots. Rounded up to a power of two. At 60fps a 60-second
    /// buffer needs 3,600, so this is normally the frame rate times the duration
    /// with headroom.
    /// </param>
    public SampleArena(long byteCapacity, int maxSamples)
    {
        if (byteCapacity < 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCapacity), byteCapacity, "Arena needs at least 4KB.");
        }

        if (byteCapacity > 1L << 34)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCapacity), byteCapacity, "Arena is capped at 16GB.");
        }

        if (maxSamples < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSamples), maxSamples, "Need at least 2 slots.");
        }

        var byteCapacityRounded = RoundUpToPowerOfTwo(byteCapacity);
        var descriptorCapacity = (int)RoundUpToPowerOfTwo(maxSamples);

        _bytes = new byte[byteCapacityRounded];
        _byteMask = byteCapacityRounded - 1;
        _descriptors = new EncodedSample[descriptorCapacity];
        _descriptorMask = descriptorCapacity - 1;
    }

    /// <summary>Bytes of storage, after rounding.</summary>
    public long ByteCapacity => _bytes.Length;

    /// <summary>Descriptor slots, after rounding.</summary>
    public int SampleCapacity => _descriptors.Length;

    /// <summary>Samples currently held.</summary>
    public int Count => (int)(_head - _tail);

    /// <summary>Bytes currently held.</summary>
    public long BytesUsed => _writeOffset - _readOffset;

    public bool IsEmpty => _head == _tail;

    /// <summary>Timestamp of the oldest sample held, or 0 when empty.</summary>
    public long OldestTimestampTicks => IsEmpty ? 0 : _descriptors[(int)(_tail & _descriptorMask)].TimestampTicks;

    /// <summary>End timestamp of the newest sample held, or 0 when empty.</summary>
    public long NewestEndTimestampTicks =>
        IsEmpty ? 0 : _descriptors[(int)((_head - 1) & _descriptorMask)].EndTimestampTicks;

    /// <summary>Wall-clock span covered by what is held.</summary>
    public TimeSpan HeldDuration =>
        IsEmpty ? TimeSpan.Zero : TimeSpan.FromTicks(NewestEndTimestampTicks - OldestTimestampTicks);

    /// <summary>
    /// Appends a sample, dropping the oldest samples as needed to make room.
    /// </summary>
    /// <remarks>
    /// This is the ring-buffer behaviour: the newest frame always wins, because a
    /// rolling buffer that refused new frames when full would stop recording
    /// exactly when it matters.
    /// </remarks>
    public bool TryAppendEvicting(
        ReadOnlySpan<byte> data,
        long timestampTicks,
        long durationTicks,
        bool isKeyFrame,
        out EncodedSample appended)
    {
        if (data.Length == 0 || data.Length > _bytes.Length)
        {
            // A single sample larger than the whole arena can never be held; that
            // is a sizing error, not something to silently drop half of.
            appended = default;
            return false;
        }

        while (Count == _descriptors.Length || _bytes.Length - BytesUsed < data.Length)
        {
            DropOldest();
        }

        appended = Append(data, timestampTicks, durationTicks, isKeyFrame);
        return true;
    }

    /// <summary>
    /// Appends a sample, refusing when full rather than dropping anything.
    /// </summary>
    /// <remarks>
    /// The queue behaviour, for the file writer: silently discarding the oldest
    /// frames of a recording the user asked for would corrupt it, so a full queue
    /// is a refusal the caller accounts for.
    /// </remarks>
    public bool TryAppend(
        ReadOnlySpan<byte> data,
        long timestampTicks,
        long durationTicks,
        bool isKeyFrame,
        out EncodedSample appended)
    {
        if (data.Length == 0 ||
            Count == _descriptors.Length ||
            _bytes.Length - BytesUsed < data.Length)
        {
            appended = default;
            return false;
        }

        appended = Append(data, timestampTicks, durationTicks, isKeyFrame);
        return true;
    }

    /// <summary>The <paramref name="index"/>th oldest sample held.</summary>
    public EncodedSample this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Only {Count} samples are held.");
            }

            return _descriptors[(int)((_tail + index) & _descriptorMask)];
        }
    }

    /// <summary>Oldest sample held.</summary>
    public EncodedSample Peek()
    {
        if (IsEmpty)
        {
            throw new InvalidOperationException("The arena is empty.");
        }

        return _descriptors[(int)(_tail & _descriptorMask)];
    }

    /// <summary>
    /// Copies a sample's bytes into <paramref name="destination"/>, joining the
    /// two halves when the sample wraps past the end of the arena.
    /// </summary>
    public void CopyTo(in EncodedSample sample, Span<byte> destination)
    {
        if (destination.Length < sample.Length)
        {
            throw new ArgumentException(
                $"Destination is {destination.Length} bytes; the sample is {sample.Length}.",
                nameof(destination));
        }

        if (sample.Offset < _readOffset || sample.EndOffset() > _writeOffset)
        {
            throw new InvalidOperationException(
                "That sample has already been evicted from the arena.");
        }

        var start = (int)(sample.Offset & _byteMask);
        var firstChunk = Math.Min(sample.Length, _bytes.Length - start);

        _bytes.AsSpan(start, firstChunk).CopyTo(destination);

        if (firstChunk < sample.Length)
        {
            _bytes.AsSpan(0, sample.Length - firstChunk).CopyTo(destination[firstChunk..]);
        }
    }

    /// <summary>
    /// The bytes of a sample as a single span when it does not wrap, otherwise
    /// <see langword="false"/> so the caller uses <see cref="CopyTo"/>.
    /// </summary>
    public bool TryGetContiguous(in EncodedSample sample, out ReadOnlySpan<byte> data)
    {
        var start = (int)(sample.Offset & _byteMask);

        if (sample.Offset >= _readOffset &&
            sample.EndOffset() <= _writeOffset &&
            start + sample.Length <= _bytes.Length)
        {
            data = _bytes.AsSpan(start, sample.Length);
            return true;
        }

        data = default;
        return false;
    }

    /// <summary>Reads a sample into <paramref name="scratch"/> and returns the filled span.</summary>
    public ReadOnlySpan<byte> Read(in EncodedSample sample, Span<byte> scratch)
    {
        if (TryGetContiguous(sample, out var contiguous))
        {
            return contiguous;
        }

        CopyTo(sample, scratch);
        return scratch[..sample.Length];
    }

    /// <summary>Removes the oldest sample.</summary>
    public EncodedSample DropOldest()
    {
        if (IsEmpty)
        {
            throw new InvalidOperationException("The arena is empty.");
        }

        var index = (int)(_tail & _descriptorMask);
        var sample = _descriptors[index];
        _descriptors[index] = default;
        _tail++;
        _readOffset = sample.Offset + sample.Length;

        if (_tail == _head)
        {
            // Fully drained: resynchronise the byte cursor so a long run cannot
            // leave the read and write offsets needlessly far apart.
            _readOffset = _writeOffset;
        }

        return sample;
    }

    /// <summary>Discards everything.</summary>
    public void Clear()
    {
        Array.Clear(_descriptors);
        _head = 0;
        _tail = 0;
        _readOffset = 0;
        _writeOffset = 0;
    }

    private EncodedSample Append(
        ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
    {
        var offset = _writeOffset;
        var start = (int)(offset & _byteMask);
        var firstChunk = Math.Min(data.Length, _bytes.Length - start);

        data[..firstChunk].CopyTo(_bytes.AsSpan(start, firstChunk));

        if (firstChunk < data.Length)
        {
            data[firstChunk..].CopyTo(_bytes.AsSpan(0, data.Length - firstChunk));
        }

        _writeOffset = offset + data.Length;

        var sample = new EncodedSample(
            offset, data.Length, timestampTicks, durationTicks, isKeyFrame, ++_sequence);

        _descriptors[(int)(_head & _descriptorMask)] = sample;
        _head++;

        return sample;
    }

    private static long RoundUpToPowerOfTwo(long value)
    {
        var result = 1L;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }
}

/// <summary>Arena-offset helpers for <see cref="EncodedSample"/>.</summary>
public static class EncodedSampleExtensions
{
    /// <summary>One past the last byte of the sample, in arena offsets.</summary>
    public static long EndOffset(this in EncodedSample sample) => sample.Offset + sample.Length;
}
