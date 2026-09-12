namespace Frost.Engine.Hotkeys;

/// <summary>
/// Decides when the global keyboard hook has been lost and should be
/// reinstalled.
/// </summary>
/// <remarks>
/// <para>Windows silently removes a low-level keyboard hook whose callback
/// exceeds <c>LowLevelHooksTimeout</c>, and hooks can also be lost across a UAC
/// prompt, a session switch or a lock/unlock. There is no notification for any
/// of it, so the only way to notice is to compare two clocks: the last time our
/// hook saw a key, and the last time Windows saw <i>any</i> input
/// (<c>GetLastInputInfo</c>). If the system has had input recently and we have
/// not, the hook is dead.</para>
///
/// <para>This matters more than it sounds: a silently dead hook means the
/// hotkeys stop working with no error anywhere, which from the user's side looks
/// like the application has simply stopped doing its job.</para>
///
/// <para>The logic is here, platform-agnostic, so it can be tested; the Windows
/// side only supplies the two timestamps.</para>
/// </remarks>
public sealed class HookWatchdog
{
    private readonly long _silenceThresholdTicks;
    private readonly long _minimumReinstallIntervalTicks;

    private long _lastHookEventTicks;
    private long _lastReinstallTicks;

    // A bool rather than "is the install timestamp zero": a monotonic clock can
    // legitimately read zero, and treating that as "never installed" would
    // disable the watchdog for a process that started at tick 0.
    private bool _installed;

    /// <param name="silenceThreshold">
    /// How much system input we must see without our hook seeing anything before
    /// concluding the hook is gone. Generous on purpose: a false positive
    /// reinstalls a working hook, which briefly drops keystroke observation.
    /// </param>
    /// <param name="minimumReinstallInterval">
    /// Floor between reinstall attempts, so a genuinely broken hook does not turn
    /// into a reinstall loop.
    /// </param>
    public HookWatchdog(
        TimeSpan? silenceThreshold = null,
        TimeSpan? minimumReinstallInterval = null)
    {
        var silence = silenceThreshold ?? TimeSpan.FromSeconds(20);
        var interval = minimumReinstallInterval ?? TimeSpan.FromSeconds(30);

        if (silence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceThreshold), silence, "Must be positive.");
        }

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumReinstallInterval), interval, "Must be positive.");
        }

        _silenceThresholdTicks = silence.Ticks;
        _minimumReinstallIntervalTicks = interval.Ticks;
    }

    public long ReinstallCount { get; private set; }

    /// <summary>Records that the hook is (re)installed as of <paramref name="nowTicks"/>.</summary>
    public void NoteInstalled(long nowTicks)
    {
        _installed = true;
        _lastHookEventTicks = nowTicks;
        _lastReinstallTicks = nowTicks;
    }

    /// <summary>Records that the hook delivered an event. Called from the hook callback.</summary>
    public void NoteHookEvent(long nowTicks) => _lastHookEventTicks = nowTicks;

    /// <summary>
    /// Whether the hook looks dead and should be reinstalled.
    /// </summary>
    /// <param name="nowTicks">Monotonic now.</param>
    /// <param name="lastSystemInputTicks">
    /// When Windows last saw any input, from <c>GetLastInputInfo</c>.
    /// </param>
    public bool ShouldReinstall(long nowTicks, long lastSystemInputTicks)
    {
        if (!_installed)
        {
            return false;
        }

        // Give a freshly installed hook time to see something before judging it.
        if (nowTicks - _lastReinstallTicks < _minimumReinstallIntervalTicks)
        {
            return false;
        }

        // The machine has been idle: our hook seeing nothing proves nothing.
        if (nowTicks - lastSystemInputTicks >= _silenceThresholdTicks)
        {
            return false;
        }

        // Someone has been typing, and none of it reached us.
        return lastSystemInputTicks - _lastHookEventTicks >= _silenceThresholdTicks;
    }

    /// <summary>Records a reinstall having happened.</summary>
    public void NoteReinstalled(long nowTicks)
    {
        ReinstallCount++;
        NoteInstalled(nowTicks);
    }
}
