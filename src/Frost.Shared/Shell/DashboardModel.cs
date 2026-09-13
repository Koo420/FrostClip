using Frost.Shared.Ipc;
using Frost.Shared.Settings;

namespace Frost.Shared.Shell;

/// <summary>What the dashboard's status line says, at a glance.</summary>
public enum EngineHealth
{
    /// <summary>No Engine on the other end of the pipe.</summary>
    Disconnected = 0,

    /// <summary>
    /// The Engine is running but cannot record — no hardware encoder, capture
    /// target gone, disk full. <see cref="DashboardModel.FaultMessage"/> says why.
    /// </summary>
    Faulted = 1,

    /// <summary>Connected and healthy, but the ring buffer is not running.</summary>
    Idle = 2,

    /// <summary>The ring buffer is running, so a clip hotkey would save something.</summary>
    Armed = 3,
}

/// <summary>One quick-clip button on the dashboard.</summary>
/// <param name="RequestedSeconds">The preset's duration.</param>
/// <param name="AvailableSeconds">
/// What pressing it would actually save right now — the preset clamped to what
/// the buffer holds.
/// </param>
/// <param name="Label">Button text, from the hotkey assignment.</param>
/// <param name="IsEnabled">False when there is nothing to save at all.</param>
/// <param name="IsShortOfRequested">
/// The buffer holds less than the preset asks for. The UI says so rather than
/// letting someone press "last 60 seconds" and get eleven.
/// </param>
public readonly record struct QuickClipOption(
    double RequestedSeconds,
    double AvailableSeconds,
    string Label,
    bool IsEnabled,
    bool IsShortOfRequested);

/// <summary>
/// Projects the Engine's status into everything the dashboard shows and every
/// button it enables.
/// </summary>
/// <remarks>
/// <para>This is deliberately a pure projection with no WinUI in it, for two
/// reasons. The first is that it can be tested on any host, which matters because
/// the Shell's XAML cannot be compiled on the build host. The second is that the
/// interesting bugs on a dashboard are not layout — they are what it claims when
/// the thing it is describing is absent or broken. A status panel that shows
/// "1920x1080 60fps, 0 clips" because the status record was null reads as working
/// hardware; a clip button that stays enabled while the Engine is faulted invites
/// a press that silently does nothing.</para>
///
/// <para>So the null status is a first-class state, not a defaulted record, and
/// every number is <c>null</c> rather than zero when it is unknown.</para>
/// </remarks>
public sealed record DashboardModel
{
    private DashboardModel()
    {
    }

    public EngineHealth Health { get; private init; }

    /// <summary>A full-session recording is in progress.</summary>
    /// <remarks>
    /// Orthogonal to <see cref="Health"/> rather than a value of it: the spec
    /// requires the ring buffer and a full-session recording to run at the same
    /// time, so "armed" and "recording" are not alternatives.
    /// </remarks>
    public bool IsRecordingSession { get; private init; }

    /// <summary>Set when <see cref="Health"/> is <see cref="EngineHealth.Faulted"/>.</summary>
    public string? FaultMessage { get; private init; }

    /// <summary>Trailing footage held right now. Null when not connected.</summary>
    public double? BufferedSeconds { get; private init; }

    /// <summary>The longest clip the buffer could currently produce.</summary>
    public double? MaxClipSeconds { get; private init; }

    /// <summary>
    /// How full the ring buffer is, 0 to 1, for a progress bar. Null when unknown.
    /// </summary>
    public double? BufferFill { get; private init; }

    public long? BufferBytes { get; private init; }

    /// <summary>e.g. "1920 x 1080 @ 60" — null when not connected.</summary>
    public string? CaptureDescription { get; private init; }

    /// <summary>e.g. "NVIDIA NVENC H264 @ 25.0 Mb/s".</summary>
    public string? EncoderDescription { get; private init; }

    public long? ClipsSaved { get; private init; }

    public string? SessionFilePath { get; private init; }

    public TimeSpan? Uptime { get; private init; }

    /// <summary>
    /// Fraction of captured frames that were dropped, or null if nothing has been
    /// captured yet.
    /// </summary>
    public double? DropRate { get; private init; }

    /// <summary>
    /// Whether the drop rate is worth showing a warning for.
    /// </summary>
    /// <remarks>
    /// Deliberately not "any dropped frame at all": WGC legitimately drops a
    /// frame around a mode switch or a fullscreen transition, and a dashboard
    /// that cries wolf on the first one trains people to ignore it.
    /// </remarks>
    public bool IsDroppingFrames { get; private init; }

    /// <summary>Saving a clip from the buffer would produce something.</summary>
    public bool CanSaveClip { get; private init; }

    public bool CanArmBuffer { get; private init; }

    public bool CanDisarmBuffer { get; private init; }

    public bool CanStartSessionRecording { get; private init; }

    public bool CanStopSessionRecording { get; private init; }

    /// <summary>Threshold for <see cref="IsDroppingFrames"/>.</summary>
    public const double DropRateWarningThreshold = 0.01;

    /// <summary>The dashboard with no Engine to describe.</summary>
    public static DashboardModel Disconnected { get; } = new()
    {
        Health = EngineHealth.Disconnected,
    };

