namespace Frost.Engine.Overlay;

/// <summary>The fixed set of things the toast can say.</summary>
/// <remarks>
/// A closed set on purpose: each one is a bitmap rendered once at startup, so
/// showing a toast is a window-position call and nothing else. Anything that
/// needed text composed at the moment of showing — a filename, a duration, a
/// counter — would mean rendering while a game is in the foreground, which is
/// exactly what this design exists to avoid.
/// </remarks>
public enum ToastKind
{
    /// <summary>Shown the instant the hotkey fires, before the file exists.</summary>
    ClipSaving = 0,

    ClipSaved = 1,
    ClipFailed = 2,
    RecordingStarted = 3,
    RecordingStopped = 4,
    Bookmarked = 5,

    /// <summary>Nothing to clip yet — the buffer is still filling.</summary>
    NothingToClip = 6,
}

/// <summary>What the policy decided to do.</summary>
public enum ToastAction
{
    /// <summary>Show this toast; nothing was visible.</summary>
    Show = 0,

    /// <summary>Swap the visible image and restart the timer.</summary>
    Replace = 1,

    /// <summary>Already showing this; just restart the timer.</summary>
    ExtendCurrent = 2,

    /// <summary>Do nothing.</summary>
    Ignore = 3,
}

/// <summary>
/// Decides what the clip-saved toast shows and for how long.
/// </summary>
/// <remarks>
/// <para>The spec's constraint is that while a game has focus the only UI cost
/// allowed is "a static, pre-rendered image, on screen for under 1.5s". This type
/// is where that is enforced rather than hoped for: the duration is a constant
/// below the limit, a second toast replaces the first rather than stacking
/// another window, and the visible set is closed so every image can be rendered
/// once at startup.</para>
///
/// <para>The other rule it encodes is that feedback is tied to the
/// <i>keypress</i>, not to the file. <see cref="ToastKind.ClipSaving"/> goes up
/// immediately and is superseded by <see cref="ToastKind.ClipSaved"/> when the
/// write finishes — which is what makes the sub-150ms feedback budget reachable
/// without waiting on a disk.</para>
/// </remarks>
public sealed class ToastPolicy
{
    /// <summary>
    /// How long a toast stays up. Comfortably under the 1.5s ceiling, and long
    /// enough to read three words.
    /// </summary>
    public static TimeSpan VisibleDuration => TimeSpan.FromMilliseconds(1_200);

    /// <summary>The ceiling the spec sets. Asserted, not assumed.</summary>
    public static TimeSpan MaximumVisibleDuration => TimeSpan.FromMilliseconds(1_500);

    private readonly long _durationTicks;
    private readonly bool _enabled;

    private ToastKind? _visible;
    private long _shownAtTicks;
    private long _shown;
    private long _replaced;
    private long _suppressed;

    /// <param name="enabled">
    /// False when the user turned toasts off, in which case every decision is
    /// <see cref="ToastAction.Ignore"/> and no window is ever created.
    /// </param>
    /// <param name="visibleDuration">Override for tests; clamped to the ceiling.</param>
    public ToastPolicy(bool enabled = true, TimeSpan? visibleDuration = null)
    {
        _enabled = enabled;

        var duration = visibleDuration ?? VisibleDuration;

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleDuration), duration, "Must be positive.");
        }

        // Clamped rather than validated: a configuration that asked for three
        // seconds should get a compliant toast, not an exception at startup.
        _durationTicks = Math.Min(duration.Ticks, MaximumVisibleDuration.Ticks);
    }

    /// <summary>What is on screen, or null.</summary>
    public ToastKind? Visible => _visible;

    /// <summary>How long the toast stays up, after clamping.</summary>
    public TimeSpan EffectiveDuration => TimeSpan.FromTicks(_durationTicks);

    public long ToastsShown => _shown;

    /// <summary>Toasts that replaced one already on screen.</summary>
    public long ToastsReplaced => _replaced;

    /// <summary>Requests ignored because toasts are off.</summary>
    public long RequestsSuppressed => _suppressed;

    /// <summary>
    /// Decides what to do about a toast request at <paramref name="nowTicks"/>.
    /// </summary>
    public ToastAction Request(ToastKind kind, long nowTicks)
    {
        if (!_enabled)
        {
            _suppressed++;
            return ToastAction.Ignore;
        }

        if (_visible == kind)
        {
            // Same image already up: restart the timer and touch nothing else.
            _shownAtTicks = nowTicks;
            return ToastAction.ExtendCurrent;
        }

        var replacing = _visible is not null;

        _visible = kind;
        _shownAtTicks = nowTicks;
        _shown++;

        if (replacing)
        {
            _replaced++;

            // Replace, never stack. Two toasts on screen at once would be two
            // windows, and a queue of them could outlast the 1.5s ceiling between
            // them.
            return ToastAction.Replace;
        }

        return ToastAction.Show;
    }

    /// <summary>Whether the visible toast's time is up.</summary>
    public bool ShouldHide(long nowTicks) =>
        _visible is not null && nowTicks - _shownAtTicks >= _durationTicks;

    /// <summary>Records that the toast was hidden.</summary>
    public void NoteHidden()
    {
        _visible = null;
        _shownAtTicks = 0;
    }

    /// <summary>
    /// Ticks until the visible toast should be hidden, or null when nothing is
    /// showing.
    /// </summary>
    /// <remarks>
    /// Lets the overlay thread wait exactly as long as it needs to rather than
    /// polling, so an idle Engine does no work for the overlay at all.
    /// </remarks>
    public long? TicksUntilHide(long nowTicks)
    {
        if (_visible is null)
        {
            return null;
        }

        var remaining = _durationTicks - (nowTicks - _shownAtTicks);
        return remaining > 0 ? remaining : 0;
    }

    /// <summary>The toast for a finished clip.</summary>
    public static ToastKind ForClipResult(bool succeeded, bool nothingBuffered = false) =>
        nothingBuffered ? ToastKind.NothingToClip : succeeded ? ToastKind.ClipSaved : ToastKind.ClipFailed;

    /// <summary>The text each pre-rendered image carries. Short: it is read in passing.</summary>
    public static string TextFor(ToastKind kind) => kind switch
    {
        ToastKind.ClipSaving => "Saving clip…",
        ToastKind.ClipSaved => "Clip saved ✓",
        ToastKind.ClipFailed => "Clip failed ✕",
        ToastKind.RecordingStarted => "Recording ●",
        ToastKind.RecordingStopped => "Recording stopped",
        ToastKind.Bookmarked => "Bookmarked ⚑",
        ToastKind.NothingToClip => "Buffer still filling",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown toast."),
    };

    /// <summary>Every kind, so the overlay can pre-render the whole set at startup.</summary>
    public static IReadOnlyList<ToastKind> AllKinds { get; } =
        Enum.GetValues<ToastKind>();
}
