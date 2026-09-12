namespace Frost.Engine.Encoding;

/// <summary>
/// The rolling buffer of encoded frames that makes instant clipping possible.
/// </summary>
/// <remarks>
/// <para>Frames go in continuously and the oldest fall out, so at any moment the
/// buffer holds roughly the last <see cref="RingBufferOptions.MaxTrailingDuration"/>
/// of gameplay, already encoded. A hotkey press then only has to mux what is
/// already there — no encode, no re-encode, which is what makes the save feel
/// instant.</para>
///
/// <para><b>Keyframe alignment.</b> A clip must begin on a keyframe to be
/// decodable without re-encoding. So trimming keeps everything back to the last
/// keyframe at or before the requested window, and a snapshot always starts on
/// one. The consequence — worth stating plainly — is that a "15 second" clip can
/// be up to one keyframe interval longer, which is why the encoder's keyframe
/// interval defaults to two seconds.</para>
///
/// <para><b>Reading while recording.</b> Writing a clip out takes long enough
/// that the encoder will deliver more frames during it. Eviction is therefore
/// blocked from passing an open snapshot's start, and the arena carries headroom
/// so that costs nothing in practice. If a reader were slow enough to fill the
/// headroom, incoming samples are refused and counted rather than corrupting the
/// clip being written.</para>
///
/// <para>Thread-safe. The lock is held for descriptor bookkeeping and memcpy
/// only; muxing and disk I/O happen outside it.</para>
/// </remarks>
public sealed class EncodedSampleRing : IEncodedSampleSink
{
    private readonly object _gate = new();
    private readonly SampleArena _arena;
    private readonly RingBufferOptions _options;
    private readonly EncodedSample[] _snapshotScratch;

    private long _samplesWritten;
    private long _samplesRefused;
    private long _bytesWritten;
    private long _reservedFromSequence = long.MaxValue;
    private bool _snapshotOpen;

    public EncodedSampleRing(RingBufferOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _arena = new SampleArena(options.ArenaBytes, options.ArenaSamples);
        _snapshotScratch = new EncodedSample[_arena.SampleCapacity];
    }

    /// <summary>Longest clip this buffer was asked to be able to produce.</summary>
    public TimeSpan MaxTrailingDuration => _options.MaxTrailingDuration;

    /// <summary>
    /// Longest clip actually achievable. Lower than
    /// <see cref="MaxTrailingDuration"/> only when the memory cap binds.
    /// </summary>
    public TimeSpan EffectiveMaxTrailingDuration => _options.EffectiveMaxTrailingDuration;

    /// <summary>True when the memory cap, not the requested duration, is the limit.</summary>
    public bool IsLimitedByMemoryCap => _options.IsLimitedByMemoryCap;

    public long ArenaBytes => _arena.ByteCapacity;

    public int ArenaSamples => _arena.SampleCapacity;

    public long SamplesWritten => Interlocked.Read(ref _samplesWritten);

