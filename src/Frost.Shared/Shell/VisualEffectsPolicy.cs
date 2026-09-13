namespace Frost.Shared.Shell;

/// <summary>What the Shell window is doing, as far as cost is concerned.</summary>
public enum ShellActivity
{
    /// <summary>Foregrounded and being looked at. The only state that may cost anything.</summary>
    Foreground = 0,

    /// <summary>
    /// Open but not focused, with nothing demanding on screen — alt-tabbed to a
    /// browser, say.
    /// </summary>
    Background = 1,

    /// <summary>
    /// Open but not focused, and a game owns the foreground. The state the
    /// spec's "UI must cost nothing" rule is about.
    /// </summary>
    BackgroundWhileGaming = 2,

    /// <summary>Minimised, or in the tray. Nothing on screen at all.</summary>
    Hidden = 3,
}

/// <summary>
/// What the Shell may spend, given what it is doing.
/// </summary>
/// <param name="UseMica">Draw the Mica backdrop.</param>
/// <param name="UseAnimations">Run transitions and implicit animations.</param>
/// <param name="RenderThumbnails">Decode gallery thumbnails.</param>
/// <param name="StatusPollInterval">
/// How often to ask the Engine for status, or null to stop asking.
/// </param>
public readonly record struct VisualEffectsBudget(
    bool UseMica,
    bool UseAnimations,
    bool RenderThumbnails,
    TimeSpan? StatusPollInterval);

/// <summary>
/// Decides what the Shell is allowed to spend while a game is running.
/// </summary>
/// <remarks>
/// <para>This is the spec's sixth constraint in code: the UI must cost nothing
/// while a game has focus, and Mica, acrylic and animations may only be active
/// while the Shell window is foregrounded.</para>
///
/// <para>The part that is easy to miss is that the visible effects are not the
/// expensive part. Mica is composited by the system and costs little once the
/// window is not being redrawn. The real cost of a background Shell is a status
/// poll: a dashboard asking the Engine for its state every 250ms wakes two
/// processes, serialises JSON, and marshals to a UI thread four times a second
/// forever — while the user is playing and cannot see any of it. So
/// <see cref="StatusPollInterval"/> goes to null while a game has focus, and the
/// window refreshes when it is next activated instead.</para>
///
/// <para>How the Shell knows a game has focus is deliberately not decided here.
/// It is a compositor-level observation — the foreground window is not ours and
/// covers a monitor — made in the Windows layer and passed in as a flag. Frost
/// never injects into or reads from a game process, so there is nothing better
/// available and nothing better is wanted.</para>
/// </remarks>
public static class VisualEffectsPolicy
{
    /// <summary>How often the dashboard refreshes while it is being watched.</summary>
    public static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How often it refreshes while merely open in the background.
    /// </summary>
    /// <remarks>
    /// Slow enough to be free, frequent enough that a window brought forward is
    /// not showing a minute-old buffer length before its first refresh lands.
    /// </remarks>
    public static readonly TimeSpan BackgroundPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Works out the activity from what the window manager reports.
    /// </summary>
    /// <param name="isVisible">The window is not minimised and not hidden to tray.</param>
    /// <param name="isForeground">The window is the foreground window.</param>
    /// <param name="isGameInForeground">
    /// Something else owns the foreground and looks like a game — a
    /// compositor-level observation, never anything read from the game itself.
    /// </param>
    public static ShellActivity Classify(
        bool isVisible,
        bool isForeground,
        bool isGameInForeground)
    {
        if (!isVisible)
        {
            return ShellActivity.Hidden;
        }

        if (isForeground)
        {
            // Foreground wins even if something was flagged as a foreground
            // game: the flags disagree during an alt-tab, and treating our own
            // focused window as "a game is playing" would blank the UI the user
            // is looking at.
            return ShellActivity.Foreground;
        }

        return isGameInForeground
            ? ShellActivity.BackgroundWhileGaming
            : ShellActivity.Background;
    }

    /// <summary>What may be spent in a given state.</summary>
    /// <param name="activity">What the window is doing.</param>
    /// <param name="userWantsMica">The Appearance setting.</param>
    public static VisualEffectsBudget For(ShellActivity activity, bool userWantsMica = true) =>
        activity switch
        {
            ShellActivity.Foreground => new VisualEffectsBudget(
                UseMica: userWantsMica,
                UseAnimations: true,
                RenderThumbnails: true,
                StatusPollInterval: ForegroundPollInterval),

            // Visible but not focused: no backdrop and no animations, because
            // both mean redrawing a window nobody is reading. Thumbnails already
            // decoded stay on screen; new ones wait.
            ShellActivity.Background => new VisualEffectsBudget(
                UseMica: false,
                UseAnimations: false,
                RenderThumbnails: false,
                StatusPollInterval: BackgroundPollInterval),

            // The state the constraint is about. Nothing at all, including the
            // poll — which is the actual cost, not the backdrop.
            ShellActivity.BackgroundWhileGaming => new VisualEffectsBudget(
                UseMica: false,
                UseAnimations: false,
                RenderThumbnails: false,
                StatusPollInterval: null),

            ShellActivity.Hidden => new VisualEffectsBudget(
                UseMica: false,
                UseAnimations: false,
                RenderThumbnails: false,
                StatusPollInterval: null),

            _ => throw new ArgumentOutOfRangeException(
                nameof(activity),
                activity,
                "Unknown shell activity."),
        };

    /// <summary>
    /// Whether a state permits any per-frame or periodic work whatsoever.
    /// </summary>
    /// <remarks>
    /// The single question the spec's constraint reduces to, kept as one call so
    /// a new effect has an obvious thing to ask before it schedules anything.
    /// </remarks>
    public static bool MayDoRecurringWork(ShellActivity activity) =>
        activity == ShellActivity.Foreground || activity == ShellActivity.Background;

    /// <summary>
    /// Whether a freshly activated window should refresh immediately.
    /// </summary>
    /// <remarks>
    /// It must, and this is the other half of stopping the poll: coming back
    /// from a game, the dashboard holds whatever was true when the game took
    /// focus, and waiting up to half a second to correct it shows a stale buffer
    /// length as though it were current.
    /// </remarks>
    public static bool ShouldRefreshOnActivation(ShellActivity previous, ShellActivity current) =>
        current == ShellActivity.Foreground && previous != ShellActivity.Foreground;
}
