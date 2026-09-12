using Frost.Shared.Hotkeys;

namespace Frost.Shared.Settings;

/// <summary>What a hotkey does.</summary>
public enum HotkeyAction
{
    /// <summary>Save the trailing N seconds from the ring buffer. The core feature.</summary>
    SaveClip = 0,

    /// <summary>Start or stop recording the whole session to disk.</summary>
    ToggleFullSessionRecording = 1,

    /// <summary>
    /// Mark this moment in the full-session recording without exporting
    /// anything — much cheaper than a clip, and reviewable later.
    /// </summary>
    Bookmark = 2,

    /// <summary>Mute or unmute the microphone track.</summary>
    ToggleMicrophoneMute = 3,
}

/// <summary>
/// One hotkey and what it triggers.
/// </summary>
/// <remarks>
/// Several <see cref="HotkeyAction.SaveClip"/> assignments can be active at
/// once, each with its own <see cref="ClipDuration"/> — that is how "15s / 30s /
/// 60s on three different keys" works, and they all read from the same ring
/// buffer.
/// </remarks>
public sealed record HotkeyAssignment
{
    public required HotkeyAction Action { get; init; }

    public required HotkeyBinding Binding { get; init; }

    /// <summary>
    /// Trailing duration for <see cref="HotkeyAction.SaveClip"/>. Ignored by the
    /// other actions.
    /// </summary>
    public TimeSpan? ClipDuration { get; init; }

    /// <summary>
    /// Short label used in the clip's file name and on the toast, e.g. "30s".
    /// Defaults to the duration when not set.
    /// </summary>
    public string? Label { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Label to show, derived from the duration when none was set.</summary>
    public string EffectiveLabel =>
        Label ?? (ClipDuration is { } duration ? FormatDuration(duration) : Action.ToString());

    public void Validate()
    {
        if (!Binding.IsBound)
        {
            throw new ArgumentException($"The {Action} hotkey has no key bound.", nameof(Binding));
        }

        if (Action == HotkeyAction.SaveClip)
        {
            if (ClipDuration is not { } duration)
            {
                throw new ArgumentException("A SaveClip hotkey needs a duration.", nameof(ClipDuration));
            }

            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ClipDuration), duration, "Clip duration must be between 0 and 30 minutes.");
            }
        }
    }

    /// <summary>"15s", "5m", "1m30s".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
        {
            return $"{(int)Math.Round(duration.TotalSeconds)}s";
        }

        var minutes = (int)duration.TotalMinutes;
        var seconds = duration.Seconds;
        return seconds == 0 ? $"{minutes}m" : $"{minutes}m{seconds}s";
    }

    /// <summary>The presets a fresh install ships with.</summary>
    public static IReadOnlyList<HotkeyAssignment> Defaults =>
    [
        new()
        {
            Action = HotkeyAction.SaveClip,
            Binding = new HotkeyBinding(VirtualKeys.F9, HotkeyModifiers.Alt),
            ClipDuration = TimeSpan.FromSeconds(15),
        },
        new()
        {
            Action = HotkeyAction.SaveClip,
            Binding = new HotkeyBinding(VirtualKeys.F10, HotkeyModifiers.Alt),
            ClipDuration = TimeSpan.FromSeconds(30),
        },
        new()
        {
            Action = HotkeyAction.SaveClip,
            Binding = new HotkeyBinding(VirtualKeys.F11, HotkeyModifiers.Alt),
            ClipDuration = TimeSpan.FromSeconds(60),
        },
        new()
        {
            Action = HotkeyAction.ToggleFullSessionRecording,
            Binding = new HotkeyBinding(VirtualKeys.F12, HotkeyModifiers.Alt),
        },
        new()
        {
            Action = HotkeyAction.Bookmark,
            Binding = new HotkeyBinding(VirtualKeys.F1, HotkeyModifiers.Alt),
        },
    ];
}
