using Frost.Engine.Ipc;
using Frost.Shared.Clips;
using Frost.Shared.Ipc;
using Frost.Shared.Settings;

namespace Frost.Engine.Tests;

/// <summary>Stands in for the Engine so the whole IPC surface is exercisable.</summary>
internal sealed class FakeEngineCommands : IEngineCommands
{
    internal List<(TimeSpan Duration, string? Label)> ClipRequests { get; } = [];

    internal List<FrostSettings> AppliedSettings { get; } = [];

    internal int BookmarkCount { get; private set; }

    internal bool IsRecording { get; private set; }

    internal EngineStatus Status { get; set; } = new()
    {
        IsArmed = true,
        BufferedSeconds = 31.5,
        MaxClipSeconds = 60,
        BufferBytes = 48_000_000,
        CaptureWidth = 1920,
        CaptureHeight = 1080,
        CaptureFps = 60,
        EncoderName = "NVIDIA H.264 Encoder MFT",
        Codec = "H264",
        BitsPerSecond = 12_400_000,
        FramesCaptured = 12_345,
        ClipsSaved = 3,
        UptimeSeconds = 205.5,
    };

    internal FrostSettings Settings { get; set; } = new();

    /// <summary>Reason to refuse the next clip request, if any.</summary>
    internal string? RefuseClipsBecause { get; set; }

    /// <summary>When set, every handler throws — the "handler blew up" path.</summary>
    internal Exception? ThrowFromHandlers { get; set; }

    public EngineStatus GetStatus()
    {
        ThrowIfConfigured();
        return Status with { IsRecordingSession = IsRecording };
    }

    public bool TryRequestClip(TimeSpan duration, string? label, out string? error)
    {
        ThrowIfConfigured();

        if (RefuseClipsBecause is not null)
        {
            error = RefuseClipsBecause;
            return false;
        }

        ClipRequests.Add((duration, label));
        error = null;
        return true;
    }

    public bool ToggleFullSessionRecording(out string? error)
    {
        ThrowIfConfigured();
        IsRecording = !IsRecording;
        error = null;
        return IsRecording;
    }

    public bool TryAddBookmark(out string? error)
    {
        ThrowIfConfigured();
        BookmarkCount++;
        error = null;
        return true;
    }

    public FrostSettings GetSettings()
    {
        ThrowIfConfigured();
        return Settings;
    }

    public bool TryApplySettings(FrostSettings settings, out string? error)
    {
        ThrowIfConfigured();
        AppliedSettings.Add(settings);
        Settings = settings;
        error = null;
        return true;
    }

    public IReadOnlyList<DisplayDescriptor> ListDisplays()
    {
        ThrowIfConfigured();
        return
        [
            new DisplayDescriptor(@"\\.\DISPLAY1", "Dell AW3423DW", 3440, 1440, true),
            new DisplayDescriptor(@"\\.\DISPLAY2", "Generic PnP Monitor", 1920, 1080, false),
        ];
    }

    public IReadOnlyList<WindowDescriptor> ListWindows()
    {
        ThrowIfConfigured();
        return [new WindowDescriptor(0x1234, "Half-Life 2", "hl2")];
    }

    public IReadOnlyList<ClipMetadata> ListClips()
    {
        ThrowIfConfigured();
        return
        [
            new ClipMetadata
            {
                FilePath = @"D:\Clips\Half-Life 2 2026-09-12 20-11-03 (30s).mp4",
                DisplayName = "Half-Life 2 2026-09-12 20-11-03 (30s)",
                CreatedUtc = new DateTimeOffset(2026, 9, 12, 20, 11, 3, TimeSpan.Zero),
                DurationTicks = TimeSpan.FromSeconds(30).Ticks,
                SizeBytes = 46_500_000,
                Width = 1920,
                Height = 1080,
                Codec = "H264",
                GameName = "hl2",
                Bookmarks = [new Bookmark(TimeSpan.FromSeconds(12).Ticks, BookmarkSource.Manual, "the shot")],
            },
        ];
    }

    private void ThrowIfConfigured()
    {
        if (ThrowFromHandlers is not null)
        {
            throw ThrowFromHandlers;
        }
    }
}
