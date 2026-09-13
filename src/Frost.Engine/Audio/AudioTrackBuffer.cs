using Frost.Engine.Encoding;

namespace Frost.Engine.Audio;

/// <summary>
/// A rolling buffer of PCM audio for one track, aligned to the video ring
/// buffer's window.
/// </summary>
/// <remarks>
/// <para>Audio is buffered as PCM, not encoded. At 48kHz stereo 16-bit that is
/// 188KB/s, so a 60-second window costs about 11MB against the video's ~93MB —
/// not worth running an AAC encoder continuously to shrink, and it means the
/// encoder in the MP4 sink can produce whatever the container wants at write
/// time. Video is buffered <i>encoded</i> for the opposite reason: raw 1080p60
/// would be 373MB per second.</para>
///
/// <para>Blocks carry the capture timestamp they arrived with, so a clip can trim
/// audio to the same window as its video and stay in sync. Keyframe alignment has
/// no meaning here — every PCM block is independently decodable — so
/// <see cref="Snapshot"/> trims to the exact requested start.</para>
/// </remarks>
public sealed class AudioTrackBuffer
{
    private readonly SampleArena _arena;
    private readonly object _gate = new();
    private readonly TimeSpan _window;

    private long _blocksWritten;
    private long _blocksRefused;
    private long _bytesWritten;

    /// <param name="format">Format of the audio being stored.</param>
    /// <param name="window">
    /// Trailing duration to keep. Should match the video ring's window plus its
    /// headroom, so a clip never finds audio missing for video it has.
    /// </param>
    /// <param name="maxBlocks">Descriptor slots. WASAPI delivers ~100 blocks/s.</param>
    public AudioTrackBuffer(AudioFormat format, TimeSpan window, int maxBlocks = 0)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();

        if (window <= TimeSpan.Zero || window > TimeSpan.FromMinutes(60))
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "Must be 0..60 minutes.");
        }

        Format = format;
        _window = window;

        // 200 blocks per second of headroom: WASAPI's period is typically 10ms,
        // but a device can hand over much smaller packets.
        var blocks = maxBlocks > 0 ? maxBlocks : (int)Math.Clamp(window.TotalSeconds * 200, 256, 1 << 18);

        _arena = new SampleArena(
            Math.Max(64 * 1024, format.BytesFor(window)),
            blocks);
    }

    public AudioFormat Format { get; }

    /// <summary>Trailing duration this buffer keeps.</summary>
    public TimeSpan Window => _window;

    public long ByteCapacity => _arena.ByteCapacity;

    public long BlocksWritten => Interlocked.Read(ref _blocksWritten);

    /// <summary>Blocks refused, which means a single block exceeded the whole buffer.</summary>
    public long BlocksRefused => Interlocked.Read(ref _blocksRefused);

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

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

    /// <summary>
    /// Appends a block of PCM. Called from the audio capture thread;
    /// allocation-free.
    /// </summary>
    /// <param name="data">PCM bytes in <see cref="Format"/>.</param>
    /// <param name="timestampTicks">Capture time of the first frame in the block.</param>
    public bool Write(ReadOnlySpan<byte> data, long timestampTicks)
    {
        if (data.IsEmpty)
        {
            return false;
        }

        var frames = data.Length / Format.BytesPerFrame;
        var duration = Format.FramesToTicks(frames);

        lock (_gate)
        {
            // Audio has no keyframes, so the newest block always wins and the
            // oldest simply falls out.
            if (!_arena.TryAppendEvicting(data, timestampTicks, duration, isKeyFrame: true, out _))
            {
                Interlocked.Increment(ref _blocksRefused);
                return false;
            }

            TrimToWindow();
        }

        Interlocked.Increment(ref _blocksWritten);
        Interlocked.Add(ref _bytesWritten, data.Length);
        return true;
    }

    /// <summary>
    /// Copies the audio covering <paramref name="startTicks"/>..<paramref name="endTicks"/>
    /// into <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// Whole blocks are taken, so the result can start slightly before the request
    /// and end slightly after — a few milliseconds at WASAPI's packet size. The
    /// actual span covered is reported so the caller can set the first sample's
    /// timestamp correctly instead of assuming it matches the request.
    /// </remarks>
    public AudioSnapshot Snapshot(long startTicks, long endTicks, Span<byte> destination)
    {
        if (endTicks <= startTicks)
        {
            return AudioSnapshot.Empty;
        }

        lock (_gate)
        {
            var written = 0;
            var firstTimestamp = 0L;
            var lastEnd = 0L;
            var blocks = 0;

            for (var i = 0; i < _arena.Count; i++)
            {
                var block = _arena[i];

                // Any overlap with the window counts; a block straddling the start
                // carries audio the clip needs.
                if (block.EndTimestampTicks <= startTicks || block.TimestampTicks >= endTicks)
                {
                    continue;
                }

                if (written + block.Length > destination.Length)
                {
                    // Out of room. Reporting what was copied beats silently
                    // truncating mid-frame.
                    break;
                }

                _arena.CopyTo(block, destination[written..]);

                if (blocks == 0)
                {
                    firstTimestamp = block.TimestampTicks;
                }

                lastEnd = block.EndTimestampTicks;
                written += block.Length;
                blocks++;
            }

            return blocks == 0
                ? AudioSnapshot.Empty
                : new AudioSnapshot(written, firstTimestamp, lastEnd, blocks);
        }
    }

    /// <summary>Bytes a snapshot of this window would need, for sizing a buffer.</summary>
    public long MaxSnapshotBytes => _arena.ByteCapacity;

    public void Clear()
    {
        lock (_gate)
        {
            _arena.Clear();
        }
    }

    private void TrimToWindow()
    {
        var cutoff = _arena.NewestEndTimestampTicks - _window.Ticks;

        while (_arena.Count > 1 && _arena.Peek().EndTimestampTicks <= cutoff)
        {
            _arena.DropOldest();
        }
    }
}

/// <summary>What a call to <see cref="AudioTrackBuffer.Snapshot"/> produced.</summary>
/// <param name="ByteCount">Bytes written to the destination.</param>
/// <param name="StartTicks">Capture timestamp of the first sample copied.</param>
/// <param name="EndTicks">End timestamp of the last sample copied.</param>
/// <param name="BlockCount">Blocks copied.</param>
public readonly record struct AudioSnapshot(int ByteCount, long StartTicks, long EndTicks, int BlockCount)
{
    public static AudioSnapshot Empty => default;

    public bool IsEmpty => ByteCount == 0;

    public TimeSpan Duration => TimeSpan.FromTicks(EndTicks - StartTicks);
}
