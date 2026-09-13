using Frost.Engine.Diagnostics;
using Frost.Engine.Ipc;
using Frost.Shared.Clips;
using Frost.Shared.Ipc;
using Frost.Shared.Settings;

namespace Frost.Engine.Windows;

/// <summary>
/// An <see cref="IEngineCommands"/> for the <c>--ipc-server</c> diagnostic verb.
/// </summary>
/// <remarks>
/// Serves the things that genuinely work without a capture session running —
/// display and window enumeration, settings, the clip library on disk — and
/// refuses the rest with a clear reason. It exists so the Shell can be developed
/// and the channel exercised against a real Engine process before the resident
/// capture mode lands in Phase 5.
/// </remarks>
internal sealed class DiagnosticEngineCommands : IEngineCommands
{
    private readonly IEngineLog _log;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private FrostSettings _settings;

    internal DiagnosticEngineCommands(FrostSettings settings, IEngineLog log)
    {
        _settings = settings;
        _log = log;
    }

    public EngineStatus GetStatus() => new()
    {
        IsArmed = false,
        IsRecordingSession = false,
        CaptureFps = _settings.Capture.Fps,
        UptimeSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,

        // Honest about what this mode is: the channel works, capture does not.
        FaultMessage =
            "The engine is running in diagnostic IPC mode, so nothing is being captured. " +
            "Capture arrives with the resident engine mode.",
    };

    public bool TryRequestClip(TimeSpan duration, string? label, out string? error)
    {
        _ = duration;
        _ = label;
        error = "Nothing is being captured in diagnostic IPC mode, so there is no buffer to clip.";
        return false;
    }

    public bool ToggleFullSessionRecording(out string? error)
    {
        error = "Recording is not available in diagnostic IPC mode.";
        return false;
    }

    public bool TryAddBookmark(out string? error)
    {
        error = "There is no recording to bookmark in diagnostic IPC mode.";
        return false;
    }

    public FrostSettings GetSettings() => _settings;

    public bool TryApplySettings(FrostSettings settings, out string? error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var corrections = new List<SettingsCorrection>();
        _settings = SettingsStore.Normalise(settings, corrections);

        foreach (var correction in corrections)
        {
            _log.Warn($"Settings correction: {correction}");
        }

        try
        {
            SettingsStore.Save(_settings);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not save settings: {ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<DisplayDescriptor> ListDisplays() =>
        DisplayEnumerator.Displays()
            .Select(d => new DisplayDescriptor(
                d.DeviceName, d.FriendlyName, d.Width, d.Height, d.IsPrimary))
            .ToList();

    public IReadOnlyList<WindowDescriptor> ListWindows() =>
        DisplayEnumerator.Windows()
            .Select(w => new WindowDescriptor(w.Handle, w.Title, w.ProcessName))
            .ToList();

    public IReadOnlyList<ClipMetadata> ListClips()
    {
        var directory = _settings.Storage.ClipDirectory;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.mp4", SearchOption.TopDirectoryOnly)
            .Select(path => ClipMetadataStore.Describe(path))
            .OrderByDescending(clip => clip.CreatedUtc)
            .ToList();
    }
}
