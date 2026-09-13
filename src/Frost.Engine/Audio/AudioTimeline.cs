namespace Frost.Engine.Audio;

/// <summary>
/// Keeps a captured audio track continuous when the source stops delivering.
/// </summary>
/// <remarks>
/// <para><b>The problem this solves.</b> WASAPI loopback delivers nothing at all
/// while nothing is playing — not silent packets, no packets. A game that goes
/// quiet for two seconds produces a two-second hole in the stream. If that hole
/// is simply not written, every sample after it moves two seconds earlier
/// relative to the video, and the recording drifts out of sync permanently. The
/// drift is cumulative, so a long session ends up wildly wrong.</para>
///
/// <para>So the timeline tracks where the track <i>should</i> be and reports how
/// much silence to insert before the next real block. Silence is written rather
/// than the gap being closed, because the video timeline is the one that must not
/// move.</para>
///
/// <para>It also guards the opposite case: a block that arrives overlapping what
/// was already written (a device glitch, or a timestamp that jumped backwards).
/// Writing it would push the track late, so the overlap is trimmed.</para>
/// </remarks>
public sealed class AudioTimeline
{
    private readonly AudioFormat _format;
    private readonly long _maxSilenceTicks;

    private long _nextExpectedTicks = -1;
    private long _silenceFramesInserted;
    private long _framesTrimmed;
    private long _resyncs;

    /// <param name="format">Format of the track.</param>
    /// <param name="maxSilenceGap">
    /// Longest gap that gets filled with silence. Past this the track is
    /// resynchronised instead: a minutes-long gap means capture actually stopped
    /// (the device was unplugged, the session was suspended), and filling it would
    /// write minutes of silence into the file for nothing.
    /// </param>
    public AudioTimeline(AudioFormat format, TimeSpan? maxSilenceGap = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();

        _format = format;

        var gap = maxSilenceGap ?? TimeSpan.FromSeconds(5);
        if (gap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSilenceGap), gap, "Must be positive.");
        }

        _maxSilenceTicks = gap.Ticks;
    }

    /// <summary>Timestamp the next sample is expected at, or -1 before the first block.</summary>
    public long NextExpectedTicks => _nextExpectedTicks;

    /// <summary>Total frames of silence inserted to keep the track continuous.</summary>
    public long SilenceFramesInserted => _silenceFramesInserted;

    /// <summary>Frames trimmed from blocks that overlapped what was already written.</summary>
    public long FramesTrimmed => _framesTrimmed;

    /// <summary>
    /// Times the timeline gave up on filling a gap and jumped forward. Non-zero
    /// means capture genuinely stopped for a while.
    /// </summary>
    public long Resyncs => _resyncs;

    /// <summary>
    /// Works out what to do with a block arriving at <paramref name="timestampTicks"/>.
    /// </summary>
    /// <param name="timestampTicks">Capture timestamp of the block's first frame.</param>
    /// <param name="frameCount">Frames in the block.</param>
    /// <returns>
    /// How much silence to write first, how many frames of the block to skip, and
    /// the timestamp to write the block at.
    /// </returns>
    public AudioPlacement Place(long timestampTicks, int frameCount)
    {
        if (frameCount <= 0)
        {
            return new AudioPlacement(0, 0, timestampTicks, frameCount);
        }

        if (_nextExpectedTicks < 0)
        {
            // First block sets the origin; there is nothing to be out of sync with
            // yet.
            _nextExpectedTicks = timestampTicks + _format.FramesToTicks(frameCount);
            return new AudioPlacement(0, 0, timestampTicks, frameCount);
        }

        var gapTicks = timestampTicks - _nextExpectedTicks;

        if (gapTicks > _maxSilenceTicks)
        {
            // Capture stopped for longer than is worth padding. Jump the timeline
            // forward rather than writing minutes of silence.
            _resyncs++;
            _nextExpectedTicks = timestampTicks + _format.FramesToTicks(frameCount);
            return new AudioPlacement(0, 0, timestampTicks, frameCount);
        }

        if (gapTicks > 0)
        {
            var silenceFrames = _format.TicksToFramesRounded(gapTicks);

            if (silenceFrames > 0)
            {
                _silenceFramesInserted += silenceFrames;
                var silenceAt = _nextExpectedTicks;
                _nextExpectedTicks =
                    silenceAt + _format.FramesToTicks(silenceFrames) + _format.FramesToTicks(frameCount);

                return new AudioPlacement(
                    silenceFrames,
                    0,
                    silenceAt + _format.FramesToTicks(silenceFrames),
                    frameCount);
            }

            // Sub-frame gap: not worth representing, and rounding it up would
            // itself cause drift.
            _nextExpectedTicks = timestampTicks + _format.FramesToTicks(frameCount);
            return new AudioPlacement(0, 0, timestampTicks, frameCount);
        }

        if (gapTicks < 0)
        {
            // Overlap. Writing it whole would push the track late, so trim the
            // part already covered.
            var overlapFrames = _format.TicksToFramesRounded(-gapTicks);

            if (overlapFrames >= frameCount)
            {
                // Entirely in the past: drop it.
                _framesTrimmed += frameCount;
                return new AudioPlacement(0, frameCount, _nextExpectedTicks, 0);
            }

            if (overlapFrames > 0)
            {
                _framesTrimmed += overlapFrames;
                var kept = frameCount - (int)overlapFrames;
                var writeAt = _nextExpectedTicks;
                _nextExpectedTicks = writeAt + _format.FramesToTicks(kept);
                return new AudioPlacement(0, (int)overlapFrames, writeAt, kept);
            }
        }

        _nextExpectedTicks = timestampTicks + _format.FramesToTicks(frameCount);
        return new AudioPlacement(0, 0, timestampTicks, frameCount);
    }

    /// <summary>Forgets the origin, e.g. when capture restarts.</summary>
    public void Reset() => _nextExpectedTicks = -1;
}

/// <summary>What to do with an arriving audio block.</summary>
/// <param name="SilenceFrames">Frames of silence to write before the block.</param>
/// <param name="SkipFrames">Frames to drop from the front of the block.</param>
/// <param name="TimestampTicks">Timestamp to write the kept part of the block at.</param>
/// <param name="FrameCount">Frames of the block to write.</param>
public readonly record struct AudioPlacement(
    long SilenceFrames,
    int SkipFrames,
    long TimestampTicks,
    int FrameCount)
{
    /// <summary>True when silence has to be written before the block.</summary>
    public bool NeedsSilence => SilenceFrames > 0;

    /// <summary>True when the whole block was already covered and should be dropped.</summary>
    public bool IsFullyTrimmed => FrameCount == 0;
}