    /// <summary>
    /// Builds the dashboard from a status message, or from its absence.
    /// </summary>
    /// <param name="status">
    /// The last status the Engine sent, or null if the Shell is not connected.
    /// </param>
    public static DashboardModel From(EngineStatus? status)
    {
        if (status is null)
        {
            return Disconnected;
        }

        var faulted = !string.IsNullOrWhiteSpace(status.FaultMessage);

        var health = faulted
            ? EngineHealth.Faulted
            : status.IsArmed ? EngineHealth.Armed : EngineHealth.Idle;

        var buffered = Math.Max(0, status.BufferedSeconds);
        var maxClip = Math.Max(0, status.MaxClipSeconds);

        var captured = Math.Max(0, status.FramesCaptured);
        var dropped = Math.Max(0, status.FramesDropped);
        var presented = captured + dropped;
        double? dropRate = presented == 0 ? null : (double)dropped / presented;

        return new DashboardModel
        {
            Health = health,
            IsRecordingSession = status.IsRecordingSession,
            FaultMessage = faulted ? status.FaultMessage : null,
            BufferedSeconds = buffered,
            MaxClipSeconds = maxClip,
            BufferFill = maxClip <= 0 ? null : Math.Clamp(buffered / maxClip, 0, 1),
            BufferBytes = status.BufferBytes,
            CaptureDescription = DescribeCapture(status),
            EncoderDescription = DescribeEncoder(status),
            ClipsSaved = status.ClipsSaved,
            SessionFilePath = status.SessionFilePath,
            Uptime = TimeSpan.FromSeconds(Math.Max(0, status.UptimeSeconds)),
            DropRate = dropRate,
            IsDroppingFrames = dropRate > DropRateWarningThreshold,

            // A faulted Engine cannot encode, so nothing that needs the encoder
            // is offered — including when it still reports itself armed, which it
            // does when the fault arrived after arming.
            CanSaveClip = !faulted && status.IsArmed && buffered > 0,
            CanArmBuffer = !faulted && !status.IsArmed,
            CanDisarmBuffer = status.IsArmed,
            CanStartSessionRecording = !faulted && !status.IsRecordingSession,
            CanStopSessionRecording = status.IsRecordingSession,
        };
    }

    private static string? DescribeCapture(EngineStatus status)
    {
        if (status.CaptureWidth <= 0 || status.CaptureHeight <= 0)
        {
            return null;
        }

        var size = $"{status.CaptureWidth} x {status.CaptureHeight}";
        return status.CaptureFps > 0 ? $"{size} @ {status.CaptureFps}" : size;
    }

    private static string? DescribeEncoder(EngineStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.EncoderName))
        {
            return null;
        }

        var parts = status.EncoderName;

        if (!string.IsNullOrWhiteSpace(status.Codec))
        {
            parts += $" {status.Codec}";
        }

        if (status.BitsPerSecond > 0)
        {
            parts += $" @ {status.BitsPerSecond / 1_000_000.0:0.#} Mb/s";
        }

        return parts;
    }

    /// <summary>
    /// The quick-clip buttons, taken from the durations the user actually has
    /// bound rather than from a hardcoded row of presets.
    /// </summary>
    /// <remarks>
    /// Two decisions worth keeping. The buttons mirror the hotkeys, so the
    /// dashboard and the keyboard never disagree about what "clip" means; and a
    /// preset longer than the buffer currently holds stays pressable but reports
    /// what it would really save, because a greyed-out button thirty seconds into
    /// a session looks like a broken app rather than a buffer that is still
    /// filling.
    /// </remarks>
    public IReadOnlyList<QuickClipOption> QuickClipOptions(FrostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var buffered = BufferedSeconds ?? 0;
        var options = new List<QuickClipOption>();
        var seen = new HashSet<double>();

        foreach (var hotkey in settings.Hotkeys)
        {
            if (!hotkey.Enabled || (HotkeyAction)hotkey.Action != HotkeyAction.SaveClip)
            {
                continue;
            }

            var requested = hotkey.ClipSeconds ?? 0;

            if (requested <= 0 || !seen.Add(requested))
            {
                continue;
            }

            var available = Math.Min(requested, buffered);

            options.Add(new QuickClipOption(
                RequestedSeconds: requested,
                AvailableSeconds: available,
                Label: string.IsNullOrWhiteSpace(hotkey.Label)
                    ? FormatDuration(requested)
                    : hotkey.Label,
                IsEnabled: CanSaveClip,
                IsShortOfRequested: available < requested));
        }

        options.Sort((a, b) => a.RequestedSeconds.CompareTo(b.RequestedSeconds));
        return options;
    }

    /// <summary>Renders a duration the way the buttons and status line want it.</summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds))
        {
            return "0s";
        }

        var span = TimeSpan.FromSeconds(seconds);

        if (span.TotalSeconds < 60)
        {
            return $"{(int)Math.Round(span.TotalSeconds)}s";
        }

        if (span.TotalHours < 1)
        {
            return span.Seconds == 0
                ? $"{span.Minutes}m"
                : $"{span.Minutes}m {span.Seconds}s";
        }

        return span.Minutes == 0
            ? $"{(int)span.TotalHours}h"
            : $"{(int)span.TotalHours}h {span.Minutes}m";
    }
}
