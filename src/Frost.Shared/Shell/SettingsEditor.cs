using Frost.Shared.Settings;

namespace Frost.Shared.Shell;

/// <summary>What control a setting needs.</summary>
public enum SettingsFieldKind
{
    Toggle = 0,

    /// <summary>A whole number with a slider or spin box.</summary>
    Integer = 1,

    Decimal = 2,

    /// <summary>One of a fixed set — an enum rendered as a combo box.</summary>
    Choice = 3,

    /// <summary>A folder, with a picker.</summary>
    FolderPath = 4,

    /// <summary>A device or encoder chosen from a list the Engine supplies.</summary>
    DeviceChoice = 5,

    /// <summary>Free text, e.g. an accent colour.</summary>
    Text = 6,

    /// <summary>A size in bytes, shown in GB.</summary>
    ByteSize = 7,
}

/// <summary>
/// One row on the settings page.
/// </summary>
/// <param name="Section">The schema section, e.g. "Capture".</param>
/// <param name="Property">The property name in that section.</param>
/// <param name="Label">What the row is called on screen.</param>
/// <param name="Kind">The control to render.</param>
/// <param name="Description">Sub-label, where the setting needs explaining.</param>
/// <param name="RequiresRestart">
/// Changing it only takes effect when the Engine next arms.
/// </param>
public readonly record struct SettingsField(
    string Section,
    string Property,
    string Label,
    SettingsFieldKind Kind,
    string? Description = null,
    bool RequiresRestart = false)
{
    /// <summary>"Capture.Fps" — the key used in a <see cref="SettingsCorrection"/>.</summary>
    public string Path => $"{Section}.{Property}";
}

/// <summary>
/// The settings page, described as data.
/// </summary>
/// <remarks>
/// <para>Phase 7's requirement is a settings UI "covering everything in Phase 4's
/// schema", and the failure mode that requirement is guarding against is a
/// schema that grows a field while the UI quietly does not. A hand-written XAML
/// page cannot be checked against the schema at all; a list of descriptors can,
/// and <c>SettingsUiTests</c> reflects over <see cref="FrostSettings"/> to assert
/// that every property has a row and every row a property. That is what turns
/// "covering everything" from a claim into something that fails a build.</para>
///
/// <para>The reflection lives in the tests rather than here on purpose. The
/// Engine is published AOT, and reflecting over the schema at runtime is both an
/// IL2075 trim warning and a thing the product never needs to do — the check is
/// only interesting while the code is being changed.</para>
///
/// <para>Bounds are deliberately absent here. Every numeric setting is already
/// clamped by <see cref="SettingsStore.Normalise"/>, which reports what it
/// changed and why; duplicating the ranges in the UI layer would give two places
/// to disagree about them. The page validates by normalising and showing the
/// corrections, which also means a hand-edited settings file and a bad UI entry
/// produce the same message.</para>
/// </remarks>
public static class SettingsEditor
{
    /// <summary>Sections the settings page does not render as plain rows.</summary>
    /// <remarks>
    /// <c>Version</c> is for migration, not for people. <c>Hotkeys</c> is a list
    /// with its own rebinding UI, handled by <see cref="HotkeyRebind"/>.
    /// </remarks>
    public static IReadOnlyList<string> NonFieldProperties { get; } = ["Version", "Hotkeys"];

