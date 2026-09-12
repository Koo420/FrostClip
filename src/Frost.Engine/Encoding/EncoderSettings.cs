namespace Frost.Engine.Encoding;

/// <summary>How the encoder should spend its bits.</summary>
public enum RateControlMode
{
    /// <summary>
    /// Constant bitrate. Predictable file size and predictable ring-buffer
    /// memory, which is why it is the default for a rolling buffer.
    /// </summary>
    ConstantBitrate = 0,

    /// <summary>Variable bitrate, peaking up to twice the target on hard scenes.</summary>
    VariableBitrate = 1,

    /// <summary>Quality-targeted; file size varies freely. Not for the ring buffer.</summary>
    Quality = 2,
}

/// <summary>
/// Everything the hardware encoder needs configuring with. Immutable — changing
/// encode settings restarts the encoder rather than mutating live state.
/// </summary>
public sealed record EncoderSettings
{
    public required VideoCodec Codec { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Fps { get; init; }

    /// <summary>Target bitrate. Defaults from <see cref="BitrateCalculator"/> when not set.</summary>
    public long? BitsPerSecond { get; init; }

    public RateControlMode RateControl { get; init; } = RateControlMode.ConstantBitrate;

    /// <summary>
    /// Seconds between keyframes.
    /// </summary>
    /// <remarks>
    /// This is the knob that decides how accurate "save the trailing 15 seconds"
    /// can be: a clip has to start on a keyframe to be decodable without
    /// re-encoding, so the worst-case error in the clip's start point is one
    /// keyframe interval. Two seconds keeps that error small without spending
    /// too many bits on I-frames.
    /// </remarks>
    public double KeyFrameIntervalSeconds { get; init; } = 2.0;

    /// <summary>
    /// Ask the encoder for its lowest-latency mode. Wanted here: it shortens the
    /// lookahead, so the frames just before a hotkey press are already encoded
    /// rather than still buffered inside the encoder.
    /// </summary>
    public bool LowLatency { get; init; } = true;

    /// <summary>Resolved bitrate, using the recommendation if none was configured.</summary>
    public long EffectiveBitsPerSecond =>
        BitsPerSecond ?? BitrateCalculator.Recommend(Codec, Width, Height, Fps);

    /// <summary>Keyframe interval in frames, which is what the encoder is configured in.</summary>
    public int KeyFrameIntervalFrames =>
        Math.Max(1, (int)Math.Round(KeyFrameIntervalSeconds * Fps));

    public void Validate()
    {
        if (Width < 16 || Height < 16 || Width > 16384 || Height > 16384)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Width), $"{Width}x{Height} is not an encodable frame size.");
        }

        // Hardware encoders want even dimensions for 4:2:0 chroma; an odd size
        // either fails at configuration time or silently crops.
        if (Width % 2 != 0 || Height % 2 != 0)
        {
            throw new ArgumentException(
                $"{Width}x{Height} has an odd dimension. NV12 is 4:2:0, so both must be even.",
                nameof(Width));
        }

        if (Fps is < 1 or > 480)
        {
            throw new ArgumentOutOfRangeException(nameof(Fps), Fps, "Frame rate must be 1..480.");
        }

        if (BitsPerSecond is { } bitrate &&
            (bitrate < BitrateCalculator.MinimumBitsPerSecond ||
             bitrate > BitrateCalculator.MaximumBitsPerSecond))
        {
            throw new ArgumentOutOfRangeException(
                nameof(BitsPerSecond), bitrate,
                $"Bitrate must be {BitrateCalculator.MinimumBitsPerSecond}..{BitrateCalculator.MaximumBitsPerSecond} bps.");
        }

        if (KeyFrameIntervalSeconds is <= 0 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(KeyFrameIntervalSeconds), KeyFrameIntervalSeconds,
                "Keyframe interval must be 0..30 seconds; it bounds how accurately a clip can start.");
        }
    }

    /// <summary>Settings for a capture size and rate, with everything else defaulted.</summary>
    public static EncoderSettings For(VideoCodec codec, int width, int height, int fps) =>
        new() { Codec = codec, Width = width, Height = height, Fps = fps };
}
