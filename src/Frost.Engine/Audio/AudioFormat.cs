namespace Frost.Engine.Audio;

/// <summary>PCM sample layout.</summary>
public enum AudioSampleType
{
    /// <summary>16-bit signed integer, the format written into the MP4.</summary>
    Int16 = 0,

    /// <summary>32-bit float, which is what WASAPI usually hands over.</summary>
    Float32 = 1,
}

/// <summary>
/// A PCM audio format.
/// </summary>
/// <remarks>
/// Frost does not resample. Whatever the endpoint's mix format is, that is what
/// gets captured and muxed: a resampler would be per-sample CPU work for no
/// benefit, and the AAC encoder in the MP4 sink accepts the common rates
/// directly. The one conversion done is float32 → int16, because that is what the
/// encoder wants and it is a multiply and a clamp.
/// </remarks>
public sealed record AudioFormat
{
    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public required AudioSampleType SampleType { get; init; }

    /// <summary>CD-quality stereo, the fallback when an endpoint reports nothing usable.</summary>
    public static AudioFormat Default { get; } = new()
    {
        SampleRate = 48_000,
        Channels = 2,
        SampleType = AudioSampleType.Float32,
    };

    public int BitsPerSample => SampleType == AudioSampleType.Int16 ? 16 : 32;

    public int BytesPerSample => BitsPerSample / 8;

    /// <summary>Bytes for one sample across all channels.</summary>
    public int BytesPerFrame => BytesPerSample * Channels;

    public int BytesPerSecond => BytesPerFrame * SampleRate;

    /// <summary>The same format as 16-bit, which is what gets muxed.</summary>
    public AudioFormat AsInt16 => SampleType == AudioSampleType.Int16
        ? this
        : this with { SampleType = AudioSampleType.Int16 };

    /// <summary>Duration of <paramref name="frames"/> frames, in 100ns ticks.</summary>
    public long FramesToTicks(long frames) => frames * TimeSpan.TicksPerSecond / SampleRate;

    /// <summary>Frames in <paramref name="ticks"/>, rounded down.</summary>
    public long TicksToFrames(long ticks) => ticks * SampleRate / TimeSpan.TicksPerSecond;

    /// <summary>
    /// Frames in <paramref name="ticks"/>, rounded to nearest.
    /// </summary>
    /// <remarks>
    /// Needed wherever a duration has already been through
    /// <see cref="FramesToTicks"/>: that truncates, so truncating again on the way
    /// back turns 200 frames into 199. Rounding also gives the behaviour a gap
    /// filler wants — a sub-frame difference becomes zero frames rather than one,
    /// so it cannot accumulate into drift.
    /// </remarks>
    public long TicksToFramesRounded(long ticks)
    {
        var scaled = ticks * SampleRate;
        var half = TimeSpan.TicksPerSecond / 2;
        return scaled >= 0
            ? (scaled + half) / TimeSpan.TicksPerSecond
            : (scaled - half) / TimeSpan.TicksPerSecond;
    }

    /// <summary>Bytes needed for <paramref name="duration"/> of audio.</summary>
    public long BytesFor(TimeSpan duration) =>
        (long)(BytesPerSecond * duration.TotalSeconds);

    public void Validate()
    {
        if (SampleRate is < 8_000 or > 384_000)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate, "Unsupported sample rate.");
        }

        if (Channels is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(Channels), Channels, "Unsupported channel count.");
        }
    }

    public override string ToString() =>
        $"{SampleRate}Hz {Channels}ch {BitsPerSample}-bit {(SampleType == AudioSampleType.Float32 ? "float" : "int")}";
}
