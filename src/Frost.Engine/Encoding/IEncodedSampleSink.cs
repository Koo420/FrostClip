namespace Frost.Engine.Encoding;

/// <summary>
/// Where encoded frames go after the encoder: the ring buffer, the full-session
/// writer, or both.
/// </summary>
/// <remarks>
/// Takes a span rather than a <c>byte[]</c> so the encoder can hand over the
/// bytes it already has without copying them into a fresh array per frame. The
/// span is only valid for the duration of the call; a sink that needs to keep
/// the data copies it into its own pre-allocated storage.
/// <para>
/// Called on the encode thread. Implementations must not block on disk I/O or
/// allocate — see <see cref="IFrameSinkRules"/> for why.
/// </para>
/// </remarks>
public interface IEncodedSampleSink
{
    /// <summary>
    /// Accepts one encoded frame. Returns <see langword="false"/> if the sink
    /// could not take it, which the caller counts as a dropped sample rather
    /// than retrying.
    /// </summary>
    bool TryWrite(
        ReadOnlySpan<byte> data,
        long timestampTicks,
        long durationTicks,
        bool isKeyFrame);
}

/// <summary>
/// Documentation marker for the rules every hot-path sink obeys. Not implemented
/// by anything; it exists so the contract has one place to live.
/// </summary>
/// <remarks>
/// A sink on the encode thread may not:
/// <list type="bullet">
/// <item>allocate on the managed heap per sample — the arena is pre-allocated;</item>
/// <item>block on disk I/O — the full-session writer hands bytes to its own
/// writer thread rather than calling <c>Write</c> inline;</item>
/// <item>busy-wait — refusing a sample is always better than spinning.</item>
/// </list>
/// Breaking any of these shows up as a frame-time spike in the game, which is
/// the one failure this application cannot have.
/// </remarks>
public interface IFrameSinkRules;

/// <summary>Forwards every sample to several sinks.</summary>
/// <remarks>
/// This is what makes "ring buffer and full-session recording at the same time"
/// a single encode rather than two: the encoder writes once, and both consumers
/// see the same bytes.
/// </remarks>
public sealed class FanOutSampleSink : IEncodedSampleSink
{
    private readonly IEncodedSampleSink[] _sinks;

    public FanOutSampleSink(params IEncodedSampleSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        if (sinks.Length == 0)
        {
            throw new ArgumentException("At least one sink is required.", nameof(sinks));
        }

        if (Array.IndexOf(sinks, null) >= 0)
        {
            throw new ArgumentException("Sinks must not be null.", nameof(sinks));
        }

        _sinks = sinks;
    }

    /// <summary>
    /// Returns true if every sink took the sample. A refusal by one does not stop
    /// the others: losing a frame from the full-session file is not a reason to
    /// lose it from the clip buffer as well.
    /// </summary>
    public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
    {
        var allAccepted = true;

        for (var i = 0; i < _sinks.Length; i++)
        {
            if (!_sinks[i].TryWrite(data, timestampTicks, durationTicks, isKeyFrame))
            {
                allAccepted = false;
            }
        }

        return allAccepted;
    }
}
