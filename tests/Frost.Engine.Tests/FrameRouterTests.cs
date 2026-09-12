using Frost.Engine.Capture;
using Frost.Engine.Pipeline;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class FrameRouterTests
{
    private const long Tps = FramePacer.TicksPerSecond;
    private const nint SourceTexture = 0xF00D;

    private sealed record Harness(
        TexturePool Pool,
        FramePacer Pacer,
        RecordingFrameCopier Copier,
        RecordingFrameSink Sink,
        FrameRouter Router) : IDisposable
    {
        public void Dispose() => Pool.Dispose();
    }

    private static Harness Build(int poolSize = 3, int fps = 60, int maxIntervalMs = 500, bool returnImmediately = true)
    {
        var pool = new TexturePool(new FakeTextureAllocator(), 1920, 1080, poolSize);
        var pacer = new FramePacer(fps, TimeSpan.FromMilliseconds(maxIntervalMs));
        var copier = new RecordingFrameCopier();
        var sink = new RecordingFrameSink(pool) { ReturnImmediately = returnImmediately };
        var router = new FrameRouter(pool, pacer, copier, sink, 1920, 1080);
        return new Harness(pool, pacer, copier, sink, router);
    }

    [Fact]
    public void CapturedFrameIsCopiedAndForwarded()
    {
        using var h = Build();

        Assert.True(h.Router.OfferCaptured(SourceTexture, 0));

        var frame = Assert.Single(h.Sink.Accepted);
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.False(frame.IsFiller);
        Assert.Equal(1, frame.SequenceNumber);
        Assert.Equal(SourceTexture, Assert.Single(h.Copier.SourceCopies).Source);
        Assert.Equal(frame.Texture, h.Copier.SourceCopies[0].Destination);
        Assert.Equal(1, h.Router.FramesEmitted);
        Assert.Equal(0, h.Router.FramesDropped);
    }

    [Fact]
    public void FramesArrivingFasterThanTargetAreDroppedWithoutCopying()
    {
        // The copy is the expensive part; a paced-out frame must not pay for it.
        using var h = Build();
        var sourceInterval = Tps / 240;

        for (var i = 0; i < 240; i++)
        {
            h.Router.OfferCaptured(SourceTexture, i * sourceInterval);
        }

        Assert.InRange(h.Router.FramesEmitted, 55, 65);
        Assert.Equal(240 - h.Router.FramesEmitted, h.Router.FramesDropped);
        Assert.Equal(h.Router.FramesEmitted, h.Copier.SourceCopies.Count);
    }

    [Fact]
    public void SequenceNumbersAreContiguousForEmittedFrames()
    {
        using var h = Build();
        var interval = Tps / 60;

        for (var i = 0; i < 50; i++)
        {
            h.Router.OfferCaptured(SourceTexture, i * interval);
        }

        Assert.Equal(
            Enumerable.Range(1, 50).Select(i => (long)i),
            h.Sink.Accepted.Select(f => f.SequenceNumber));
    }

    [Fact]
    public void FrameIsDroppedWhenEveryPoolSlotIsStillDownstream()
    {
        // Encoder falling behind. Capture must drop, never block: the compositor
        // is on the other end of this.
        using var h = Build(poolSize: 2, returnImmediately: false);
        var interval = Tps / 60;

        Assert.True(h.Router.OfferCaptured(SourceTexture, 0));
        Assert.True(h.Router.OfferCaptured(SourceTexture, interval));
        Assert.False(h.Router.OfferCaptured(SourceTexture, 2 * interval));

        Assert.Equal(2, h.Router.FramesEmitted);
        Assert.Equal(1, h.Router.FramesDropped);
        Assert.Equal(0, h.Pool.Available);

        h.Sink.ReturnAll();
        Assert.True(h.Router.OfferCaptured(SourceTexture, 3 * interval));
    }

    [Fact]
    public void RefusedFrameReturnsItsSlotToThePool()
    {
        // If a refused frame kept its slot, capture would starve after N frames.
        using var h = Build(poolSize: 2);
        h.Sink.RefuseEverything = true;
        var interval = Tps / 60;

        for (var i = 0; i < 20; i++)
        {
            Assert.False(h.Router.OfferCaptured(SourceTexture, i * interval));
        }

        Assert.Equal(2, h.Pool.Available);
        Assert.Equal(0, h.Router.FramesEmitted);
        Assert.Equal(20, h.Router.FramesDropped);

        h.Sink.RefuseEverything = false;
        Assert.True(h.Router.OfferCaptured(SourceTexture, 20 * interval));
    }

    [Fact]
    public void NoFillerBeforeAnyRealFrame()
    {
        using var h = Build();
        Assert.False(h.Router.OfferFillerIfDue(10 * Tps));
        Assert.Empty(h.Sink.Accepted);
    }

    [Fact]
    public void FillerReusesTheLastSlotWithNoCopyWhenItIsFree()
    {
        // This is the property that makes a static screen nearly free: the slot
        // handed back still holds the previous frame's pixels.
        using var h = Build(poolSize: 3);

        Assert.True(h.Router.OfferCaptured(SourceTexture, 0));
        var firstSlot = h.Sink.Accepted[0].SlotIndex;
        h.Sink.Accepted.Clear();

        Assert.True(h.Router.OfferFillerIfDue(TimeSpan.FromMilliseconds(500).Ticks));

        var filler = Assert.Single(h.Sink.Accepted);
        Assert.True(filler.IsFiller);
        Assert.Equal(firstSlot, filler.SlotIndex);
        Assert.Empty(h.Copier.PooledCopies);
    }

    [Fact]
    public void FillerCopiesFromTheLastSlotWhenItIsStillDownstream()
    {
        using var h = Build(poolSize: 3, returnImmediately: false);

        Assert.True(h.Router.OfferCaptured(SourceTexture, 0));
        var heldSlot = h.Sink.Accepted[0].SlotIndex;
        var heldHandle = h.Pool.HandleAt(heldSlot);

        Assert.True(h.Router.OfferFillerIfDue(TimeSpan.FromMilliseconds(500).Ticks));

        var filler = h.Sink.Accepted[^1];
        Assert.True(filler.IsFiller);
        Assert.NotEqual(heldSlot, filler.SlotIndex);

        var copy = Assert.Single(h.Copier.PooledCopies);
        Assert.Equal(heldHandle, copy.Source);
        Assert.Equal(filler.Texture, copy.Destination);
    }

    [Fact]
    public void FillersStopOnceRealFramesResume()
    {
        using var h = Build();
        var interval = Tps / 60;

        h.Router.OfferCaptured(SourceTexture, 0);
        Assert.True(h.Router.OfferFillerIfDue(TimeSpan.FromMilliseconds(500).Ticks));

        var resume = TimeSpan.FromMilliseconds(500).Ticks + interval;
        Assert.True(h.Router.OfferCaptured(SourceTexture, resume));
        Assert.False(h.Router.OfferFillerIfDue(resume + interval));

        Assert.Equal(1, h.Router.FillerFrames);
    }

    [Fact]
    public void SourceDropsAreCounted()
    {
        using var h = Build();
        h.Router.NoteSourceDrop();
        h.Router.NoteSourceDrop();
        Assert.Equal(2, h.Router.FramesDropped);
    }

    [Fact]
    public void RebindSwitchesToTheNewPoolAndGeometry()
    {
        using var h = Build();
        h.Router.OfferCaptured(SourceTexture, 0);
        h.Sink.Accepted.Clear();

        var resized = new TexturePool(new FakeTextureAllocator(), 1280, 720, 3);
        try
        {
            h.Router.Rebind(resized, 1280, 720);
            h.Sink.Pool = resized;

            // Pacing state is cleared with the rebind, so a filler is not due
            // until a fresh frame has been emitted.
            Assert.False(h.Router.OfferFillerIfDue(100 * Tps));

            Assert.True(h.Router.OfferCaptured(SourceTexture, 0));
            var frame = h.Sink.Accepted[^1];
            Assert.Equal(1280, frame.Width);
            Assert.Equal(720, frame.Height);
            Assert.Equal(resized.HandleAt(frame.SlotIndex), frame.Texture);
        }
        finally
        {
            resized.Dispose();
        }
    }

    [Fact]
    public void SteadyStateRoutingDoesNotAllocate()
    {
        // The whole point of the pipeline's shape: a frame moving from capture to
        // the encode queue must not touch the managed heap.
        var pool = new TexturePool(new FakeTextureAllocator(), 1920, 1080, 3);
        try
        {
            var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
            var router = new FrameRouter(pool, pacer, new NoOpCopier(), new ReturningSink(pool), 1920, 1080);
            var interval = Tps / 60;

            for (var i = 0; i < 2000; i++)
            {
                router.OfferCaptured(SourceTexture, i * interval);
                router.OfferFillerIfDue(i * interval);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 2000; i < 200_000; i++)
            {
                router.OfferCaptured(SourceTexture, i * interval);
                router.OfferFillerIfDue(i * interval);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            pool.Dispose();
        }
    }

    private sealed class NoOpCopier : IFrameCopier
    {
        public void CopyFromSource(nint sourceTexture, nint destinationTexture, int width, int height)
        {
        }

        public void CopyPooled(nint sourceTexture, nint destinationTexture, int width, int height)
        {
        }
    }

    private sealed class ReturningSink(TexturePool pool) : IFrameSink
    {
        public bool TryAccept(in CapturedFrame frame)
        {
            pool.Return(frame.SlotIndex);
            return true;
        }
    }
}
