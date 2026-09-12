namespace Frost.Engine.Capture;

/// <summary>
/// Everything the capture stage needs to know up front. Deliberately immutable:
/// changing capture settings restarts the session rather than mutating live
/// state a capture thread is reading.
/// </summary>
public sealed record CaptureConfiguration
{
    /// <summary>What to capture.</summary>
    public CaptureTarget Target { get; init; } = CaptureTarget.PrimaryMonitor;

    /// <summary>Frames per second handed downstream. Frames above this rate are dropped.</summary>
    public int TargetFps { get; init; } = 60;

    /// <summary>
    /// Number of textures pre-allocated for the capture→encode handoff. Three is
    /// enough to absorb ordinary encoder jitter without adding latency; more
    /// costs VRAM (width * height * 4 bytes each).
    /// </summary>
    public int TexturePoolSize { get; init; } = 3;

    /// <summary>
    /// WGC's own frame pool depth. Separate from <see cref="TexturePoolSize"/>:
    /// this is how many frames the compositor may queue for us before dropping.
    /// </summary>
    public int CaptureQueueDepth { get; init; } = 3;

    /// <summary>
    /// Longest gap allowed between emitted frames. A perfectly static screen
    /// produces no WGC frames at all; without a bound the encoded timeline would
    /// contain arbitrarily long samples and "trailing N seconds" would get fuzzy.
    /// </summary>
    public TimeSpan MaxFrameInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Draw the mouse cursor into the capture.</summary>
    public bool CaptureCursor { get; init; }

    /// <summary>
    /// Show the yellow "this is being captured" border. Off by default; only
    /// honoured on Windows builds where the API exists (Win11 21H2+).
    /// </summary>
    public bool ShowCaptureBorder { get; init; }

    internal void Validate()
    {
        if (TargetFps is < 1 or > 480)
        {
            throw new ArgumentOutOfRangeException(nameof(TargetFps), TargetFps, "TargetFps must be 1..480.");
        }

        if (TexturePoolSize < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TexturePoolSize), TexturePoolSize,
                "At least two textures are needed so capture and encode never contend for one.");
        }

        if (CaptureQueueDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CaptureQueueDepth), CaptureQueueDepth, "Must be >= 1.");
        }

        if (MaxFrameInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFrameInterval), MaxFrameInterval, "Must be positive.");
        }
    }
}
