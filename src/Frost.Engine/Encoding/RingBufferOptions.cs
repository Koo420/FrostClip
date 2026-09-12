namespace Frost.Engine.Encoding;

/// <summary>How the trailing-clip ring buffer is sized.</summary>
public sealed record RingBufferOptions
{
    /// <summary>
    /// Longest clip the buffer must be able to produce. This is the maximum of
    /// every hotkey's configured duration, not any one of them: one buffer serves
    /// all the presets, and a 15-second clip is just a shorter read of the same
    /// data.
    /// </summary>
    public required TimeSpan MaxTrailingDuration { get; init; }

    /// <summary>Encoder bitrate, used to size the byte arena.</summary>
    public required long BitsPerSecond { get; init; }

    /// <summary>Capture frame rate, used to size the descriptor ring.</summary>
    public required int Fps { get; init; }

    /// <summary>
    /// Extra capacity beyond the requested duration, as a multiplier.
    /// </summary>
    /// <remarks>
    /// Headroom buys two things. Keyframe alignment: a clip has to start on a
    /// keyframe, so the buffer holds back to the keyframe *before* the requested
    /// window. And a margin while a clip is being written out, during which the
    /// samples being read cannot be evicted — without slack, saving a clip would
    /// start dropping incoming frames.
    /// </remarks>
    public double HeadroomFactor { get; init; } = 1.5;

    /// <summary>Ceiling on the arena, so a misconfiguration cannot exhaust RAM.</summary>
    public long MaxBytes { get; init; } = 1024L * 1024 * 1024;

    public void Validate()
    {
        if (MaxTrailingDuration <= TimeSpan.Zero || MaxTrailingDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTrailingDuration), MaxTrailingDuration, "Must be between 0 and 30 minutes.");
        }

        if (BitsPerSecond < BitrateCalculator.MinimumBitsPerSecond ||
            BitsPerSecond > BitrateCalculator.MaximumBitsPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(BitsPerSecond), BitsPerSecond, "Out of range.");
        }

        if (Fps is < 1 or > 480)
        {
            throw new ArgumentOutOfRangeException(nameof(Fps), Fps, "Frame rate must be 1..480.");
        }

        if (HeadroomFactor is < 1.1 or > 4.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeadroomFactor), HeadroomFactor,
                "Headroom must be 1.1..4.0; below that a clip save would evict frames it is still reading.");
        }

        if (MaxBytes < 4L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), MaxBytes, "Must be at least 4MB.");
        }
    }

    /// <summary>Byte arena size implied by these options, after the cap.</summary>
    public long ArenaBytes
    {
        get
        {
            var wanted = BitrateCalculator.BytesFor(
                BitsPerSecond, MaxTrailingDuration * HeadroomFactor);

            return Math.Clamp(wanted, 4L * 1024 * 1024, MaxBytes);
        }
    }

    /// <summary>
    /// The longest clip actually achievable, which is shorter than
    /// <see cref="MaxTrailingDuration"/> when <see cref="MaxBytes"/> binds.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than hidden: a user who asks for a 5-minute buffer at
    /// 50Mbps and silently gets 2.5 minutes would rightly call that a bug. The
    /// settings UI shows this number, and the Engine logs it at startup.
    /// </remarks>
    public TimeSpan EffectiveMaxTrailingDuration
    {
        get
        {
            var seconds = ArenaBytes / (BitsPerSecond / 8.0) / HeadroomFactor;
            var achievable = TimeSpan.FromSeconds(seconds);
            return achievable < MaxTrailingDuration ? achievable : MaxTrailingDuration;
        }
    }

    /// <summary>True when the byte cap, not the requested duration, is the limit.</summary>
    public bool IsLimitedByMemoryCap => EffectiveMaxTrailingDuration < MaxTrailingDuration;

    /// <summary>Descriptor slots implied by these options.</summary>
    public int ArenaSamples
    {
        get
        {
            var wanted = MaxTrailingDuration.TotalSeconds * Fps * HeadroomFactor;

            // Filler frames aside, one descriptor per frame plus a healthy margin;
            // running out of descriptors before running out of bytes would trim
            // the buffer shorter than the user asked for.
            return (int)Math.Clamp(wanted + 64, 64, 1 << 20);
        }
    }
}
