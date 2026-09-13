using Frost.Engine.Encoding;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>Records what a sink was handed.</summary>
internal sealed class RecordingSampleSink : IEncodedSampleSink
{
    internal List<(byte[] Data, long Timestamp, long Duration, bool IsKeyFrame)> Written { get; } = [];

    internal bool Refuse { get; set; }

    public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
    {
        if (Refuse)
        {
            return false;
        }

        Written.Add((data.ToArray(), timestampTicks, durationTicks, isKeyFrame));
        return true;
    }
}

public sealed class FanOutSampleSinkTests
{
    [Fact]
    public void EverySinkSeesTheSameBytes()
    {
        // This is what "one encode, two destinations" means in practice: the ring
        // buffer and the full-session file get the same encoded frame, not two
        // encodes of it.
        var ring = new RecordingSampleSink();
        var file = new RecordingSampleSink();
        var fanOut = new FanOutSampleSink(ring, file);

        var data = new byte[] { 1, 2, 3, 4, 5 };
        Assert.True(fanOut.TryWrite(data, 1000, 100, isKeyFrame: true));

        Assert.Equal(data, Assert.Single(ring.Written).Data);
        Assert.Equal(data, Assert.Single(file.Written).Data);
        Assert.True(file.Written[0].IsKeyFrame);
        Assert.Equal(1000, file.Written[0].Timestamp);
    }

    [Fact]
    public void OneSinkRefusingDoesNotDenyTheOthers()
    {
        // Losing a frame from the full-session file is not a reason to lose it
        // from the clip buffer as well.
        var ring = new RecordingSampleSink();
        var file = new RecordingSampleSink { Refuse = true };
        var fanOut = new FanOutSampleSink(ring, file);

        Assert.False(fanOut.TryWrite([9], 1, 1, false));
        Assert.Single(ring.Written);
        Assert.Empty(file.Written);
    }

    [Fact]
    public void AtLeastOneSinkIsRequired() =>
        Assert.Throws<ArgumentException>(() => new FanOutSampleSink());

    [Fact]
    public void NullSinksAreRejected() =>
        Assert.Throws<ArgumentException>(() => new FanOutSampleSink(new RecordingSampleSink(), null!));

    [Fact]
    public void FanOutDoesNotAllocatePerSample()
    {
        var fanOut = new FanOutSampleSink(new NoOpSink(), new NoOpSink(), new NoOpSink());
        var data = new byte[4096];

        AllocationAssert.NoPerIterationAllocation(i => fanOut.TryWrite(data, i, 1, i % 120 == 0));
    }

    private sealed class NoOpSink : IEncodedSampleSink
    {
        public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame) =>
            true;
    }
}
