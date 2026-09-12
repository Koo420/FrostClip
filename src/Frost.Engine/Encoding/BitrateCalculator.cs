namespace Frost.Engine.Encoding;

/// <summary>
/// Default bitrates, so a user who never opens Settings still gets a sensible
/// file size for their resolution and frame rate.
/// </summary>
/// <remarks>
/// Bits-per-pixel-per-frame, which scales correctly across resolution and frame
/// rate in a way a table of fixed presets does not. The constants are tuned for
/// game capture — high motion, lots of fine detail, and viewers who will notice
/// smearing during a firefight — so they sit above what the same numbers would
/// give for talking-head video.
/// </remarks>
public static class BitrateCalculator
{
    /// <summary>Never go below this; a 2Mbps floor keeps low-resolution captures watchable.</summary>
    public const long MinimumBitsPerSecond = 2_000_000;

    /// <summary>
    /// Ceiling. Past this, disk and upload cost grow faster than anyone's ability
    /// to see the difference, and some hardware encoders start refusing the
    /// configuration.
    /// </summary>
    public const long MaximumBitsPerSecond = 150_000_000;

    private static double BitsPerPixelPerFrame(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => 0.100,
        VideoCodec.Hevc => 0.070,
        VideoCodec.Av1 => 0.055,
        _ => 0.100,
    };

    /// <summary>Recommended bitrate in bits per second.</summary>
    public static long Recommend(VideoCodec codec, int width, int height, int fps)
    {
        if (width < 1 || height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid frame size {width}x{height}.");
        }

        if (fps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "Frame rate must be positive.");
        }

        var raw = (double)width * height * fps * BitsPerPixelPerFrame(codec);

        // Round to the nearest 100kbps: the exact figure is a heuristic, and a
        // round number is what a user expects to see in Settings.
        var rounded = (long)Math.Round(raw / 100_000) * 100_000;

        return Math.Clamp(rounded, MinimumBitsPerSecond, MaximumBitsPerSecond);
    }

    /// <summary>
    /// Bytes a stream at this bitrate occupies for a given duration — what the
    /// ring buffer needs to size itself, and what the disk-usage cap works from.
    /// </summary>
    public static long BytesFor(long bitsPerSecond, TimeSpan duration)
    {
        if (bitsPerSecond < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSecond), bitsPerSecond, "Must be positive.");
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Must not be negative.");
        }

        return (long)(bitsPerSecond / 8.0 * duration.TotalSeconds);
    }
}
