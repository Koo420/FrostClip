using System.Diagnostics;
using Frost.Engine.Capture;
using Frost.Engine.Diagnostics;
using Frost.Engine.Pipeline;

namespace Frost.Engine.Windows;

/// <summary>
/// The manual smoke test from Phase 1: run capture for a while and report
/// whether memory and frame delivery stayed flat.
/// </summary>
/// <remarks>
/// Invoked as <c>Frost.Engine.exe --soak [minutes]</c>. It exercises the real
/// capture path — WGC session, texture pool, pacer, SPSC queue — with a consumer
/// that does nothing but return textures, so anything that grows is capture's
/// fault and not the encoder's. Exit code 0 means stable.
/// </remarks>
internal static class CaptureSoakTest
{
    internal static int Run(CaptureConfiguration config, TimeSpan duration, IEngineLog log)
    {
        using var device = GraphicsDevice.Create(config.Target, log);
        using var source = new WgcCaptureSource(config, device, log);

        // Capacity generous enough that a scheduling hiccup on the consumer does
        // not show up as a capture drop and muddy the result.
        var queue = new FrameQueue(config.TexturePoolSize * 2);
        source.Start(new EnqueueOnlySink(queue));

        var consumerStop = false;
        var consumed = 0L;
        var consumer = new Thread(() =>
        {
            while (!Volatile.Read(ref consumerStop))
            {
                if (queue.TryDequeue(out var frame))
                {
                    // Stand-in for encode: take ownership, release it immediately.
                    source.CurrentTexturePool?.Return(frame.SlotIndex);
                    Interlocked.Increment(ref consumed);
                    continue;
                }

                Thread.Sleep(1);
            }
        })
        { IsBackground = true, Name = "frost-soak-consumer" };

        consumer.Start();

        var tracker = new MemoryStabilityTracker(warmUpSamples: 3);
        var clock = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds / 20, 5, 30));
        var nextSample = interval;
        var lastEmitted = 0L;

        log.Info($"Soaking capture for {duration:hh\\:mm\\:ss}, sampling every {interval.TotalSeconds:F0}s.");

        while (clock.Elapsed < duration)
        {
            Thread.Sleep(250);

            if (clock.Elapsed < nextSample)
            {
                continue;
            }

            nextSample += interval;
            var sample = tracker.Sample(clock.Elapsed);
            var emitted = source.FramesEmitted;
            var fps = (emitted - lastEmitted) / interval.TotalSeconds;
            lastEmitted = emitted;

            log.Info(
                $"t={sample.Elapsed:hh\\:mm\\:ss} " +
                $"rss={sample.WorkingSetBytes / (1024.0 * 1024.0):F1}MB " +
                $"heap={sample.ManagedHeapBytes / (1024.0 * 1024.0):F1}MB " +
                $"gen0={sample.Gen0Collections} " +
                $"emitted={emitted} ({fps:F1}/s, {source.FillerFrames} filler) " +
                $"dropped={source.FramesDropped} " +
                $"consumed={Interlocked.Read(ref consumed)} " +
                $"free-slots={source.PoolSlotsAvailable}");

            if (!source.IsRunning)
            {
                log.Error("Capture stopped early; see the log above for the fault.");
                break;
            }
        }

        Volatile.Write(ref consumerStop, true);
        consumer.Join(TimeSpan.FromSeconds(5));
        source.Stop();

        var report = tracker.Report();
        log.Info($"Soak report:{Environment.NewLine}{report}");
        log.Info($"Frames: {source.FramesEmitted} emitted, {Interlocked.Read(ref consumed)} consumed, " +
                 $"{source.FramesDropped} dropped, {source.FillerFrames} filler.");

        if (!report.IsStable)
        {
            log.Error("Memory did not stay flat over the run.");
            return 2;
        }

        log.Info("Capture soak passed.");
        return 0;
    }

    /// <summary>
    /// Enqueues and nothing else. Refusing when full is what capture expects, and
    /// it keeps a slow consumer from deadlocking the run.
    /// </summary>
    private sealed class EnqueueOnlySink(FrameQueue queue) : IFrameSink
    {
        public bool TryAccept(in CapturedFrame frame) => queue.TryEnqueue(frame);
    }
}
