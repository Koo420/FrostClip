using Frost.Engine.Capture;

namespace Frost.Engine.Pipeline;

/// <summary>
/// Single-producer / single-consumer bounded queue of <see cref="CapturedFrame"/>,
/// backed by one array allocated at construction.
/// </summary>
/// <remarks>
/// This is the capture→encode handoff. It must never allocate, never block and
/// never busy-wait, which rules out <c>BlockingCollection</c>,
/// <c>ConcurrentQueue</c> (segment allocation) and <c>Channel</c> (task
/// allocation per await). What is left is a classic Lamport ring: the producer
/// only ever writes <c>_tail</c>, the consumer only ever writes <c>_head</c>, and
/// the frame structs live in a pre-allocated array.
/// <para>
/// Capacity is rounded up to a power of two so the index wrap is a mask rather
/// than a modulo. One slot is always left empty to keep full and empty
/// distinguishable without a separate count.
/// </para>
/// <para>
/// Backpressure is explicit: <see cref="TryEnqueue"/> returns
/// <see langword="false"/> when full rather than waiting. Capture then returns
/// the texture slot and counts a drop, which is the correct behaviour — a
/// stalled encoder must never stall the compositor.
/// </para>
/// </remarks>
public sealed class FrameQueue
{
    private readonly CapturedFrame[] _slots;
    private readonly int _mask;

    // Padded apart so the producer's and consumer's cursors do not share a cache
    // line; false sharing here shows up directly as capture-thread jitter.
    private PaddedLong _tail;
    private PaddedLong _head;

    public FrameQueue(int capacity)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be >= 2.");
        }

        var rounded = RoundUpToPowerOfTwo(capacity + 1);
        _slots = new CapturedFrame[rounded];
        _mask = rounded - 1;
        Capacity = rounded - 1;
    }

    /// <summary>Maximum frames held at once.</summary>
    public int Capacity { get; }

    public int Count
    {
        get
        {
            var count = (int)(Volatile.Read(ref _tail.Value) - Volatile.Read(ref _head.Value));
            return count < 0 ? 0 : count;
        }
    }

    public bool IsEmpty => Volatile.Read(ref _tail.Value) == Volatile.Read(ref _head.Value);

    /// <summary>Producer side. Allocation-free, never blocks.</summary>
    public bool TryEnqueue(in CapturedFrame frame)
    {
        var tail = _tail.Value;
        var head = Volatile.Read(ref _head.Value);

        if (tail - head >= Capacity)
        {
            return false;
        }

        _slots[(int)(tail & _mask)] = frame;

        // Publish the payload before the cursor, or the consumer can read a slot
        // it has been told about but that has not been written yet.
        Volatile.Write(ref _tail.Value, tail + 1);
        return true;
    }

    /// <summary>Consumer side. Allocation-free, never blocks.</summary>
    public bool TryDequeue(out CapturedFrame frame)
    {
        var head = _head.Value;

        if (Volatile.Read(ref _tail.Value) == head)
        {
            frame = default;
            return false;
        }

        var index = (int)(head & _mask);
        frame = _slots[index];

        // Clear the slot so a stale texture handle cannot be mistaken for a live
        // frame by anything inspecting the backing array (diagnostics, a leak
        // hunt, or a future consumer that peeks).
        _slots[index] = default;

        Volatile.Write(ref _head.Value, head + 1);
        return true;
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        var result = 2;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Explicit, Size = 128)]
    private struct PaddedLong
    {
        [System.Runtime.InteropServices.FieldOffset(64)]
        public long Value;
    }
}
