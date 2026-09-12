namespace Frost.Shared.Clips;

/// <summary>How a clip came to exist.</summary>
public enum ClipKind
{
    /// <summary>Saved from the ring buffer by a hotkey — the core feature.</summary>
    InstantClip = 0,

    /// <summary>A whole play session recorded to disk.</summary>
    FullSession = 1,

    /// <summary>Produced by trimming an existing clip in the gallery.</summary>
    Trimmed = 2,
}

/// <summary>
/// A marked moment inside a recording.
/// </summary>
/// <param name="OffsetTicks">Offset from the start of the recording, in 100ns ticks.</param>
/// <param name="Source">What put it there.</param>
/// <param name="Label">Optional user-visible note.</param>
public sealed record Bookmark(long OffsetTicks, BookmarkSource Source, string? Label = null)
{
    public TimeSpan Offset => TimeSpan.FromTicks(OffsetTicks);
}

/// <summary>Where a bookmark came from.</summary>
public enum BookmarkSource
{
    /// <summary>The user pressed the bookmark hotkey.</summary>
    Manual = 0,

    /// <summary>
    /// A loudness spike on the game-audio track. Best-effort by nature — see
    /// the autoclip notes in the README.
    /// </summary>
    AudioLoudnessSpike = 1,
}

/// <summary>
/// What the Shell needs to show a clip in the gallery, and what the Engine
/// writes alongside each file.
/// </summary>
/// <remarks>
/// Stored as a sidecar JSON next to the video rather than inside the MP4: it can
/// be rewritten (a rename, a new bookmark) without touching the video file, and
/// a clip whose sidecar is lost still plays.
/// </remarks>
public sealed record ClipMetadata
{
    public required string FilePath { get; init; }

    /// <summary>Name shown in the gallery. Defaults to the file name.</summary>
    public required string DisplayName { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required long DurationTicks { get; init; }

    public required long SizeBytes { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Codec as written, e.g. "H264".</summary>
    public required string Codec { get; init; }

    public ClipKind Kind { get; init; } = ClipKind.InstantClip;

    /// <summary>Process name of the foreground window when the clip was taken, if known.</summary>
    public string? GameName { get; init; }

    public IReadOnlyList<Bookmark> Bookmarks { get; init; } = [];

    public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);

    /// <summary>Sidecar path for a clip file.</summary>
    public static string SidecarPathFor(string clipPath) => clipPath + ".frost.json";
}
