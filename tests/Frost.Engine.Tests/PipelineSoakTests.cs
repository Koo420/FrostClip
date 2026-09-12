using System.Diagnostics;
using Frost.Engine.Capture;
using Frost.Engine.Diagnostics;
using Frost.Engine.Pipeline;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// The runnable half of Phase 1's "capture runs 10 minutes with stable memory".
/// </summary>
/// <remarks>
/// The Windows half — a real WGC session against a real GPU — is
/// <c>Frost.Engine.exe --soak</c>. What is checkable on any host is that the
/// pipeline itself neither allocates nor leaks pool slots over a run of that
/// length, with a real producer thread and a real consumer thread, which is
/// where a leak in Frost's own code would live.
/// </remarks>
public sealed class PipelineSoakTests
{
    private const long Tps = FramePacer.TicksPerSecond;

    [Fact]
    public void TenMinutesOfFramesAtSixtyFpsNeitherAllocatesNorLeaksSlots()
    {
        const int fps = 60;
        const int minutes = 10;
        const long frames = fps * 60 * minutes;

        using var pool = new TexturePool(new FakeTextureAllocator(), 1920, 1080, 3);
        var pacer = new FramePacer(fps, TimeSpan.FromMilliseconds(500));
        var queue = new FrameQueue(6);
        var sink = new QueueingFrameSink(queue, pool);
        var router = new FrameRouter(pool, pacer, new NoOpCopier(), sink, 1920, 1080);

        var producedFrames = 0L;
        var consumedFrames = 0L;
        var producerDone = false;
        Exception? consumerFailure = null;

        var consumer = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref producerDone) || !queue.IsEmpty)
                {
                    if (sink.TryTake(out var frame))
                    {
                        sink.Complete(frame);
                        Interlocked.Increment(ref consumedFrames);
                        continue;
                    }

                    Thread.SpinWait(64);
                }
            }
            catch (Exception ex)
            {
                consumerFailure = ex;
            }
        })
        { IsBackground = true, Name = "soak-consumer" };

        // Warm up before measuring: JIT and first-touch are not a leak.
        var interval = Tps / fps;
        for (var i = 0; i < 2000; i++)
        {
            router.OfferCaptured(0xABCD, i * interval);
            while (sink.TryTake(out var warm))
            {
                sink.Complete(warm);
            }
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        consumer.Start();

        for (long i = 2000; i < frames; i++)
        {
            // Real capture is paced by a waitable timer, so it never outruns the
            // encoder by more than the pool depth. Here the producer is unpaced,
            // so wait for a slot rather than manufacturing drops the real
            // pipeline would not see. Available is a read-only scan of three
            // ints; it allocates nothing.
            while (pool.Available == 0)
            {
                Thread.SpinWait(32);
            }

            Assert.True(router.OfferCaptured(0xABCD, i * interval));
            Interlocked.Increment(ref producedFrames);
        }

        Volatile.Write(ref producerDone, true);
        Assert.True(consumer.Join(TimeSpan.FromSeconds(60)), "consumer did not drain");
        Assert.Null(consumerFailure);

        var producerAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Producer side is the capture thread's workload; it must be exactly zero.
        Assert.Equal(0, producerAllocated);

        // Every slot handed out must have come back, or capture would have
        // starved long before ten minutes were up.
        Assert.Equal(pool.Capacity, pool.Available);

        // The warm-up frames were consumed inline, before the consumer thread
        // started; everything after that went through the queue.
        Assert.Equal(router.FramesEmitted - 2000, Interlocked.Read(ref consumedFrames));
        Assert.Equal(frames, router.FramesEmitted);
        Assert.Equal(0, router.FramesDropped);
        Assert.Equal(frames - 2000, Interlocked.Read(ref producedFrames));
    }

    [Fact]
    public void StaticScreenForTenMinutesProducesFillersAtTheCeilingAndNoAllocation()
    {
        // The other long-run shape: nothing on screen changes, so WGC delivers
        // nothing and the pacer's filler path carries the whole run.
        using var pool = new TexturePool(new FakeTextureAllocator(), 1920, 1080, 3);
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        var queue = new FrameQueue(6);
        var sink = new QueueingFrameSink(queue, pool);
        var router = new FrameRouter(pool, pacer, new NoOpCopier(), sink, 1920, 1080);

        router.OfferCaptured(0xABCD, 0);
        Assert.True(sink.TryTake(out var first));
        sink.Complete(first);

        var pollInterval = Tps / 60;
        var totalTicks = 10 * 60 * Tps;

        // Warm-up poll before measuring.
        for (long t = 0; t < Tps; t += pollInterval)
        {
            router.OfferFillerIfDue(t);
            while (sink.TryTake(out var warm))
            {
                sink.Complete(warm);
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var t = Tps; t <= totalTicks; t += pollInterval)
        {
            router.OfferFillerIfDue(t);
            while (sink.TryTake(out var frame))
            {
                sink.Complete(frame);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(pool.Capacity, pool.Available);

        // 10 minutes at a 500ms ceiling is at most 1200 fillers — slightly fewer
        // in practice, because each filler's timestamp is the poll time and
        // polling is only every 16.7ms. The number that matters is that it is
        // nowhere near the 36,000 frames a naive "keep the frame rate up" filler
        // would have handed the encoder.
        Assert.InRange(router.FillerFrames, 1100, 1200);
        Assert.Equal(0, router.FramesDropped);
    }

    [Fact]
    public void MemoryTrackerCallsAFlatRunStableAndAGrowingOneNot()
    {
        var flat = new MemoryStabilityTracker(warmUpSamples: 1, workingSetProvider: () => 40L * 1024 * 1024);
        for (var i = 0; i <= 10; i++)
        {
            flat.Sample(TimeSpan.FromSeconds(i * 30));
        }

        Assert.True(flat.Report().IsStable);

        var growth = 40L * 1024 * 1024;
        var leaking = new MemoryStabilityTracker(
            warmUpSamples: 1,
            workingSetProvider: () => growth += 4L * 1024 * 1024);

        for (var i = 0; i <= 10; i++)
        {
            leaking.Sample(TimeSpan.FromSeconds(i * 30));
        }

        var report = leaking.Report();
        Assert.False(report.IsStable);
        Assert.True(report.WorkingSetDriftBytes > MemoryStabilityReport.DriftToleranceBytes);
    }

    [Fact]
    public void MemoryTrackerReportsTheSteadyStateAllocationRate()
    {
        var tracker = new MemoryStabilityTracker(warmUpSamples: 0, workingSetProvider: () => 1024);
        tracker.Sample(TimeSpan.Zero);
        tracker.Sample(TimeSpan.FromSeconds(10));

        var report = tracker.Report();
        Assert.Equal(TimeSpan.FromSeconds(10), report.Duration);
        Assert.Equal(2, report.SampleCount);
        Assert.True(report.SteadyStateBytesPerSecond >= 0);
    }

    [Fact]
    public void MemoryTrackerRefusesToReportWithoutSamples() =>
        Assert.Throws<InvalidOperationException>(() => new MemoryStabilityTracker().Report());

    [Fact]
    public void SustainedThroughputIsFarAboveWhatSixtyFpsNeeds()
    {
        // Not a benchmark, a sanity bound: if the handoff cannot comfortably
        // outpace 60fps on the test host it is not fit for the hot path.
        using var pool = new TexturePool(new FakeTextureAllocator(), 1920, 1080, 3);
        var pacer = new FramePacer(480, TimeSpan.FromSeconds(1));
        var queue = new FrameQueue(6);
        var sink = new QueueingFrameSink(queue, pool);
        var router = new FrameRouter(pool, pacer, new NoOpCopier(), sink, 1920, 1080);

        const int iterations = 500_000;
        var interval = Tps / 480;
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            router.OfferCaptured(0xABCD, i * interval);
            while (sink.TryTake(out var frame))
            {
                sink.Complete(frame);
            }
        }

        clock.Stop();
        var perSecond = iterations / clock.Elapsed.TotalSeconds;
        Assert.True(perSecond > 100_000, $"pipeline managed only {perSecond:F0} frames/s");
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
}