    /// <summary>Every row on the settings page, in the order it is shown.</summary>
    public static IReadOnlyList<SettingsField> Fields { get; } =
    [
        // ---- Capture ----
        new("Capture", nameof(CaptureSettings.TargetKind), "What to record",
            SettingsFieldKind.Choice,
            "A monitor, a single window, or whichever display the foreground window is on."),
        new("Capture", nameof(CaptureSettings.MonitorDeviceName), "Monitor",
            SettingsFieldKind.DeviceChoice),
        new("Capture", nameof(CaptureSettings.WindowProcessName), "Window",
            SettingsFieldKind.DeviceChoice,
            "Matched by process name, so it reattaches when the game restarts."),
        new("Capture", nameof(CaptureSettings.Fps), "Frame rate",
            SettingsFieldKind.Integer, RequiresRestart: true),
        new("Capture", nameof(CaptureSettings.CaptureCursor), "Include the mouse cursor",
            SettingsFieldKind.Toggle),
        new("Capture", nameof(CaptureSettings.ShowCaptureBorder), "Show the capture border",
            SettingsFieldKind.Toggle,
            "Windows draws a highlight around what is being captured. Off by default; " +
            "on Windows 10 it cannot be hidden at all."),

        // ---- Encoder ----
        new("Encoder", nameof(EncoderSettingsModel.Codec), "Codec",
            SettingsFieldKind.Choice,
            "H.264 plays everywhere. HEVC and AV1 are smaller at the same quality, " +
            "if your GPU encodes them.",
            RequiresRestart: true),
        new("Encoder", nameof(EncoderSettingsModel.PreferredEncoderName), "Encoder",
            SettingsFieldKind.DeviceChoice,
            "Hardware encoders only — Frost will not fall back to software.",
            RequiresRestart: true),
        new("Encoder", nameof(EncoderSettingsModel.BitsPerSecond), "Bitrate",
            SettingsFieldKind.Integer, RequiresRestart: true),
        new("Encoder", nameof(EncoderSettingsModel.RateControl), "Rate control",
            SettingsFieldKind.Choice, RequiresRestart: true),
        new("Encoder", nameof(EncoderSettingsModel.KeyFrameIntervalSeconds), "Keyframe interval",
            SettingsFieldKind.Decimal,
            "Also the accuracy of a lossless trim: a clip can only be cut on a keyframe.",
            RequiresRestart: true),

        // ---- Audio ----
        new("Audio", nameof(AudioSettings.CaptureSystemAudio), "Record game audio",
            SettingsFieldKind.Toggle, RequiresRestart: true),
        new("Audio", nameof(AudioSettings.CaptureMicrophone), "Record the microphone",
            SettingsFieldKind.Toggle,
            "Added as a second track, so it can be removed later.",
            RequiresRestart: true),
        new("Audio", nameof(AudioSettings.MicrophoneDeviceId), "Microphone",
            SettingsFieldKind.DeviceChoice, RequiresRestart: true),
        new("Audio", nameof(AudioSettings.MicrophoneGain), "Microphone gain",
            SettingsFieldKind.Decimal),
        new("Audio", nameof(AudioSettings.MicrophoneMutedAtStart), "Start muted",
            SettingsFieldKind.Toggle),

        // ---- Autoclip ----
        new("Autoclip", nameof(AutoclipSettings.DetectLoudnessSpikes),
            "Bookmark loud moments",
            SettingsFieldKind.Toggle,
            "Best-effort: it marks a loudness spike on the game audio. It does not " +
            "know what happened, and it will mark things you do not care about."),
        new("Autoclip", nameof(AutoclipSettings.LoudnessSpikeThresholdDb),
            "How much louder counts", SettingsFieldKind.Decimal),
        new("Autoclip", nameof(AutoclipSettings.LoudnessBaselineSeconds),
            "Compared against the last", SettingsFieldKind.Decimal),
        new("Autoclip", nameof(AutoclipSettings.MinimumSecondsBetweenMarks),
            "Minimum gap between marks", SettingsFieldKind.Decimal),

        // ---- Storage ----
        new("Storage", nameof(StorageSettings.ClipDirectory), "Save clips to",
            SettingsFieldKind.FolderPath),
        new("Storage", nameof(StorageSettings.MaxDiskUsageBytes), "Disk budget",
            SettingsFieldKind.ByteSize),
        new("Storage", nameof(StorageSettings.DeleteOldestWhenFull),
            "Delete the oldest when over budget", SettingsFieldKind.Toggle),
        new("Storage", nameof(StorageSettings.ProtectRecentDays), "Never delete clips newer than",
            SettingsFieldKind.Integer,
            "So a long session cannot evict the clip you saved at the start of it."),

        // ---- Buffer ----
        new("Buffer", nameof(BufferSettings.MaxMemoryBytes), "Memory for the buffer",
            SettingsFieldKind.ByteSize,
            "The ring buffer is held in RAM, so the longest clip you can save is " +
            "limited by this rather than by disk.",
            RequiresRestart: true),
        new("Buffer", nameof(BufferSettings.HeadroomFactor), "Buffer headroom",
            SettingsFieldKind.Decimal,
            "Extra room over the longest clip preset, so a bitrate spike cannot " +
            "shorten what a hotkey saves.",
            RequiresRestart: true),

        // ---- Behaviour ----
        new("Behaviour", nameof(BehaviourSettings.StartWithWindows), "Start with Windows",
            SettingsFieldKind.Toggle),
        new("Behaviour", nameof(BehaviourSettings.StartMinimisedToTray), "Start in the tray",
            SettingsFieldKind.Toggle),
        new("Behaviour", nameof(BehaviourSettings.ArmBufferOnStart), "Arm the buffer on start",
            SettingsFieldKind.Toggle,
            "Off means a hotkey has nothing to save until you arm it."),
        new("Behaviour", nameof(BehaviourSettings.ShowClipSavedToast), "Show a toast on save",
            SettingsFieldKind.Toggle),

        // ---- Appearance ----
        new("Appearance", nameof(AppearanceSettings.Theme), "Theme", SettingsFieldKind.Choice),
        new("Appearance", nameof(AppearanceSettings.AccentColor), "Accent colour",
            SettingsFieldKind.Text),
        new("Appearance", nameof(AppearanceSettings.UseMica), "Mica background",
            SettingsFieldKind.Toggle,
            "Only drawn while the Frost window is in the foreground."),
    ];

    /// <summary>
    /// Validates a whole settings object the way the page should: by normalising
    /// it and reporting what had to change.
    /// </summary>
    /// <remarks>
    /// Reusing <see cref="SettingsStore.Normalise"/> rather than re-stating its
    /// ranges means the UI and a hand-edited file can never disagree about what
    /// is valid, and both produce the same wording.
    /// </remarks>
    public static (FrostSettings Settings, IReadOnlyList<SettingsCorrection> Corrections)
        Validate(FrostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var corrections = new List<SettingsCorrection>();
        return (SettingsStore.Normalise(settings, corrections), corrections);
    }
}
