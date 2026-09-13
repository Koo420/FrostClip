using Frost.Engine.Audio;
using Frost.Shared.Settings;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class LoudnessSpikeDetectorTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    private static AutoclipSettings Settings(
        double thresholdDb = 9.0, double baselineSeconds = 3.0, double minimumGap = 5.0) => new()
        {
            DetectLoudnessSpikes = true,
            LoudnessSpikeThresholdDb = thresholdDb,
            LoudnessBaselineSeconds = baselineSeconds,
            MinimumSecondsBetweenMarks = minimumGap,
        };

    private static LoudnessSpikeDetector Detector(AutoclipSettings? settings = null) =>
        new(SampleRate, Channels, settings ?? Settings());

    /// <summary>Amplitude for a given dBFS level, as RMS of a constant signal.</summary>
    private static float AmplitudeFor(double db) => (float)Math.Pow(10, db / 20.0);

    /// <summary>
    /// Feeds <paramref name="seconds"/> of constant-amplitude audio in 100ms
    /// blocks, collecting any marks.
    /// </summary>
    private static List<long> Feed(
        LoudnessSpikeDetector detector, double seconds, double levelDb, ref long clockTicks)
    {
        var marks = new List<long>();
        var blockFrames = SampleRate / 10;
        var block = new float[blockFrames * Channels];
        Array.Fill(block, AmplitudeFor(levelDb));

        var blockTicks = TimeSpan.TicksPerSecond / 10;
        var blocks = (int)Math.Round(seconds * 10);

        for (var i = 0; i < blocks; i++)
        {
            if (detector.ProcessBlock(block, clockTicks, out var mark))
            {
                marks.Add(mark);
            }

            clockTicks += blockTicks;
        }

        return marks;
    }

    [Fact]
    public void SteadyAudioProducesNoMarks()
    {
        // A game at a constant volume is not a series of moments.
        var detector = Detector();
        var clock = 0L;

        var marks = Feed(detector, seconds: 60, levelDb: -20, ref clock);

        Assert.Empty(marks);
        Assert.Equal(0, detector.MarksDetected);
        Assert.InRange(detector.CurrentLevelDb, -21, -19);
    }

    [Fact]
    public void ASuddenJumpAboveTheThresholdIsMarked()
    {
        var detector = Detector();
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -30, ref clock);
        var marks = Feed(detector, seconds: 1, levelDb: -12, ref clock);

        Assert.Single(marks);
        Assert.Equal(1, detector.MarksDetected);
    }

    [Fact]
    public void AJumpBelowTheThresholdIsNotMarked()
    {
        var detector = Detector(Settings(thresholdDb: 12));
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -30, ref clock);

        // 6dB up, well under the 12dB threshold.
        var marks = Feed(detector, seconds: 2, levelDb: -24, ref clock);

        Assert.Empty(marks);
    }

    [Fact]
    public void SustainedLoudnessIsOneMarkNotAContinuousStream()
    {
        // A loud section becomes the new normal; the baseline catches up.
        var detector = Detector(Settings(minimumGap: 1.0));
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -30, ref clock);
        var marks = Feed(detector, seconds: 30, levelDb: -12, ref clock);

        Assert.True(marks.Count <= 2, $"{marks.Count} marks for one sustained loud section");
        Assert.NotEmpty(marks);
    }

    [Fact]
    public void MarksAreRateLimited()
    {
        // One firefight is one mark, not fifty.
        var detector = Detector(Settings(minimumGap: 10.0));
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -30, ref clock);

        var marks = new List<long>();
        for (var burst = 0; burst < 6; burst++)
        {
            marks.AddRange(Feed(detector, seconds: 0.5, levelDb: -10, ref clock));
            marks.AddRange(Feed(detector, seconds: 1.0, levelDb: -30, ref clock));
        }

        // Nine seconds of bursts with a ten-second minimum gap: one mark.
        Assert.Single(marks);
    }

    [Fact]
    public void ASecondSpikeAfterTheGapIsMarkedAgain()
    {
        var detector = Detector(Settings(minimumGap: 2.0));
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -30, ref clock);
        var first = Feed(detector, seconds: 0.5, levelDb: -10, ref clock);
        Feed(detector, seconds: 6, levelDb: -30, ref clock);
        var second = Feed(detector, seconds: 0.5, levelDb: -10, ref clock);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(2, detector.MarksDetected);
    }

    [Fact]
    public void ASpikeOutOfNearSilenceIsNotAMoment()
    {
        // Un-pausing a game, or a menu sound in a quiet lobby, must not mark.
        // Without the absolute floor these look like a 40dB jump.
        var detector = Detector();
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -90, ref clock);
        var marks = Feed(detector, seconds: 2, levelDb: -55, ref clock);

        Assert.Empty(marks);
    }

    [Fact]
    public void ALoudSpikeOutOfSilenceStillCountsOnceItClearsTheFloor()
    {
        var detector = Detector();
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -80, ref clock);
        var marks = Feed(detector, seconds: 1, levelDb: -10, ref clock);

        Assert.Single(marks);
    }

    [Fact]
    public void DigitalSilenceIsHandledWithoutInfinitiesOrMarks()
    {
        var detector = Detector();
        var clock = 0L;
        var block = new float[SampleRate / 10 * Channels];

        for (var i = 0; i < 200; i++)
        {
            Assert.False(detector.ProcessBlock(block, clock, out _));
            clock += TimeSpan.TicksPerSecond / 10;
        }

        Assert.Equal(LoudnessSpikeDetector.SilenceDb, detector.CurrentLevelDb);
        Assert.False(double.IsNaN(detector.BaselineDb));
        Assert.False(double.IsInfinity(detector.BaselineDb));
        Assert.Equal(0, detector.MarksDetected);
    }

    [Fact]
    public void NothingIsMarkedBeforeTheBaselineHasSettled()
    {
        // Otherwise the first loud sound after launch is always a "moment".
        var detector = Detector(Settings(baselineSeconds: 5.0));
        var clock = 0L;

        Feed(detector, seconds: 0.5, levelDb: -40, ref clock);
        var early = Feed(detector, seconds: 1.0, levelDb: -5, ref clock);

        Assert.Empty(early);
        Assert.False(detector.IsWarmedUp);
    }

    [Fact]
    public void WarmUpCompletesAfterTheBaselinePeriod()
    {
        var detector = Detector(Settings(baselineSeconds: 2.0));
        var clock = 0L;

        Feed(detector, seconds: 5, levelDb: -25, ref clock);
        Assert.True(detector.IsWarmedUp);
    }

    [Fact]
    public void TheBaselineFollowsSlowlyEnoughToStillSeeASpike()
    {
        var detector = Detector(Settings(baselineSeconds: 3.0));
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -30, ref clock);
        var baselineBefore = detector.BaselineDb;
        Assert.InRange(baselineBefore, -31, -29);

        // A single 300ms window at +20dB must not drag the baseline more than a
        // couple of dB, or the spike would hide itself.
        Feed(detector, seconds: 0.3, levelDb: -10, ref clock);
        Assert.InRange(detector.BaselineDb, baselineBefore - 1, baselineBefore + 4);
    }

    [Fact]
    public void ADropInLevelIsNeverAMark()
    {
        var detector = Detector();
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -10, ref clock);
        var marks = Feed(detector, seconds: 10, levelDb: -40, ref clock);

        Assert.Empty(marks);
    }

    [Fact]
    public void MarkTimestampsAreOnTheCaptureClock()
    {
        var detector = Detector();
        var clock = 100 * TimeSpan.TicksPerSecond;

        Feed(detector, seconds: 10, levelDb: -30, ref clock);
        var atSpike = clock;
        var marks = Feed(detector, seconds: 1, levelDb: -10, ref clock);

        var mark = Assert.Single(marks);
        Assert.InRange(mark, atSpike, atSpike + TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var detector = Detector();
        var clock = 0L;

        Feed(detector, seconds: 10, levelDb: -30, ref clock);
        Assert.True(detector.IsWarmedUp);

        detector.Reset();

        Assert.False(detector.IsWarmedUp);
        Assert.True(double.IsNaN(detector.BaselineDb));

        // And it will not mark immediately after a reset either.
        var marks = Feed(detector, seconds: 0.5, levelDb: -5, ref clock);
        Assert.Empty(marks);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void WorksForAnyChannelCount(int channels)
    {
        var detector = new LoudnessSpikeDetector(SampleRate, channels, Settings());
        var clock = 0L;
        var blockTicks = TimeSpan.TicksPerSecond / 10;
        var quiet = new float[SampleRate / 10 * channels];
        var loud = new float[SampleRate / 10 * channels];
        Array.Fill(quiet, AmplitudeFor(-30));
        Array.Fill(loud, AmplitudeFor(-10));

        for (var i = 0; i < 100; i++)
        {
            detector.ProcessBlock(quiet, clock, out _);
            clock += blockTicks;
        }

        var marked = false;
        for (var i = 0; i < 10 && !marked; i++)
        {
            marked = detector.ProcessBlock(loud, clock, out _);
            clock += blockTicks;
        }

        Assert.True(marked, $"no spike detected with {channels} channel(s)");
    }

    [Fact]
    public void AnEmptyBlockIsIgnored()
    {
        var detector = Detector();
        Assert.False(detector.ProcessBlock([], 0, out _));
    }

    [Fact]
    public void BlocksThatDoNotAlignToTheWindowStillWork()
    {
        // WASAPI hands over whatever the device gives, not tidy round numbers.
        var detector = Detector();
        var clock = 0L;
        var random = new Random(20260913);

        var marked = false;
        for (var i = 0; i < 4000 && !marked; i++)
        {
            var frames = random.Next(37, 971);
            var block = new float[frames * Channels];

            // Quiet for the first ~12 seconds, then loud.
            var level = clock < 12 * TimeSpan.TicksPerSecond ? -30.0 : -8.0;
            Array.Fill(block, AmplitudeFor(level));

            marked = detector.ProcessBlock(block, clock, out _);
            clock += (long)(frames / (double)SampleRate * TimeSpan.TicksPerSecond);
        }

        Assert.True(marked, "no spike detected with irregular block sizes");
    }

    [Fact]
    public void ProcessingDoesNotAllocate()
    {
        // Runs on the audio capture thread.
        var detector = Detector();
        var block = new float[SampleRate / 100 * Channels];
        Array.Fill(block, AmplitudeFor(-25));
        var blockTicks = TimeSpan.TicksPerSecond / 100;

        AllocationAssert.NoPerIterationAllocation(
            i => detector.ProcessBlock(block, i * blockTicks, out _),
            iterations: 50_000,
            warmUpIterations: 2_000);
    }

    [Theory]
    [InlineData(1000, 2)]
    [InlineData(500_000, 2)]
    [InlineData(48_000, 0)]
    [InlineData(48_000, 32)]
    public void UnsupportedFormatsAreRejected(int sampleRate, int channels) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LoudnessSpikeDetector(sampleRate, channels, Settings()));

    [Fact]
    public void NullSettingsAreRejected() =>
        Assert.Throws<ArgumentNullException>(() => new LoudnessSpikeDetector(48_000, 2, null!));

    [Fact]
    public void TheDetectorIsOnlyEverUsedWhenTheUserOptedIn() =>
        // The setting that gates this is off in a fresh install, by design: the
        // heuristic produces false positives and must not surprise anyone.
        Assert.False(new AutoclipSettings().DetectLoudnessSpikes);
}
