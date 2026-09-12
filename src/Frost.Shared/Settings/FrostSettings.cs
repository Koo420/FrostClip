using Frost.Shared.Hotkeys;

namespace Frost.Shared.Settings;

/// <summary>What to capture.</summary>
public sealed record CaptureSettings
{
    /// <summary>0 = primary monitor, 1 = named monitor, 2 = specific window.</summary>
    public int TargetKind { get; init; }

    /// <summary>DXGI output device name, e.g. <c>\\.\DISPLAY1</c>.</summary>
    public string? MonitorDeviceName { get; init; }

    /// <summary>
    /// Process name of the window to capture. Stored rather than the HWND, which
    /// changes every time the game restarts.
    /// </summary>
    public string? WindowProcessName { get; init; }

    public int Fps { get; init; } = 60;

    /// <summary>Draw the cursor into recordings.</summary>
    public bool CaptureCursor { get; init; }

    /// <summary>Show the OS capture border. Off by default; most people find it distracting.</summary>
    public bool ShowCaptureBorder { get; init; }
}

/// <summary>How to encode.</summary>
public sealed record EncoderSettingsModel
{
    /// <summary>0 = H.264, 1 = HEVC, 2 = AV1.</summary>
    public int Codec { get; init; }

    /// <summary>
    /// Exact encoder MFT name, for a machine with more than one GPU. Empty means
    /// "let Frost pick the one on the GPU it is capturing from".
    /// </summary>
    public string? PreferredEncoderName { get; init; }

    /// <summary>Null means use the resolution/frame-rate recommendation.</summary>
    public long? BitsPerSecond { get; init; }

    /// <summary>0 = CBR, 1 = VBR, 2 = quality-targeted.</summary>
    public int RateControl { get; init; }

    /// <summary>
    /// Seconds between keyframes. Bounds how accurately a clip can start, since
    /// clips must begin on one.
    /// </summary>
    public double KeyFrameIntervalSeconds { get; init; } = 2.0;
}

/// <summary>Audio capture.</summary>
public sealed record AudioSettings
{
    /// <summary>Record game and system audio via WASAPI loopback.</summary>
    public bool CaptureSystemAudio { get; init; } = true;

    /// <summary>Record the microphone as a second track.</summary>
    public bool CaptureMicrophone { get; init; }

    /// <summary>WASAPI device ID, or empty for the default device.</summary>
    public string? MicrophoneDeviceId { get; init; }

    /// <summary>Microphone gain, 0..2 where 1 is unity.</summary>
    public double MicrophoneGain { get; init; } = 1.0;

    /// <summary>Start with the microphone muted.</summary>
    public bool MicrophoneMutedAtStart { get; init; }
}

/// <summary>
/// The optional, best-effort autoclip heuristics.
/// </summary>
/// <remarks>
/// Off by default, and labelled best-effort wherever it appears, because
/// compositor-level heuristics genuinely do produce false positives. The
/// deliberate omission is any attempt to read on-screen UI or killfeeds: that
/// needs OCR or per-game templates, costs performance on every frame and breaks
/// with every game patch.
/// </remarks>
public sealed record AutoclipSettings
{
    /// <summary>Bookmark moments where the game audio spikes.</summary>
    public bool DetectLoudnessSpikes { get; init; }

    /// <summary>
    /// How far above the recent baseline a spike must be, in decibels, to count.
    /// </summary>
    public double LoudnessSpikeThresholdDb { get; init; } = 9.0;

    /// <summary>Seconds of baseline the detector averages over.</summary>
    public double LoudnessBaselineSeconds { get; init; } = 3.0;

    /// <summary>Minimum gap between automatic bookmarks, so one fight is not fifty marks.</summary>
    public double MinimumSecondsBetweenMarks { get; init; } = 5.0;
}

/// <summary>Where recordings go and how much disk they may use.</summary>
public sealed record StorageSettings
{
    /// <summary>Empty means Videos\Frost.</summary>
    public string? ClipDirectory { get; init; }

    /// <summary>Disk budget in bytes. Zero means unlimited.</summary>
    public long MaxDiskUsageBytes { get; init; } = 100L * 1024 * 1024 * 1024;

    /// <summary>Delete oldest recordings first when over budget.</summary>
    public bool DeleteOldestWhenFull { get; init; } = true;

    /// <summary>
    /// Never delete recordings newer than this many days, even when over budget.
    /// </summary>
    /// <remarks>
    /// A guard against the case where a long session fills the budget and the
    /// cleanup eats the clips from earlier the same evening.
    /// </remarks>
    public int ProtectRecentDays { get; init; } = 1;
}

/// <summary>Ring buffer sizing.</summary>
public sealed record BufferSettings
{
    /// <summary>
    /// Hard ceiling on ring-buffer memory. The effective clip length is whichever
    /// is smaller, this or the longest hotkey preset.
    /// </summary>
    public long MaxMemoryBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Spare capacity beyond the requested duration.</summary>
    public double HeadroomFactor { get; init; } = 1.5;
}

