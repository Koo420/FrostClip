using Frost.Engine.Pipeline;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class FramePacerTests
{
    private const long Tps = FramePacer.TicksPerSecond;

    [Fact]
    public void FirstFrameIsAlwaysEmitted()
    {
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        Assert.Equal(PacingDecision.Emit, pacer.Consider(12345));
        Assert.Equal(1, pacer.EmittedCount);
    }

    [Fact]
    public void SourceAtTargetRateIsNeverDecimated()
    {
        // The failure mode this guards: pacing from "last accepted timestamp"
        // makes a 60fps source sampled at 60fps drift just past the deadline and
        // lose every other frame, halving the capture rate.
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        var interval = Tps / 60;

        for (var i = 0; i < 600; i++)
        {
            Assert.Equal(PacingDecision.Emit, pacer.Consider(i * interval));
        }

        Assert.Equal(600, pacer.EmittedCount);
        Assert.Equal(0, pacer.DroppedCount);
    }

    [Fact]
    public void SourceAtTargetRateSurvivesJitter()
    {
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        var interval = Tps / 60;
        var jitter = new[] { 0L, interval / 5, -interval / 5, interval / 3, -interval / 4 };

        for (var i = 0; i < 500; i++)
        {
            var timestamp = (i * interval) + jitter[i % jitter.Length];
            Assert.Equal(PacingDecision.Emit, pacer.Consider(timestamp));
        }

        Assert.Equal(0, pacer.DroppedCount);
    }

    [Fact]
    public void FastSourceIsDecimatedToTheTargetRate()
    {
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        var sourceInterval = Tps / 240;

        for (var i = 0; i < 2400; i++)
        {
            pacer.Consider(i * sourceInterval);
        }

        // 2400 frames of 240Hz source is 10 seconds; at 60fps that is ~600 out.
        Assert.InRange(pacer.EmittedCount, 595, 605);
        Assert.Equal(2400 - pacer.EmittedCount, pacer.DroppedCount);
    }

    [Fact]
    public void NoFillerWhileFramesKeepArriving()
    {
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        var interval = Tps / 60;

        for (var i = 0; i < 120; i++)
        {
            pacer.Consider(i * interval);
            Assert.False(pacer.TryPlanFiller(i * interval, out _));
        }

        Assert.Equal(0, pacer.FillerCount);
    }

    [Fact]
    public void FillerIsDueOnceTheGapReachesTheCeiling()
    {
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        pacer.Consider(0);

        var justUnder = TimeSpan.FromMilliseconds(499).Ticks;
        Assert.False(pacer.TryPlanFiller(justUnder, out _));

        var atCeiling = TimeSpan.FromMilliseconds(500).Ticks;
        Assert.True(pacer.TryPlanFiller(atCeiling, out var stamp));
        Assert.Equal(atCeiling, stamp);
        Assert.Equal(1, pacer.FillerCount);

        // One filler per call: a long static period must not produce a burst.
        Assert.False(pacer.TryPlanFiller(atCeiling, out _));
    }

    [Fact]
    public void FillerCadenceOverALongStaticPeriodMatchesTheCeiling()
    {
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        pacer.Consider(0);

        // Poll at 60Hz for 10 seconds with no new content.
        for (long t = 0; t <= 10 * Tps; t += Tps / 60)
        {
            pacer.TryPlanFiller(t, out _);
        }

        Assert.InRange(pacer.FillerCount, 19, 21);
    }

    [Fact]
    public void NoFillerBeforeTheFirstRealFrame()
    {
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        Assert.False(pacer.TryPlanFiller(10 * Tps, out _));
    }

    [Fact]
    public void ResumingAfterAStallDoesNotProduceACatchUpBurst()
    {
        // Alt-tab / loading screen: the source goes quiet for 30 seconds, then
        // returns at full rate. A naive fixed-cadence deadline would be 1800
        // intervals in the past and would accept everything for a while.
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        var interval = Tps / 60;
        pacer.Consider(0);

        var resume = 30 * Tps;
        Assert.Equal(PacingDecision.Emit, pacer.Consider(resume));

        var sourceInterval = Tps / 240;
        var emittedBefore = pacer.EmittedCount;
        for (var i = 1; i <= 240; i++)
        {
            pacer.Consider(resume + (i * sourceInterval));
        }

        // One second of 240Hz source after the stall => about 60 frames, not 240.
        Assert.InRange(pacer.EmittedCount - emittedBefore, 55, 65);
        _ = interval;
    }

    [Fact]
    public void MaxFrameIntervalIsNeverBelowTheFrameInterval()
    {
        // A 1ms ceiling at 60fps is nonsense; clamping keeps the filler logic from
        // firing on every single poll.
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(1));
        Assert.Equal(Tps / 60, pacer.MaxFrameIntervalTicks);
    }

    [Fact]
    public void ResetClearsPacingState()
    {
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        pacer.Consider(5 * Tps);
        pacer.Reset();

        Assert.False(pacer.TryPlanFiller(100 * Tps, out _));
        Assert.Equal(PacingDecision.Emit, pacer.Consider(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(481)]
    public void InvalidFpsIsRejected(int fps) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FramePacer(fps, TimeSpan.FromMilliseconds(500)));

    [Fact]
    public void NonPositiveMaxIntervalIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(60, TimeSpan.Zero));

    [Fact]
    public void PacingDoesNotAllocate()
    {
        var pacer = new FramePacer(60, TimeSpan.FromMilliseconds(500));
        var interval = Tps / 240;

        for (var i = 0; i < 1000; i++)
        {
            pacer.Consider(i * interval);
            pacer.TryPlanFiller(i * interval, out _);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1000; i < 100_000; i++)
        {
            pacer.Consider(i * interval);
            pacer.TryPlanFiller(i * interval, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
