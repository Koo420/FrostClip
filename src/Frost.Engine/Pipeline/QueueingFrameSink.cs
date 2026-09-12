using Frost.Engine.Capture;

namespace Frost.Engine.Pipeline;

/// <summary>
/// Hands captured frames to a consumer thread through a pre-allocated
/// <see cref="FrameQueue"/>.
/// </summary>
/// <remarks>
/// This is the capture→encode boundary. Capture's only obligation is to drop a
/// struct into the ring and return; if the ring is full the frame is refused,
/// which the router turns into a counted drop. Nothing here waits, allocates or
/// touches the disk.
/// <para>
/// The consumer signals completion by calling <see cref="Complete"/> with the
/// frame it finished, which returns the pooled texture. Ownership therefore moves
/// capture → queue → consumer → pool, with exactly one owner at a time.
/// </para>
/// </remarks>
public sealed class QueueingFrameSink : IFrameSink
{
    private readonly FrameQueue _queue;
    private readonly TexturePool _pool;
    private long _refused;

    public QueueingFrameSink(FrameQueue queue, TexturePool pool)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(pool);

        _queue = queue;
        _pool = pool;
    }

    /// <summary>Frames the queue had no room for.</summary>
    public long RefusedFrames => Interlocked.Read(ref _refused);

    /// <summary>Frames waiting for the consumer.</summary>
    public int Depth => _queue.Count;

    public bool TryAccept(in CapturedFrame frame)
    {
        if (_queue.TryEnqueue(frame))
        {
            return true;
        }

        Interlocked.Increment(ref _refused);
        return false;
    }

    /// <summary>Consumer side: takes the next frame, if any. Allocation-free.</summary>
    public bool TryTake(out CapturedFrame frame) => _queue.TryDequeue(out frame);

    /// <summary>
    /// Consumer side: releases a frame's texture back to the capture pool. Must be
    /// called exactly once per frame taken, or capture starves.
    /// </summary>
    public void Complete(in CapturedFrame frame) => _pool.Return(frame.SlotIndex);
}
