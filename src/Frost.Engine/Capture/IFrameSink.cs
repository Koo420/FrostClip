namespace Frost.Engine.Capture;

/// <summary>
/// Where captured frames go. Implemented by the encode stage.
/// </summary>
/// <remarks>
/// Called on the capture thread. Implementations must not allocate, block on disk
/// or a lock held by anything slow, or busy-wait — the whole pipeline's frame
/// pacing depends on this returning promptly. Queue the frame and get out.
/// <para>
/// Declared as an interface rather than an event so that the call site is a
/// non-allocating interface dispatch instead of a delegate invocation list.
/// </para>
/// </remarks>
public interface IFrameSink
{
    /// <summary>
    /// Accept a frame. Return <see langword="true"/> if ownership of
    /// <see cref="CapturedFrame.SlotIndex"/> was taken (the sink will return the
    /// slot to the pool), <see langword="false"/> to refuse it, in which case the
    /// caller returns the slot and counts a drop.
    /// </summary>
    bool TryAccept(in CapturedFrame frame);
}
