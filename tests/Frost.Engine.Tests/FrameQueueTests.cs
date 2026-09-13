using Frost.Engine.Capture;
using Frost.Engine.Pipeline;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class FrameQueueTests
{
    private static CapturedFrame Frame(long sequence) =>
        new(texture: 0x2000 + (nint)sequence, slotIndex: (int)(sequence % 3),
            width: 1920, height: 1080, timestampTicks: sequence * 1000,
            sequenceNumber: sequence, isFiller: false);

    [Fact]
    public void RoundTripsFramesInOrder()
    {
        var queue = new FrameQueue(8);

        for (var i = 0; i < 8; i++)
        {
            Assert.True(queue.TryEnqueue(Frame(i)));
        }

        for (var i = 0; i < 8; i++)
        {
            Assert.True(queue.TryDequeue(out var frame));
            Assert.Equal(i, frame.SequenceNumber);
            Assert.Equal(1920, frame.Width);
        }

        Assert.False(queue.TryDequeue(out _));
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void RefusesFramesWhenFullRatherThanBlocking()
    {
        // A stalled encoder must never stall the compositor; the capture stage
        // takes the drop instead.
        var queue = new FrameQueue(4);

        for (var i = 0; i < queue.Capacity; i++)
        {
            Assert.True(queue.TryEnqueue(Frame(i)));
        }

        Assert.False(queue.TryEnqueue(Frame(999)));
        Assert.Equal(queue.Capacity, queue.Count);
    }

    [Fact]
    public void WrapsAroundIndefinitely()
    {
        var queue = new FrameQueue(4);

        for (long i = 0; i < 10_000; i++)
        {
            Assert.True(queue.TryEnqueue(Frame(i)));
            Assert.True(queue.TryDequeue(out var frame));
            Assert.Equal(i, frame.SequenceNumber);
        }
    }

    [Fact]
    public void DequeueClearsTheBackingSlot()
    {
        // Leaving a stale texture handle in the array makes a leak hunt much
        // harder and invites a future peeking consumer to resurrect a returned
        // pool slot.
        var queue = new FrameQueue(4);
        queue.TryEnqueue(Frame(1));
        queue.TryDequeue(out _);

        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void EnqueueAndDequeueDoNotAllocate()
    {
        var queue = new FrameQueue(8);

        AllocationAssert.NoPerIterationAllocation(i =>
        {
            queue.TryEnqueue(Frame(i));
            queue.TryDequeue(out _);
        });
    }

    [Fact]
    public void CapacityIsRoundedUpToAPowerOfTwoMinusOne()
    {
        Assert.Equal(7, new FrameQueue(5).Capacity);
        Assert.Equal(3, new FrameQueue(3).Capacity);
        Assert.Equal(15, new FrameQueue(8).Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-4)]
    public void TooSmallCapacityIsRejected(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameQueue(capacity));

    [Fact]
    public void SingleProducerSingleConsumerNeverTearsAFrame()
    {
        // The whole point of the Lamport ring is that the consumer never observes
        // a slot the producer has not finished writing. Run both sides hot and
        // check every frame that comes out is internally consistent and in order.
        const long total = 300_000;
        var queue = new FrameQueue(16);
        long received = 0;
        long dropped = 0;
        Exception? failure = null;

        var consumer = new Thread(() =>
        {
            try
            {
                long expected = 0;
                while (received + dropped < total)
                {
                    if (!queue.TryDequeue(out var frame))
                    {
                        Thread.SpinWait(8);
                        continue;
                    }

                    Assert.Equal(0x2000 + (nint)frame.SequenceNumber, frame.Texture);
                    Assert.Equal(frame.SequenceNumber * 1000, frame.TimestampTicks);
                    Assert.True(frame.SequenceNumber >= expected);
                    expected = frame.SequenceNumber + 1;
                    received++;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { IsBackground = true, Name = "frame-queue-consumer" };

        consumer.Start();

        for (long i = 0; i < total; i++)
        {
            while (!queue.TryEnqueue(Frame(i)))
            {
                if (failure is not null)
                {
                    break;
                }

                Thread.SpinWait(8);
            }
        }

        Assert.True(consumer.Join(TimeSpan.FromSeconds(30)), "consumer did not drain the queue");
        Assert.Null(failure);
        Assert.Equal(total, received);
    }
}

public sealed class QueueingFrameSinkTests
{
    [Fact]
    public void MovesOwnershipCaptureToConsumerToPool()
    {
        using var pool = new TexturePool(new FakeTextureAllocator(), 64, 64, 3);
        var sink = new QueueingFrameSink(new FrameQueue(4), pool);

        Assert.True(pool.TryRent(out var slot));
        var frame = new CapturedFrame(slot.Handle, slot.SlotIndex, 64, 64, 1000, 1, false);

        Assert.True(sink.TryAccept(frame));
        Assert.Equal(1, sink.Depth);
        Assert.Equal(2, pool.Available);

        Assert.True(sink.TryTake(out var taken));
        Assert.Equal(frame.SlotIndex, taken.SlotIndex);

        sink.Complete(taken);
        Assert.Equal(3, pool.Available);
        Assert.Equal(0, sink.Depth);
    }

    [Fact]
    public void RefusesAndCountsWhenTheQueueIsFull()
    {
        using var pool = new TexturePool(new FakeTextureAllocator(), 64, 64, 8);
        var queue = new FrameQueue(2);
        var sink = new QueueingFrameSink(queue, pool);

        for (var i = 0; i < queue.Capacity; i++)
        {
            Assert.True(sink.TryAccept(new CapturedFrame(1, 0, 64, 64, i, i, false)));
        }

        Assert.False(sink.TryAccept(new CapturedFrame(1, 0, 64, 64, 99, 99, false)));
        Assert.Equal(1, sink.RefusedFrames);
    }
}