    /// <summary>
    /// Samples the buffer could not take. Non-zero means a clip write held the
    /// buffer longer than its headroom allowed, or a single sample was larger
    /// than the whole arena.
    /// </summary>
    public long SamplesRefused => Interlocked.Read(ref _samplesRefused);

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>Samples held right now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _arena.Count;
            }
        }
    }

    /// <summary>Bytes held right now — the buffer's real memory cost.</summary>
    public long BytesHeld
    {
        get
        {
            lock (_gate)
            {
                return _arena.BytesUsed;
            }
        }
    }

    /// <summary>Wall-clock span currently held, which is what a clip can draw on.</summary>
    public TimeSpan HeldDuration
    {
        get
        {
            lock (_gate)
            {
                return _arena.HeldDuration;
            }
        }
    }

    /// <summary>Encode-thread entry point. Never blocks on I/O, never allocates.</summary>
    public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
    {
        if (data.IsEmpty)
        {
            return false;
        }

        lock (_gate)
        {
            if (!MakeRoomFor(data.Length))
            {
                Interlocked.Increment(ref _samplesRefused);
                return false;
            }

            if (!_arena.TryAppend(data, timestampTicks, durationTicks, isKeyFrame, out _))
            {
                Interlocked.Increment(ref _samplesRefused);
                return false;
            }

            TrimToRetentionWindow();
        }

        Interlocked.Increment(ref _samplesWritten);
        Interlocked.Add(ref _bytesWritten, data.Length);
        return true;
    }

    /// <summary>
    /// Opens a view of the trailing <paramref name="duration"/>, starting at the
    /// last keyframe at or before that point.
    /// </summary>
    /// <remarks>
    /// While the returned snapshot is open the samples it covers will not be
    /// evicted, so the caller can mux them to disk without holding a lock.
    /// Dispose it as soon as the clip is written.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// If a snapshot is already open. Clip writes are serialised deliberately:
    /// two concurrent writers would each pin the buffer and together could
    /// outlast its headroom.
    /// </exception>
    public RingSnapshot OpenSnapshot(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Must be positive.");
        }

        lock (_gate)
        {
            if (_snapshotOpen)
            {
                throw new InvalidOperationException(
                    "A ring-buffer snapshot is already open. Clip writes are serialised.");
            }

            if (_arena.IsEmpty)
            {
                _snapshotOpen = true;
                return new RingSnapshot(this, _snapshotScratch, 0, TimeSpan.Zero, 0);
            }

            var endTicks = _arena.NewestEndTimestampTicks;
            var wantedStartTicks = endTicks - duration.Ticks;
            var startIndex = FindKeyFrameStartIndex(wantedStartTicks);

            var count = 0;
            for (var i = startIndex; i < _arena.Count; i++)
            {
                _snapshotScratch[count++] = _arena[i];
            }

            _reservedFromSequence = count > 0 ? _snapshotScratch[0].SequenceNumber : long.MaxValue;
            _snapshotOpen = true;

            var span = count > 0
                ? TimeSpan.FromTicks(_snapshotScratch[count - 1].EndTimestampTicks
                                     - _snapshotScratch[0].TimestampTicks)
                : TimeSpan.Zero;

            long bytes = 0;
            for (var i = 0; i < count; i++)
            {
                bytes += _snapshotScratch[i].Length;
            }

            return new RingSnapshot(this, _snapshotScratch, count, span, bytes);
        }
    }

    /// <summary>Reads one snapshot sample's bytes. Called by <see cref="RingSnapshot"/>.</summary>
    internal ReadOnlySpan<byte> ReadInto(EncodedSample sample, Span<byte> scratch)
    {
        lock (_gate)
        {
            _arena.CopyTo(sample, scratch);
        }

        return scratch[..sample.Length];
    }

    internal void CloseSnapshot()
    {
        lock (_gate)
        {
            _snapshotOpen = false;
            _reservedFromSequence = long.MaxValue;
        }
    }

    /// <summary>Discards everything, e.g. when capture restarts with new settings.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_snapshotOpen)
            {
                throw new InvalidOperationException("Cannot clear the ring while a snapshot is open.");
            }

            _arena.Clear();
        }
    }

    /// <summary>Evicts until <paramref name="length"/> bytes and a slot are free.</summary>
    private bool MakeRoomFor(int length)
    {
        if (length > _arena.ByteCapacity)
        {
            return false;
        }

        while (_arena.Count == _arena.SampleCapacity ||
               _arena.ByteCapacity - _arena.BytesUsed < length)
        {
            if (_arena.IsEmpty)
            {
                return false;
            }

            if (_arena.Peek().SequenceNumber >= _reservedFromSequence)
            {
                // The oldest sample is part of a clip currently being written.
                // Refusing the new frame is the only safe answer: evicting it
                // would corrupt the clip.
                return false;
            }

            _arena.DropOldest();
        }

        return true;
    }

    /// <summary>
    /// Drops samples older than the retention window, keeping back to the
    /// keyframe that a maximum-length clip would have to start on.
    /// </summary>
    private void TrimToRetentionWindow()
    {
        var retainTicks = (long)(_options.MaxTrailingDuration.Ticks * _options.HeadroomFactor);
        var cutoff = _arena.NewestEndTimestampTicks - retainTicks;

        while (_arena.Count > 1)
        {
            var oldest = _arena.Peek();

            if (oldest.EndTimestampTicks > cutoff ||
                oldest.SequenceNumber >= _reservedFromSequence)
            {
                return;
            }

            // Never drop a keyframe whose group is still inside the window: the
            // frames after it would be undecodable on their own.
            if (oldest.IsKeyFrame && !AnyLaterKeyFrameBefore(cutoff))
            {
                return;
            }

            _arena.DropOldest();
        }
    }

    private bool AnyLaterKeyFrameBefore(long cutoffTicks)
    {
        for (var i = 1; i < _arena.Count; i++)
        {
            var sample = _arena[i];
            if (sample.IsKeyFrame && sample.TimestampTicks <= cutoffTicks)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Index of the sample a clip should start at: the last keyframe at or before
    /// <paramref name="wantedStartTicks"/>, or the first keyframe held if the
    /// buffer does not reach back that far.
    /// </summary>
    private int FindKeyFrameStartIndex(long wantedStartTicks)
    {
        var best = -1;

        for (var i = 0; i < _arena.Count; i++)
        {
            var sample = _arena[i];

            if (!sample.IsKeyFrame)
            {
                continue;
            }

            if (sample.TimestampTicks <= wantedStartTicks)
            {
                // A later keyframe still inside the window is a better start:
                // it gives a clip closer to the requested length.
                best = i;
                continue;
            }

            // First keyframe past the requested start. Use it only if we have not
            // found an earlier one, in which case the clip is shorter than asked
            // for but still decodable.
            if (best < 0)
            {
                best = i;
            }

            break;
        }

        // No keyframe at all: the encoder has not produced one yet. Starting from
        // the oldest sample gives a file a decoder will refuse, so return the
        // whole buffer and let the caller see an empty-ish clip rather than
        // pretend. In practice the first encoded frame is always a keyframe.
        return best < 0 ? 0 : best;
    }
}

/// <summary>
/// A pinned view of the ring buffer's trailing samples, for writing a clip.
/// </summary>
/// <remarks>
/// Holds eviction back for the range it covers, so dispose it as soon as the clip
/// is written — the ring's headroom is what pays for the delay.
/// </remarks>
public sealed class RingSnapshot : IDisposable
{
    private readonly EncodedSampleRing _ring;
    private readonly EncodedSample[] _samples;
    private bool _disposed;

    internal RingSnapshot(
        EncodedSampleRing ring,
        EncodedSample[] samples,
        int count,
        TimeSpan duration,
        long totalBytes)
    {
        _ring = ring;
        _samples = samples;
        Count = count;
        Duration = duration;
        TotalBytes = totalBytes;
    }

    /// <summary>Samples covered, oldest first, starting on a keyframe.</summary>
    public int Count { get; }

    /// <summary>Wall-clock span covered. May exceed the request by up to one keyframe interval.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Encoded bytes covered — the size of the clip about to be written.</summary>
    public long TotalBytes { get; }

    public bool IsEmpty => Count == 0;

    /// <summary>Largest single sample, for sizing a read scratch buffer.</summary>
    public int LargestSampleLength
    {
        get
        {
            var largest = 0;
            for (var i = 0; i < Count; i++)
            {
                largest = Math.Max(largest, _samples[i].Length);
            }

            return largest;
        }
    }

    public EncodedSample this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Snapshot holds {Count} samples.");
            }

            return _samples[index];
        }
    }

    /// <summary>Copies a sample's bytes into <paramref name="scratch"/>.</summary>
    public ReadOnlySpan<byte> Read(int index, Span<byte> scratch)
    {
        var sample = this[index];
        return _ring.ReadInto(sample, scratch);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ring.CloseSnapshot();
    }
}
