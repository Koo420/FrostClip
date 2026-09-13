namespace Frost.Shared.Shell;

/// <summary>Why a trim was refused.</summary>
public enum TrimRejection
{
    None = 0,

    /// <summary>The end is at or before the start.</summary>
    EmptyRange = 1,

    /// <summary>Start or end lies outside the clip.</summary>
    OutOfBounds = 2,

    /// <summary>
    /// The result would be shorter than <see cref="TrimPlanner.MinimumDurationTicks"/>.
    /// </summary>
    TooShort = 3,

    /// <summary>
    /// The clip has no keyframe index, so a re-mux cannot be cut accurately.
    /// </summary>
    NoKeyFrames = 4,
}

/// <summary>
/// A trim that has been checked, with the range a re-mux would actually produce.
/// </summary>
/// <param name="RequestedStartTicks">Where the user put the left handle.</param>
/// <param name="RequestedEndTicks">Where the user put the right handle.</param>
/// <param name="StartTicks">
/// The keyframe at or before the requested start. A re-mux cannot begin
/// mid-GOP, so this is where the output really starts.
/// </param>
/// <param name="EndTicks">Where the output really ends.</param>
public readonly record struct TrimRange(
    long RequestedStartTicks,
    long RequestedEndTicks,
    long StartTicks,
    long EndTicks)
{
    public TimeSpan Start => TimeSpan.FromTicks(StartTicks);

    public TimeSpan End => TimeSpan.FromTicks(EndTicks);

    public TimeSpan Duration => TimeSpan.FromTicks(EndTicks - StartTicks);

    /// <summary>
    /// How much earlier the output starts than the user asked for, because of
    /// keyframe alignment.
    /// </summary>
    public TimeSpan StartDrift => TimeSpan.FromTicks(RequestedStartTicks - StartTicks);

    /// <summary>Whether alignment moved either handle.</summary>
    public bool IsAligned =>
        StartTicks == RequestedStartTicks && EndTicks == RequestedEndTicks;
}

/// <summary>Whether a trim can go ahead, and what it would produce.</summary>
public readonly record struct TrimCheck(
    TrimRejection Rejection,
    TrimRange? Range,
    string? Message)
{
    public bool IsAllowed => Rejection == TrimRejection.None;
}

/// <summary>
/// Works out what a quick trim would produce, given that it re-muxes rather
/// than re-encodes.
/// </summary>
/// <remarks>
/// <para>The spec asks for "quick trim with re-mux not re-encode", and that
/// constraint is the whole of this type. A re-mux copies encoded samples
/// untouched into a new container, which is why it is instant and lossless — but
/// it also means the output can only begin on a keyframe. Every frame after a
/// keyframe is coded as a difference from the frames before it, so starting a
/// copy in the middle of a GOP produces a clip whose first second is
/// unwatchable garbage.</para>
///
/// <para>So the left handle snaps *backwards* to the nearest keyframe at or
/// before it. Backwards, not to the nearest: snapping forwards would silently
/// discard the moment the user was trying to keep, which is the one thing a trim
/// UI must never do. The extra footage at the front is harmless, and
/// <see cref="TrimRange.StartDrift"/> lets the UI say so.</para>
///
/// <para>The right handle needs no alignment — a decoder stops wherever the
/// samples stop — so it is only clamped to the clip.</para>
/// </remarks>
public static class TrimPlanner
{
    /// <summary>
    /// Shortest clip a trim may produce. Half a second: below that the output is
    /// a single GOP with nothing in it worth keeping, and most players will not
    /// show a seek bar at all.
    /// </summary>
    public const long MinimumDurationTicks = TimeSpan.TicksPerSecond / 2;

    /// <summary>
    /// Plans a trim.
    /// </summary>
    /// <param name="durationTicks">Length of the source clip.</param>
    /// <param name="keyFrameTicks">
    /// Keyframe timestamps in ascending order, the first of which should be 0.
    /// </param>
    /// <param name="requestedStartTicks">Left handle.</param>
    /// <param name="requestedEndTicks">Right handle.</param>
    public static TrimCheck Check(
        long durationTicks,
        IReadOnlyList<long> keyFrameTicks,
        long requestedStartTicks,
        long requestedEndTicks)
    {
        ArgumentNullException.ThrowIfNull(keyFrameTicks);

        if (durationTicks <= 0)
        {
            return Refuse(TrimRejection.OutOfBounds, "This clip has no length recorded.");
        }

        if (requestedStartTicks < 0 || requestedEndTicks > durationTicks)
        {
            return Refuse(
                TrimRejection.OutOfBounds,
                "The trim range has to be inside the clip.");
        }

        if (requestedEndTicks <= requestedStartTicks)
        {
            return Refuse(TrimRejection.EmptyRange, "The end has to come after the start.");
        }

        if (keyFrameTicks.Count == 0)
        {
            return Refuse(
                TrimRejection.NoKeyFrames,
                "Frost cannot trim this clip losslessly — it has no keyframe index.");
        }

        var start = KeyFrameAtOrBefore(keyFrameTicks, requestedStartTicks);

        if (start is null)
        {
            // Every keyframe is after the requested start, which means the index
            // does not begin at zero. Rather than guess, refuse: silently
            // starting at the first keyframe could drop seconds the user kept.
            return Refuse(
                TrimRejection.NoKeyFrames,
                "This clip's keyframe index does not cover the selected start.");
        }

        var range = new TrimRange(
            RequestedStartTicks: requestedStartTicks,
            RequestedEndTicks: requestedEndTicks,
            StartTicks: start.Value,
            EndTicks: requestedEndTicks);

        if (range.EndTicks - range.StartTicks < MinimumDurationTicks)
        {
            return Refuse(
                TrimRejection.TooShort,
                "A trimmed clip has to be at least half a second long.");
        }

        return new TrimCheck(TrimRejection.None, range, null);
    }

    /// <summary>
    /// The keyframe a re-mux would have to start from, or null if there is none
    /// at or before the requested point.
    /// </summary>
    /// <remarks>
    /// Binary search rather than a scan: a ten-minute session recording at a
    /// two-second keyframe interval has three hundred entries, and the trim UI
    /// calls this on every drag of the handle.
    /// </remarks>
    public static long? KeyFrameAtOrBefore(IReadOnlyList<long> keyFrameTicks, long ticks)
    {
        ArgumentNullException.ThrowIfNull(keyFrameTicks);

        var low = 0;
        var high = keyFrameTicks.Count - 1;
        long? best = null;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);

            if (keyFrameTicks[middle] <= ticks)
            {
                best = keyFrameTicks[middle];
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    /// <summary>
    /// What the UI says under the trim handles.
    /// </summary>
    /// <remarks>
    /// Reporting the drift matters more than it looks: a lossless trim that
    /// quietly includes two extra seconds looks like a bug, where "starts 1.4s
    /// earlier to keep it lossless" reads as a deliberate trade the user can
    /// accept or work around.
    /// </remarks>
    public static string Describe(TrimRange range)
    {
        var duration = DashboardModel.FormatDuration(range.Duration.TotalSeconds);

        if (range.IsAligned)
        {
            return duration;
        }

        var drift = range.StartDrift.TotalSeconds;

        return $"{duration} — starts {drift:0.#}s earlier to keep it lossless";
    }

    private static TrimCheck Refuse(TrimRejection rejection, string message) =>
        new(rejection, null, message);
}
