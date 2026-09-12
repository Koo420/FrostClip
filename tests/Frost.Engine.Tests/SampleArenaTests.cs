using Frost.Engine.Encoding;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class SampleArenaTests
{
    private static byte[] Pattern(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return bytes;
    }

    [Fact]
    public void CapacitiesAreRoundedUpToPowersOfTwo()
    {
        var arena = new SampleArena(byteCapacity: 5000, maxSamples: 100);
        Assert.Equal(8192, arena.ByteCapacity);
        Assert.Equal(128, arena.SampleCapacity);
    }

    [Theory]
    [InlineData(1024, 100)]
    [InlineData(1L << 35, 100)]
    [InlineData(8192, 1)]
    public void NonsenseSizesAreRejected(long byteCapacity, int maxSamples) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SampleArena(byteCapacity, maxSamples));

    [Fact]
    public void RoundTripsASampleByteForByte()
    {
        var arena = new SampleArena(8192, 16);
        var data = Pattern(500, 7);

        Assert.True(arena.TryAppend(data, 1000, 100, isKeyFrame: true, out var sample));

        Assert.Equal(500, sample.Length);
        Assert.Equal(1000, sample.TimestampTicks);
        Assert.True(sample.IsKeyFrame);
        Assert.Equal(1, arena.Count);
        Assert.Equal(500, arena.BytesUsed);

        var read = new byte[500];
        arena.CopyTo(sample, read);
        Assert.Equal(data, read);
    }

    [Fact]
    public void PreservesOrderAcrossManySamples()
    {
        var arena = new SampleArena(1 << 16, 64);

        for (var i = 0; i < 40; i++)
        {
            Assert.True(arena.TryAppend(Pattern(100, (byte)i), i * 1000, 1000, i % 10 == 0, out _));
        }

        Assert.Equal(40, arena.Count);

        var scratch = new byte[256];
        for (var i = 0; i < 40; i++)
        {
            var sample = arena[i];
            Assert.Equal(i * 1000, sample.TimestampTicks);
            Assert.Equal(Pattern(100, (byte)i), arena.Read(sample, scratch).ToArray());
        }
    }

    [Fact]
    public void ASampleThatWrapsPastTheEndIsStillReadBackIntact()
    {
        // The case a naive implementation gets wrong and only notices in the
        // field, as one corrupted frame every few minutes.
        var arena = new SampleArena(4096, 16);

        // Fill most of the arena, then drop it, so the write cursor sits near the
        // end and the next sample has to straddle the boundary.
        Assert.True(arena.TryAppend(Pattern(3900, 1), 0, 100, true, out var first));
        arena.DropOldest();
        _ = first;

        var straddling = Pattern(500, 200);
        Assert.True(arena.TryAppend(straddling, 1000, 100, false, out var sample));

        Assert.False(arena.TryGetContiguous(sample, out _));

        var read = new byte[500];
        arena.CopyTo(sample, read);
        Assert.Equal(straddling, read);

        var scratch = new byte[1024];
        Assert.Equal(straddling, arena.Read(sample, scratch).ToArray());
    }

    [Fact]
    public void ContiguousReadAvoidsTheCopyWhenItCan()
    {
        var arena = new SampleArena(8192, 16);
        var data = Pattern(300, 5);
        arena.TryAppend(data, 0, 100, true, out var sample);

        Assert.True(arena.TryGetContiguous(sample, out var span));
        Assert.Equal(data, span.ToArray());
    }

    [Fact]
    public void AppendRefusesWhenTheByteStorageIsFull()
    {
        var arena = new SampleArena(4096, 64);
        var data = Pattern(1000, 1);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(arena.TryAppend(data, i, 1, false, out _));
        }

        Assert.False(arena.TryAppend(data, 99, 1, false, out _));
        Assert.Equal(4, arena.Count);
    }

    [Fact]
    public void AppendRefusesWhenTheDescriptorRingIsFull()
    {
        var arena = new SampleArena(1 << 20, 4);

        for (var i = 0; i < arena.SampleCapacity; i++)
        {
            Assert.True(arena.TryAppend(Pattern(10, (byte)i), i, 1, false, out _));
        }

        Assert.False(arena.TryAppend(Pattern(10, 99), 99, 1, false, out _));
    }

    [Fact]
    public void EvictingAppendDropsTheOldestToMakeRoom()
    {
        // Ring-buffer behaviour: the newest frame always wins. A rolling buffer
        // that refused new frames when full would stop recording exactly when it
        // matters.
        var arena = new SampleArena(4096, 64);
        var data = Pattern(1000, 1);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(arena.TryAppendEvicting(data, i * 1000, 1000, false, out _));
        }

        Assert.True(arena.TryAppendEvicting(data, 4000, 1000, false, out _));
        Assert.Equal(4, arena.Count);
        Assert.Equal(1000, arena.OldestTimestampTicks);
        Assert.Equal(5000, arena.NewestEndTimestampTicks);
    }

    [Fact]
    public void EvictingAppendAlsoRecyclesDescriptorSlots()
    {
        var arena = new SampleArena(1 << 20, 4);

        for (var i = 0; i < 50; i++)
        {
            Assert.True(arena.TryAppendEvicting(Pattern(10, (byte)i), i, 1, false, out _));
        }

        Assert.Equal(arena.SampleCapacity, arena.Count);
        Assert.Equal(49, arena[arena.Count - 1].TimestampTicks);
    }

    [Fact]
    public void ASampleLargerThanTheWholeArenaIsRefusedRatherThanTruncated()
    {
        var arena = new SampleArena(4096, 16);
        var tooBig = new byte[arena.ByteCapacity + 1];

        Assert.False(arena.TryAppendEvicting(tooBig, 0, 1, true, out _));
        Assert.False(arena.TryAppend(tooBig, 0, 1, true, out _));
        Assert.True(arena.IsEmpty);
    }

    [Fact]
    public void EmptyAppendsAreRefused()
    {
        var arena = new SampleArena(4096, 16);
        Assert.False(arena.TryAppend([], 0, 1, true, out _));
        Assert.False(arena.TryAppendEvicting([], 0, 1, true, out _));
    }

    [Fact]
    public void ReadingAnEvictedSampleIsAnErrorRatherThanGarbage()
    {
        var arena = new SampleArena(4096, 16);
        arena.TryAppend(Pattern(1000, 1), 0, 1, true, out var first);

        for (var i = 0; i < 10; i++)
        {
            arena.TryAppendEvicting(Pattern(1000, (byte)i), i, 1, false, out _);
        }

        var destination = new byte[1000];
        Assert.Throws<InvalidOperationException>(() => arena.CopyTo(first, destination));
    }

    [Fact]
    public void CopyToRejectsATooSmallDestination()
    {
        var arena = new SampleArena(4096, 16);
        arena.TryAppend(Pattern(100, 1), 0, 1, true, out var sample);
        Assert.Throws<ArgumentException>(() => arena.CopyTo(sample, new byte[99]));
    }

    [Fact]
    public void DurationHeldTracksTheSampleTimestamps()
    {
        var arena = new SampleArena(1 << 16, 64);
        Assert.Equal(TimeSpan.Zero, arena.HeldDuration);

        var frameTicks = TimeSpan.TicksPerSecond / 60;
        for (var i = 0; i < 60; i++)
        {
            arena.TryAppend(Pattern(100, (byte)i), i * frameTicks, frameTicks, i == 0, out _);
        }

        // 60 frames of TicksPerSecond/60 each, which truncates a few ticks short
        // of a whole second - the arena reports what the timestamps actually say.
        Assert.Equal(60 * frameTicks, arena.HeldDuration.Ticks);
    }

    [Fact]
    public void PeekAndDropWorkAsAQueue()
    {
        var arena = new SampleArena(1 << 16, 16);
        for (var i = 0; i < 5; i++)
        {
            arena.TryAppend(Pattern(50, (byte)i), i, 1, false, out _);
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i, arena.Peek().TimestampTicks);
            arena.DropOldest();
        }

        Assert.True(arena.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => arena.Peek());
        Assert.Throws<InvalidOperationException>(() => arena.DropOldest());
    }

    [Fact]
    public void ClearDiscardsEverything()
    {
        var arena = new SampleArena(1 << 16, 16);
        arena.TryAppend(Pattern(100, 1), 0, 1, true, out _);
        arena.Clear();

        Assert.True(arena.IsEmpty);
        Assert.Equal(0, arena.BytesUsed);
        Assert.Equal(0, arena.Count);
    }

    [Fact]
    public void IndexerRejectsOutOfRange()
    {
        var arena = new SampleArena(1 << 16, 16);
        arena.TryAppend(Pattern(10, 1), 0, 1, true, out _);

        Assert.Throws<ArgumentOutOfRangeException>(() => arena[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => arena[-1]);
    }

    [Fact]
    public void SteadyStateAppendAndEvictDoNotAllocate()
    {
        var arena = new SampleArena(1 << 20, 256);
        var data = Pattern(4000, 3);
        var scratch = new byte[8192];

        for (var i = 0; i < 2000; i++)
        {
            arena.TryAppendEvicting(data, i, 1, i % 120 == 0, out var sample);
            arena.Read(sample, scratch);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200_000; i++)
        {
            arena.TryAppendEvicting(data, i, 1, i % 120 == 0, out var sample);
            arena.Read(sample, scratch);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ALongRunOfWrapsNeverCorruptsASample()
    {
        // Runs the byte cursor around the arena many times over with varying
        // sample sizes, checking every sample reads back exactly.
        var arena = new SampleArena(1 << 14, 64);
        var random = new Random(20260912);
        var scratch = new byte[4096];

        for (var i = 0; i < 20_000; i++)
        {
            var length = random.Next(1, 3000);
            var seed = (byte)random.Next(256);
            var data = new byte[length];
            for (var j = 0; j < length; j++)
            {
                data[j] = (byte)(seed + j);
            }

            Assert.True(arena.TryAppendEvicting(data, i, 1, false, out var sample));

            var read = arena.Read(sample, scratch);
            Assert.Equal(length, read.Length);
            for (var j = 0; j < length; j++)
            {
                Assert.Equal((byte)(seed + j), read[j]);
            }
        }
    }
}
