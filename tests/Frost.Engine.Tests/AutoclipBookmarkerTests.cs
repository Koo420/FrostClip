using Frost.Engine.Audio;
using Frost.Engine.Diagnostics;
using Frost.Engine.Recording;
using Frost.Shared.Clips;
using Frost.Shared.Settings;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class AutoclipBookmarkerTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    private sealed class RecordingTarget : IBookmarkTarget
    {
        internal List<BookmarkSource> Added { get; } = [];

        internal bool Refuse { get; set; }

        public bool TryAddBookmark(BookmarkSource source, string? label, out string? error)
        {
            if (Refuse)
            {
                error = "no recording";
                return false;
            }

            Added.Add(source);
            error = null;
            return true;
        }
    }

    private static float AmplitudeFor(double db) => (float)Math.Pow(10, db / 20.0);

    private static void Feed(
        AutoclipBookmarker bookmarker, double seconds, double levelDb, ref long clock)
    {
        var block = new float[SampleRate / 10 * Channels];
        Array.Fill(block, AmplitudeFor(levelDb));
        var blockTicks = TimeSpan.TicksPerSecond / 10;

        for (var i = 0; i < (int)Math.Round(seconds * 10); i++)
        {
            bookmarker.ProcessAudio(block, clock);
            clock += blockTicks;
        }
    }

    [Fact]
    public void DisabledByDefaultAndDoesNothing()
    {
        var target = new RecordingTarget();
        var bookmarker = new AutoclipBookmarker(
            new AutoclipSettings(), SampleRate, Channels, target, NullEngineLog.Instance);

        Assert.False(bookmarker.IsEnabled);

        var clock = 0L;
        Feed(bookmarker, seconds: 20, levelDb: -30, ref clock);
        Feed(bookmarker, seconds: 5, levelDb: -5, ref clock);

        Assert.Empty(target.Added);
        Assert.Equal(0, bookmarker.MarksPlaced);
        Assert.Contains("off", bookmarker.Describe());
    }

    [Fact]
    public void WhenEnabledASpikePlacesABookmark()
    {
        var target = new RecordingTarget();
        var bookmarker = new AutoclipBookmarker(
            new AutoclipSettings { DetectLoudnessSpikes = true },
            SampleRate, Channels, target, NullEngineLog.Instance);

        Assert.True(bookmarker.IsEnabled);

        var clock = 0L;
        Feed(bookmarker, seconds: 10, levelDb: -30, ref clock);
        Feed(bookmarker, seconds: 1, levelDb: -10, ref clock);

        Assert.Equal(BookmarkSource.AudioLoudnessSpike, Assert.Single(target.Added));
        Assert.Equal(1, bookmarker.MarksPlaced);
    }

    [Fact]
    public void ASpikeWithNoRecordingToMarkIsCountedNotLogged()
    {
        // The detector runs whenever audio does, so this is expected and harmless.
        var target = new RecordingTarget { Refuse = true };
        var bookmarker = new AutoclipBookmarker(
            new AutoclipSettings { DetectLoudnessSpikes = true },
            SampleRate, Channels, target, NullEngineLog.Instance);

        var clock = 0L;
        Feed(bookmarker, seconds: 10, levelDb: -30, ref clock);
        Feed(bookmarker, seconds: 1, levelDb: -10, ref clock);

        Assert.Empty(target.Added);
        Assert.Equal(0, bookmarker.MarksPlaced);
        Assert.Equal(1, bookmarker.MarksDeclined);
    }

    [Fact]
    public void BookmarksAreTheOnlyThingItEverDoes()
    {
        // The automatic path must never save a clip or start a recording. The type
        // only has a bookmark target, so there is nothing else it could do — this
        // asserts that surface stays that way.
        var members = typeof(AutoclipBookmarker)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_", StringComparison.Ordinal) && n != "ToString"
                        && n != "Equals" && n != "GetHashCode" && n != "GetType")
            .ToList();

        Assert.Equal(["Describe", "ProcessAudio", "Reset"], members.Order().ToList());
    }

    [Fact]
    public void EndToEndItMarksARealSessionRecording()
    {
        var writer = new CountingWriter();
        using var recorder = new FullSessionRecorder(_ => writer, NullEngineLog.Instance);

        var directory = Path.Combine(
            Path.GetTempPath(), "frost-autoclip-tests", Guid.NewGuid().ToString("N"));

        try
        {
            recorder.TryStart(
                Path.Combine(directory, "session.mp4"), 1920, 1080, "H264", "hl2", out _);

            // A couple of seconds of video so the recording has a timeline.
            var frameTicks = TimeSpan.TicksPerSecond / 60;
            var payload = new byte[1000];
            for (var i = 0; i < 240; i++)
            {
                recorder.TryWrite(payload, i * frameTicks, frameTicks, i % 120 == 0);
            }

            var bookmarker = new AutoclipBookmarker(
                new AutoclipSettings { DetectLoudnessSpikes = true },
                SampleRate, Channels, recorder, NullEngineLog.Instance);

            var clock = 0L;
            Feed(bookmarker, seconds: 10, levelDb: -30, ref clock);
            Feed(bookmarker, seconds: 1, levelDb: -10, ref clock);

            Assert.Equal(1, bookmarker.MarksPlaced);

            var metadata = recorder.Stop();
            var bookmark = Assert.Single(metadata!.Bookmarks);
            Assert.Equal(BookmarkSource.AudioLoudnessSpike, bookmark.Source);
            Assert.Null(bookmark.Label);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void DisabledProcessingCostsNothingPerBlock()
    {
        // The default configuration. This runs for every audio block of every
        // session whether or not anyone enables the feature.
        var bookmarker = new AutoclipBookmarker(
            new AutoclipSettings(), SampleRate, Channels, new RecordingTarget(), NullEngineLog.Instance);

        var block = new float[SampleRate / 100 * Channels];

        AllocationAssert.NoPerIterationAllocation(
            i => bookmarker.ProcessAudio(block, i), iterations: 100_000);
    }

    [Fact]
    public void EnabledProcessingDoesNotAllocatePerBlock()
    {
        var bookmarker = new AutoclipBookmarker(
            new AutoclipSettings { DetectLoudnessSpikes = true },
            SampleRate, Channels, new RecordingTarget(), NullEngineLog.Instance);

        var block = new float[SampleRate / 100 * Channels];
        Array.Fill(block, AmplitudeFor(-25));
        var blockTicks = TimeSpan.TicksPerSecond / 100;

        AllocationAssert.NoPerIterationAllocation(
            i => bookmarker.ProcessAudio(block, i * blockTicks),
            iterations: 50_000,
            warmUpIterations: 2_000);
    }

    [Fact]
    public void ResetClearsDetectorState()
    {
        var target = new RecordingTarget();
        var bookmarker = new AutoclipBookmarker(
            new AutoclipSettings { DetectLoudnessSpikes = true },
            SampleRate, Channels, target, NullEngineLog.Instance);

        var clock = 0L;
        Feed(bookmarker, seconds: 10, levelDb: -30, ref clock);
        bookmarker.Reset();

        // After a reset the baseline has to settle again before anything marks.
        Feed(bookmarker, seconds: 0.5, levelDb: -5, ref clock);
        Assert.Empty(target.Added);
    }

    [Fact]
    public void NullDependenciesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new AutoclipBookmarker(
            null!, SampleRate, Channels, new RecordingTarget(), NullEngineLog.Instance));
        Assert.Throws<ArgumentNullException>(() => new AutoclipBookmarker(
            new AutoclipSettings(), SampleRate, Channels, null!, NullEngineLog.Instance));
        Assert.Throws<ArgumentNullException>(() => new AutoclipBookmarker(
            new AutoclipSettings(), SampleRate, Channels, new RecordingTarget(), null!));
    }

    private sealed class CountingWriter : ISessionWriter
    {
        public string Extension => ".mp4";

        public long BytesWritten { get; private set; }

        public bool IsFaulted => false;

        public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
        {
            BytesWritten += data.Length;
            return true;
        }

        public void Finish(TimeSpan timeout)
        {
        }

        public void Dispose()
        {
        }
    }
}
