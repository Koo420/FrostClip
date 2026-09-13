using Frost.Shared.Ipc;

namespace Frost.Engine.Tray;

/// <summary>What a tray menu item does.</summary>
public enum TrayCommand
{
    /// <summary>Open the Shell window.</summary>
    OpenGallery = 0,

    /// <summary>Arm or disarm the ring buffer.</summary>
    ToggleArmed = 1,

    /// <summary>Start or stop full-session recording.</summary>
    ToggleSessionRecording = 2,

    /// <summary>Save a clip at the default preset length.</summary>
    SaveClip = 3,

    /// <summary>Open the Shell's settings page.</summary>
    OpenSettings = 4,

    /// <summary>Open the folder clips are saved to.</summary>
    OpenClipFolder = 5,

    /// <summary>Quit the Engine.</summary>
    Exit = 6,
}

/// <summary>One row in the tray menu.</summary>
/// <param name="Command">What it does.</param>
/// <param name="Text">Label to show.</param>
/// <param name="IsEnabled">Whether it can be clicked.</param>
/// <param name="IsDefault">
/// Whether it is the double-click action — bolded by Windows.
/// </param>
/// <param name="IsSeparator">True for a divider, in which case the rest is ignored.</param>
public readonly record struct TrayMenuItem(
    TrayCommand Command,
    string Text,
    bool IsEnabled = true,
    bool IsDefault = false,
    bool IsSeparator = false)
{
    /// <summary>A divider row.</summary>
    public static TrayMenuItem Separator => new(default, string.Empty, IsSeparator: true);
}

/// <summary>
/// Builds the tray menu from the Engine's state.
/// </summary>
/// <remarks>
/// <para>Separated from the Win32 menu code so the part that can be wrong — which
/// items appear, what they say, and when they are greyed out — is testable. The
/// platform code turns this list into a <c>HMENU</c> and nothing else.</para>
///
/// <para>The guiding rule is that the tray must never offer an action that would
/// fail. "Save clip" with nothing buffered, or "Stop recording" when nothing is
/// recording, are worse than absent: the user clicks, nothing happens, and they
/// have no way to know why.</para>
/// </remarks>
public static class TrayMenuModel
{
    /// <summary>Shortest buffer worth offering a clip from.</summary>
    public static TimeSpan MinimumBufferedForClip => TimeSpan.FromSeconds(1);

    /// <summary>
    /// The menu for a given state.
    /// </summary>
    /// <param name="status">Latest Engine status, or null when it is not running.</param>
    /// <param name="hasClipFolder">Whether a clip folder exists to open.</param>
    public static IReadOnlyList<TrayMenuItem> Build(EngineStatus? status, bool hasClipFolder = true)
    {
        var items = new List<TrayMenuItem>(8)
        {
            new(TrayCommand.OpenGallery, "Open Frost", IsDefault: true),
            TrayMenuItem.Separator,
        };

        if (status is null)
        {
            // The Engine is starting, or has faulted. Offering recording controls
            // that cannot work would just produce clicks that do nothing.
            items.Add(new TrayMenuItem(TrayCommand.SaveClip, "Save clip", IsEnabled: false));
            items.Add(new TrayMenuItem(TrayCommand.ToggleArmed, "Not recording", IsEnabled: false));
            items.Add(TrayMenuItem.Separator);
            items.Add(new TrayMenuItem(TrayCommand.OpenSettings, "Settings…"));
            items.Add(new TrayMenuItem(
                TrayCommand.OpenClipFolder, "Open clip folder", IsEnabled: hasClipFolder));
            items.Add(TrayMenuItem.Separator);
            items.Add(new TrayMenuItem(TrayCommand.Exit, "Exit Frost"));
            return items;
        }

        var buffered = TimeSpan.FromSeconds(status.BufferedSeconds);
        var canClip = status.IsArmed && buffered >= MinimumBufferedForClip;

        items.Add(new TrayMenuItem(
            TrayCommand.SaveClip,
            canClip ? $"Save clip ({FormatBuffered(buffered)} buffered)" : "Save clip",
            IsEnabled: canClip));

        items.Add(new TrayMenuItem(
            TrayCommand.ToggleSessionRecording,
            status.IsRecordingSession ? "Stop recording session" : "Record whole session",
            IsEnabled: status.FaultMessage is null));

        items.Add(new TrayMenuItem(
            TrayCommand.ToggleArmed,
            status.IsArmed ? "Pause clip buffer" : "Resume clip buffer",
            IsEnabled: status.FaultMessage is null));

        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(TrayCommand.OpenSettings, "Settings…"));
        items.Add(new TrayMenuItem(
            TrayCommand.OpenClipFolder, "Open clip folder", IsEnabled: hasClipFolder));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(TrayCommand.Exit, "Exit Frost"));

        return items;
    }

    /// <summary>
    /// The hover tooltip. Kept short: Windows truncates it, and it is read at a
    /// glance.
    /// </summary>
    public static string BuildTooltip(EngineStatus? status)
    {
        if (status is null)
        {
            return "Frost — engine not running";
        }

        if (status.FaultMessage is { Length: > 0 } fault)
        {
            // The fault is the only thing worth saying when there is one.
            return Truncate($"Frost — {fault}", 127);
        }

        if (status.IsRecordingSession)
        {
            return Truncate(
                $"Frost — recording session, {FormatBuffered(TimeSpan.FromSeconds(status.BufferedSeconds))} buffered",
                127);
        }

        return status.IsArmed
            ? Truncate(
                $"Frost — armed, {FormatBuffered(TimeSpan.FromSeconds(status.BufferedSeconds))} buffered", 127)
            : "Frost — paused";
    }

    /// <summary>"12s", "1m 30s", "5m".</summary>
    public static string FormatBuffered(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            return "0s";
        }

        if (duration.TotalSeconds < 60)
        {
            return $"{(int)duration.TotalSeconds}s";
        }

        var minutes = (int)duration.TotalMinutes;
        var seconds = duration.Seconds;
        return seconds == 0 ? $"{minutes}m" : $"{minutes}m {seconds}s";
    }

    /// <summary>Windows' tooltip limit is 128 characters including the terminator.</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