/// <summary>Startup and tray behaviour.</summary>
public sealed record BehaviourSettings
{
    public bool StartWithWindows { get; init; }

    public bool StartMinimisedToTray { get; init; } = true;

    /// <summary>Arm the ring buffer as soon as the Engine starts.</summary>
    public bool ArmBufferOnStart { get; init; } = true;

    /// <summary>Show the "clip saved" toast.</summary>
    public bool ShowClipSavedToast { get; init; } = true;
}

/// <summary>Shell appearance.</summary>
public sealed record AppearanceSettings
{
    /// <summary>"Dark", "Light" or "System". Dark is the default.</summary>
    public string Theme { get; init; } = "Dark";

    /// <summary>Accent colour as #RRGGBB.</summary>
    public string AccentColor { get; init; } = "#3FA9F5";

    /// <summary>Use Mica. Only ever active while the Shell window is foregrounded.</summary>
    public bool UseMica { get; init; } = true;
}

/// <summary>
/// The whole of Frost's configuration, as persisted.
/// </summary>
/// <remarks>
/// Records with init-only properties and defaults on every one: a settings file
/// written by an older build is missing fields, and every missing field has to
/// land on a sane value rather than null or zero.
/// </remarks>
public sealed record FrostSettings
{
    /// <summary>Bumped when a change needs migrating rather than defaulting.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public CaptureSettings Capture { get; init; } = new();

    public EncoderSettingsModel Encoder { get; init; } = new();

    public AudioSettings Audio { get; init; } = new();

    public AutoclipSettings Autoclip { get; init; } = new();

    public StorageSettings Storage { get; init; } = new();

    public BufferSettings Buffer { get; init; } = new();

    public BehaviourSettings Behaviour { get; init; } = new();

    public AppearanceSettings Appearance { get; init; } = new();

    public List<HotkeyAssignmentModel> Hotkeys { get; init; } =
        HotkeyAssignment.Defaults.Select(HotkeyAssignmentModel.From).ToList();

    /// <summary>
    /// Whether any configured hotkey is usable, without the default fallback.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ResolveHotkeys"/> so a caller can tell "the user
    /// configured nothing usable" from "here is something that works" — the
    /// former is worth reporting as a correction.
    /// </remarks>
    public bool HasUsableHotkey()
    {
        if (Hotkeys is null)
        {
            return false;
        }

        foreach (var model in Hotkeys)
        {
            if (model.TryResolve(out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hotkeys as the Engine's own type.</summary>
    public IReadOnlyList<HotkeyAssignment> ResolveHotkeys()
    {
        if (Hotkeys is null)
        {
            return HotkeyAssignment.Defaults;
        }

        var resolved = new List<HotkeyAssignment>(Hotkeys.Count);

        foreach (var model in Hotkeys)
        {
            if (model.TryResolve(out var assignment))
            {
                resolved.Add(assignment);
            }
        }

        // A settings file with no usable hotkey would leave the app silently
        // inert, so fall back to the defaults rather than doing nothing.
        return resolved.Count > 0 ? resolved : HotkeyAssignment.Defaults;
    }
}

/// <summary>
/// A hotkey as stored on disk: the binding as text, so a settings file is
/// readable and hand-editable.
/// </summary>
public sealed record HotkeyAssignmentModel
{
    public int Action { get; init; }

    /// <summary>e.g. <c>Alt+F10</c>.</summary>
    public string Binding { get; init; } = string.Empty;

    /// <summary>Seconds, for SaveClip.</summary>
    public double? ClipSeconds { get; init; }

    public string? Label { get; init; }

    public bool Enabled { get; init; } = true;

    public static HotkeyAssignmentModel From(HotkeyAssignment assignment) => new()
    {
        Action = (int)assignment.Action,
        Binding = assignment.Binding.ToString(),
        ClipSeconds = assignment.ClipDuration?.TotalSeconds,
        Label = assignment.Label,
        Enabled = assignment.Enabled,
    };

    /// <summary>
    /// Converts back, rejecting anything unusable rather than throwing — one bad
    /// line in a settings file must not stop the other hotkeys from working.
    /// </summary>
    public bool TryResolve(out HotkeyAssignment assignment)
    {
        assignment = null!;

        if (!HotkeyBinding.TryParse(Binding, out var binding) ||
            !Enum.IsDefined(typeof(HotkeyAction), Action))
        {
            return false;
        }

        var action = (HotkeyAction)Action;
        TimeSpan? duration = ClipSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

        if (action == HotkeyAction.SaveClip &&
            (duration is null || duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30)))
        {
            return false;
        }

        assignment = new HotkeyAssignment
        {
            Action = action,
            Binding = binding,
            ClipDuration = duration,
            Label = Label,
            Enabled = Enabled,
        };

        return true;
    }
}
