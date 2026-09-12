namespace Frost.Engine.Pipeline;

/// <summary>What the pacer decided about an incoming frame.</summary>
public enum PacingDecision
{
    /// <summary>Pass it downstream.</summary>
    Emit = 0,

    /// <summary>Frames are arriving faster than the target rate; discard this one.</summary>
    DropTooSoon = 1,
}

/// <summary>
/// Decides which captured frames become encoded frames. Pure arithmetic over a
/// monotonic tick clock: no timers, no threads, no allocation, so it can be
/// tested exhaustively without a GPU or a compositor.
/// </summary>
/// <remarks>
/// Two jobs:
/// <list type="bullet">
/// <item>Rate limiting. A 240Hz game must not push 240 frames/s into a 60fps
/// encode. Deadlines advance by a fixed interval rather than from the last
/// accepted timestamp, so a 60fps source sampled at 60fps does not slowly drift
/// into dropping every other frame.</item>
/// <item>Gap bounding. WGC delivers nothing at all while the screen is static.
/// Without a ceiling on the sample interval the encoded timeline would contain
/// multi-second samples and "save the trailing 15 seconds" would stop being
/// accurate. <see cref="TryPlanFiller"/> reports when a no-new-content frame is
/// due.</item>
/// </list>
/// </remarks>
public sealed class FramePacer
{
    /// <summary>Ticks per second for all timestamps handled here (100ns units).</summary>
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private readonly long _intervalTicks;
    private readonly long _toleranceTicks;
    private readonly long _maxFrameIntervalTicks;

    private long _nextDeadlineTicks;
    private long _lastEmittedTicks;
    private bool _started;

    /// <param name="targetFps">Frames per second to pass downstream.</param>
    /// <param name="maxFrameInterval">
    /// Longest gap tolerated between emitted frames before a filler is due.
    /// </param>
    public FramePacer(int targetFps, TimeSpan maxFrameInterval)
    {
        if (targetFps is < 1 or > 480)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFps), targetFps, "TargetFps must be 1..480.");
        }

        if (maxFrameInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameInterval), maxFrameInterval, "Must be positive.");
        }

        TargetFps = targetFps;
        _intervalTicks = TicksPerSecond / targetFps;

        // Accept a frame that is up to half an interval early. A vsynced source
        // at exactly the target rate jitters either side of the deadline; without
        // slack roughly half of those frames would be dropped and the effective
        // capture rate would halve.
        _toleranceTicks = _intervalTicks / 2;

        _maxFrameIntervalTicks = Math.Max(maxFrameInterval.Ticks, _intervalTicks);
    }

    public int TargetFps { get; }

    /// <summary>Nominal gap between emitted frames, in 100ns ticks.</summary>
    public long IntervalTicks => _intervalTicks;

    /// <summary>Ceiling on the gap between emitted frames, in 100ns ticks.</summary>
    public long MaxFrameIntervalTicks => _maxFrameIntervalTicks;

    public long EmittedCount { get; private set; }

    public long DroppedCount { get; private set; }

    public long FillerCount { get; private set; }

    /// <summary>Timestamp of the last emitted frame, or 0 before the first.</summary>
    public long LastEmittedTicks => _lastEmittedTicks;

    /// <summary>
    /// Classifies a captured frame by its capture timestamp and, when the answer
    /// is <see cref="PacingDecision.Emit"/>, records it as emitted.
    /// </summary>
    public PacingDecision Consider(long timestampTicks)
    {
        if (!_started)
        {
            _started = true;
            Accept(timestampTicks);
            return PacingDecision.Emit;
        }

        if (timestampTicks < _nextDeadlineTicks - _toleranceTicks)
        {
            DroppedCount++;
            return PacingDecision.DropTooSoon;
        }

        Accept(timestampTicks);
        return PacingDecision.Emit;
    }

    /// <summary>
    /// Reports whether a filler frame is due as of <paramref name="nowTicks"/>,
    /// and if so records it as emitted at that timestamp.
    /// </summary>
    /// <remarks>
    /// Deliberately at most one filler per call: the caller polls, so a long
    /// static period produces fillers at the max-interval rate rather than a
    /// burst of catch-up frames nobody wants to encode.
    /// </remarks>
    public bool TryPlanFiller(long nowTicks, out long fillerTimestampTicks)
    {
        if (!_started || nowTicks - _lastEmittedTicks < _maxFrameIntervalTicks)
        {
            fillerTimestampTicks = 0;
            return false;
        }

        fillerTimestampTicks = nowTicks;
        FillerCount++;
        Accept(nowTicks);
        return true;
    }

    /// <summary>Forgets pacing state, e.g. after the capture target is resized.</summary>
    public void Reset()
    {
        _started = false;
        _nextDeadlineTicks = 0;
        _lastEmittedTicks = 0;
    }

    private void Accept(long timestampTicks)
    {
        EmittedCount++;
        _lastEmittedTicks = timestampTicks;

        var deadline = _nextDeadlineTicks + _intervalTicks;

        // If the source stalled (alt-tab, a loading screen, a dropped capture),
        // the fixed-cadence deadline is now far in the past. Resynchronise
        // instead of letting the pacer emit a catch-up burst.
        if (deadline < timestampTicks)
        {
            deadline = timestampTicks + _intervalTicks;
        }

        _nextDeadlineTicks = deadline;
    }
}
