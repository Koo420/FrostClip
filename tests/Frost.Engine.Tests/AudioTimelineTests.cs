using Frost.Engine.Audio;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class AudioTimelineTests
{
    private static readonly AudioFormat Format = AudioFormat.Default.AsInt16;

    /// <summary>Frames in a 10ms WASAPI packet.</summary>
    private const int PacketFrames = 480;

    private static long PacketTicks => Format.FramesToTicks(PacketFrames);

    private static AudioTimeline Timeline(double maxSilenceSeconds = 5) =>
        new(Format, TimeSpan.FromSeconds(maxSilenceSeconds));

    [Fact]
    public void TheFirstBlockSetsTheOriginAndIsWrittenAsIs()
    {
        var timeline = Timeline();
        var placement = timeline.Place(12_345, PacketFrames);

        Assert.False(placement.NeedsSilence);
        Assert.Equal(0, placement.SkipFrames);
        Assert.Equal(12_345, placement.TimestampTicks);
        Assert.Equal(PacketFrames, placement.FrameCount);
    }

    [Fact]
    public void AContinuousStreamNeedsNoAdjustment()
    {
        var timeline = Timeline();

        for (var i = 0; i < 1000; i++)
        {
            var placement = timeline.Place(i * PacketTicks, PacketFrames);

            Assert.False(placement.NeedsSilence);
            Assert.Equal(0, placement.SkipFrames);
            Assert.Equal(PacketFrames, placement.FrameCount);
        }

        Assert.Equal(0, timeline.SilenceFramesInserted);
        Assert.Equal(0, timeline.FramesTrimmed);
        Assert.Equal(0, timeline.Resyncs);
    }

    [Fact]
    public void AGapIsFilledWithSilenceRatherThanClosed()
    {
        // The failure this prevents: loopback delivers nothing while a game is
        // quiet, and closing the gap moves every later sample earlier relative to
        // the video. The drift is cumulative and permanent.
        var timeline = Timeline();

        timeline.Place(0, PacketFrames);

        // Two seconds of silence, then audio resumes.
        var resumeAt = PacketTicks + (2 * TimeSpan.TicksPerSecond);
        var placement = timeline.Place(resumeAt, PacketFrames);

        Assert.True(placement.NeedsSilence);
        Assert.Equal(Format.SampleRate * 2, placement.SilenceFrames);
        Assert.Equal(PacketFrames, placement.FrameCount);

        // And the resumed audio still lands where it was captured.
        Assert.InRange(
            placement.TimestampTicks,
            resumeAt - PacketTicks,
            resumeAt + PacketTicks);
    }

    [Fact]
    public void SilenceKeepsTheTrackInSyncOverManyGaps()
    {
        // Tracks total written duration against total elapsed capture time: they
        // must not drift apart.
        var timeline = Timeline();
        var writtenFrames = 0L;
        var clock = 0L;

        for (var round = 0; round < 50; round++)
        {
            // Half a second of audio.
            for (var i = 0; i < 50; i++)
            {
                var placement = timeline.Place(clock, PacketFrames);
                writtenFrames += placement.SilenceFrames + placement.FrameCount;
                clock += PacketTicks;
            }

            // Then a quarter-second of nothing.
            clock += TimeSpan.TicksPerSecond / 4;
        }

        // The final gap has no block after it, so nothing has triggered its fill -
        // a gap is only knowable once audio returns. Compare against elapsed time
        // up to the last written block.
        var elapsedFrames = Format.TicksToFrames(clock - (TimeSpan.TicksPerSecond / 4));

        // Within a couple of packets over 37 seconds of capture.
        Assert.InRange(writtenFrames, elapsedFrames - (PacketFrames * 2), elapsedFrames + PacketFrames);
    }

    [Fact]
    public void ASubFrameGapIsIgnoredRatherThanRoundedUp()
    {
        // Rounding a fraction of a frame up, every packet, would itself cause drift.
        var timeline = Timeline();
        timeline.Place(0, PacketFrames);

        var placement = timeline.Place(PacketTicks + 3, PacketFrames);

        Assert.False(placement.NeedsSilence);
        Assert.Equal(0, timeline.SilenceFramesInserted);
    }

    [Fact]
    public void AGapLongerThanTheLimitResynchronisesInsteadOfPaddingForever()
    {
        // A minutes-long gap means capture actually stopped - the device was
        // unplugged, or the session was suspended. Writing minutes of silence into
        // the file would be worse than a discontinuity.
        var timeline = Timeline(maxSilenceSeconds: 5);
        timeline.Place(0, PacketFrames);

        var placement = timeline.Place(60 * TimeSpan.TicksPerSecond, PacketFrames);

        Assert.False(placement.NeedsSilence);
        Assert.Equal(1, timeline.Resyncs);
        Assert.Equal(0, timeline.SilenceFramesInserted);
        Assert.Equal(60 * TimeSpan.TicksPerSecond, placement.TimestampTicks);
    }

    [Fact]
    public void AGapExactlyAtTheLimitIsStillFilled()
    {
        var timeline = Timeline(maxSilenceSeconds: 5);
        timeline.Place(0, PacketFrames);

        var placement = timeline.Place(PacketTicks + (5 * TimeSpan.TicksPerSecond), PacketFrames);

        Assert.True(placement.NeedsSilence);
        Assert.Equal(0, timeline.Resyncs);
    }

    [Fact]
    public void AnOverlappingBlockIsTrimmedRatherThanPushingTheTrackLate()
    {
        var timeline = Timeline();
        timeline.Place(0, PacketFrames);

        // Arrives 200 frames earlier than expected: 200 frames already covered.
        var overlapTicks = Format.FramesToTicks(200);
        var placement = timeline.Place(PacketTicks - overlapTicks, PacketFrames);

        Assert.Equal(200, placement.SkipFrames);
        Assert.Equal(PacketFrames - 200, placement.FrameCount);
        Assert.Equal(PacketTicks, placement.TimestampTicks);
        Assert.Equal(200, timeline.FramesTrimmed);
    }

    [Fact]
    public void ABlockEntirelyInThePastIsDropped()
    {
        var timeline = Timeline();
        timeline.Place(0, PacketFrames);
        timeline.Place(PacketTicks, PacketFrames);

        // A timestamp that jumped backwards by a whole second.
        var placement = timeline.Place(-TimeSpan.TicksPerSecond, PacketFrames);

        Assert.True(placement.IsFullyTrimmed);
        Assert.Equal(PacketFrames, placement.SkipFrames);
        Assert.Equal(0, placement.FrameCount);
    }

    [Fact]
    public void TrimmingDoesNotLoseTrackOfWhereTheTimelineIs()
    {
        var timeline = Timeline();
        timeline.Place(0, PacketFrames);

        var afterFirst = timeline.NextExpectedTicks;

        // A fully-dropped block must not move the timeline.
        timeline.Place(-TimeSpan.TicksPerSecond, PacketFrames);
        Assert.Equal(afterFirst, timeline.NextExpectedTicks);

        // And the next in-order block still lands correctly.
        var placement = timeline.Place(afterFirst, PacketFrames);
        Assert.Equal(afterFirst, placement.TimestampTicks);
        Assert.Equal(PacketFrames, placement.FrameCount);
    }

    [Fact]
    public void IrregularPacketSizesAreHandled()
    {
        // WASAPI hands over whatever the device has, not tidy round numbers.
        var timeline = Timeline();
        var random = new Random(20260913);
        var clock = 0L;
        var writtenFrames = 0L;

        for (var i = 0; i < 5000; i++)
        {
            var frames = random.Next(64, 1024);
            var placement = timeline.Place(clock, frames);

            writtenFrames += placement.SilenceFrames + placement.FrameCount;
            clock += Format.FramesToTicks(frames);
        }

        var elapsedFrames = Format.TicksToFrames(clock);
        Assert.InRange(writtenFrames, elapsedFrames - 5000, elapsedFrames + 5000);
    }

    [Fact]
    public void EmptyBlocksArePassedThroughWithoutTouchingTheTimeline()
    {
        var timeline = Timeline();
        timeline.Place(0, PacketFrames);
        var before = timeline.NextExpectedTicks;

        timeline.Place(PacketTicks, 0);

        Assert.Equal(before, timeline.NextExpectedTicks);
    }

    [Fact]
    public void ResetForgetsTheOrigin()
    {
        var timeline = Timeline();
        timeline.Place(5 * TimeSpan.TicksPerSecond, PacketFrames);
        timeline.Reset();

        Assert.Equal(-1, timeline.NextExpectedTicks);

        // A wildly different timestamp is then accepted as a fresh origin.
        var placement = timeline.Place(0, PacketFrames);
        Assert.False(placement.NeedsSilence);
        Assert.Equal(0, placement.TimestampTicks);
    }

    [Fact]
    public void PlacementDoesNotAllocate()
    {
        // Runs on the audio capture thread, once per packet.
        var timeline = Timeline();

        AllocationAssert.NoPerIterationAllocation(
            i => timeline.Place(i * PacketTicks, PacketFrames),
            iterations: 100_000);
    }

    [Fact]
    public void NullFormatAndBadGapsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new AudioTimeline(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioTimeline(Format, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AudioTimeline(Format, TimeSpan.FromSeconds(-1)));
    }
}
