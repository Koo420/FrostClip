using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Engine.Recording;
using Frost.Shared.Clips;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>Records what was written, and can be made to refuse or fail.</summary>
internal sealed class FakeSessionWriter : ISessionWriter
{
    private readonly string _path;

    internal FakeSessionWriter(string path) => _path = path;

    public string Extension => ".mp4";

    public long BytesWritten { get; private set; }

    public bool IsFaulted { get; private set; }

    internal List<(byte[] Data, long Timestamp, long Duration, bool IsKeyFrame)> Written { get; } = [];

    internal bool Finished { get; private set; }

    internal bool Disposed { get; private set; }

    /// <summary>When set, every write is refused — the "disk is behind" case.</summary>
    internal bool RefuseWrites { get; set; }

    /// <summary>When set, Finish throws — the "finalising failed" case.</summary>
    internal bool ThrowOnFinish { get; set; }

    public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
    {
        if (RefuseWrites || IsFaulted)
        {
            return false;
        }

        Written.Add((data.ToArray(), timestampTicks, durationTicks, isKeyFrame));
        BytesWritten += data.Length;
        return true;
    }

    public void Finish(TimeSpan timeout)
    {
        if (ThrowOnFinish)
        {
            IsFaulted = true;
            throw new IOException("finalising failed");
        }

        Finished = true;

        // Stand in for the real file, so the recorder can stat it.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, new byte[Math.Max(1, BytesWritten)]);
    }

    public void Dispose() => Disposed = true;
}

public sealed class FullSessionRecorderTests : IDisposable
{
    private const int Fps = 60;
    private static readonly long FrameTicks = TimeSpan.TicksPerSecond / Fps;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "frost-session-tests", Guid.NewGuid().ToString("N"));

    private readonly List<FakeSessionWriter> _writers = [];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Frame timestamps are TicksPerSecond/60, which truncates, so durations land
    /// a few hundred ticks short of a round number.
    /// </summary>
    private static void AssertCloseTo(TimeSpan expected, TimeSpan actual) =>
        Assert.InRange(actual, expected - TimeSpan.FromMilliseconds(20), expected + TimeSpan.FromMilliseconds(20));

    private FullSessionRecorder Recorder() =>
        new(path =>
        {
            var writer = new FakeSessionWriter(path);
            _writers.Add(writer);
            return writer;
        }, NullEngineLog.Instance);

    private string PathFor(string name) => Path.Combine(_directory, name);

    /// <summary>Feeds frames with a keyframe every <paramref name="keyFrameEvery"/>.</summary>
    private static void Feed(
        IEncodedSampleSink sink, int frames, int keyFrameEvery = 120, int startFrame = 0, int bytes = 1000)
    {
        var payload = new byte[bytes];

        for (var i = startFrame; i < startFrame + frames; i++)
        {
            Array.Fill(payload, (byte)(i & 0xFF));
            sink.TryWrite(payload, i * FrameTicks, FrameTicks, i % keyFrameEvery == 0);
        }
    }

    [Fact]
    public void DoesNothingUntilStarted()
    {
        using var recorder = Recorder();

        Assert.False(recorder.IsRecording);
        Feed(recorder, 600);

        Assert.Equal(0, recorder.SamplesWritten);
        Assert.Empty(_writers);
        Assert.Null(recorder.Stop());
    }

    [Fact]
    public void RecordsFromTheFirstKeyFrameOnward()
    {
        // A file that starts on a P-frame has an undecodable first GOP.
        using var recorder = Recorder();
        Assert.True(recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", "hl2", out var error));
        Assert.Null(error);

        // Start mid-GOP: frames 1..119 are P-frames, 120 is the next keyframe.
        Feed(recorder, frames: 200, keyFrameEvery: 120, startFrame: 1);

        var writer = Assert.Single(_writers);
        Assert.True(writer.Written[0].IsKeyFrame);
        Assert.Equal(119, recorder.DiscardedBeforeFirstKeyFrame);
        Assert.Equal(81, recorder.SamplesWritten);
        Assert.Equal(81, writer.Written.Count);
    }

    [Fact]
    public void StartingOnAKeyFrameDiscardsNothing()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);

        Feed(recorder, frames: 120, keyFrameEvery: 120);

        Assert.Equal(0, recorder.DiscardedBeforeFirstKeyFrame);
        Assert.Equal(120, recorder.SamplesWritten);
    }

    [Fact]
    public void WritesEverySampleByteForByte()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);

        Feed(recorder, frames: 300, keyFrameEvery: 60);

        var writer = Assert.Single(_writers);
        Assert.Equal(300, writer.Written.Count);

        for (var i = 0; i < 300; i++)
        {
            Assert.Equal(i * FrameTicks, writer.Written[i].Timestamp);
            Assert.All(writer.Written[i].Data, b => Assert.Equal((byte)(i & 0xFF), b));
        }
    }

    [Fact]
    public void ElapsedTracksWhatWasWrittenNotWhatWasOffered()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);

        // 60 discarded P-frames then 120 written frames: 2 seconds, not 3.
        Feed(recorder, frames: 180, keyFrameEvery: 60, startFrame: 1);

        AssertCloseTo(TimeSpan.FromSeconds(2), recorder.Elapsed);
    }

    [Fact]
    public void StopFinalisesTheFileAndReturnsMetadata()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("session.mp4"), 1920, 1080, "H264", "hl2", out _);
        Feed(recorder, frames: 600, keyFrameEvery: 120);

        var metadata = recorder.Stop();

        Assert.NotNull(metadata);
        Assert.Equal(ClipKind.FullSession, metadata!.Kind);
        Assert.Equal("session", metadata.DisplayName);
        Assert.Equal(1920, metadata.Width);
        Assert.Equal("H264", metadata.Codec);
        Assert.Equal("hl2", metadata.GameName);
        AssertCloseTo(TimeSpan.FromSeconds(10), TimeSpan.FromTicks(metadata.DurationTicks));

        var writer = Assert.Single(_writers);
        Assert.True(writer.Finished);
        Assert.True(writer.Disposed);
        Assert.False(recorder.IsRecording);

        // And the sidecar is beside the file.
        Assert.NotNull(ClipMetadataStore.TryLoad(metadata.FilePath));
    }

    [Fact]
    public void StopWithNothingRecordedSavesNoFile()
    {
        // Stopped before the first keyframe ever arrived.
        using var recorder = Recorder();
        recorder.TryStart(PathFor("empty.mp4"), 1920, 1080, "H264", null, out _);
        Feed(recorder, frames: 30, keyFrameEvery: 120, startFrame: 1);

        Assert.Null(recorder.Stop());
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void StopIsIdempotent()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);
        Feed(recorder, frames: 120);

        Assert.NotNull(recorder.Stop());
        Assert.Null(recorder.Stop());
    }

    [Fact]
    public void SamplesAfterStopAreIgnored()
    {
        // The encode thread keeps running; it must not write into a finalised file.
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);
        Feed(recorder, frames: 120);
        recorder.Stop();

        var writtenAtStop = _writers[0].Written.Count;
        Feed(recorder, frames: 120, startFrame: 120);

        Assert.Equal(writtenAtStop, _writers[0].Written.Count);
    }

    [Fact]
    public void StartingTwiceIsRefused()
    {
        using var recorder = Recorder();
        Assert.True(recorder.TryStart(PathFor("a.mp4"), 1920, 1080, "H264", null, out _));
        Assert.False(recorder.TryStart(PathFor("b.mp4"), 1920, 1080, "H264", null, out var error));
        Assert.Contains("already in progress", error);
        Assert.Single(_writers);
    }

    [Fact]
    public void AWriterThatCannotBeCreatedIsReportedNotThrown()
    {
        using var recorder = new FullSessionRecorder(
            _ => throw new UnauthorizedAccessException("the folder is read-only"),
            NullEngineLog.Instance);

        Assert.False(recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out var error));
        Assert.Contains("read-only", error);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void RefusedSamplesAreCountedAndRecordingContinues()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);

        Feed(recorder, frames: 60, keyFrameEvery: 120);
        _writers[0].RefuseWrites = true;
        Feed(recorder, frames: 60, keyFrameEvery: 120, startFrame: 60);
        _writers[0].RefuseWrites = false;
        Feed(recorder, frames: 60, keyFrameEvery: 120, startFrame: 120);

        Assert.Equal(60, recorder.SamplesRefused);
        Assert.Equal(120, recorder.SamplesWritten);
        Assert.True(recorder.IsRecording);
    }

    [Fact]
    public void AFailedFinaliseStillStopsCleanly()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);
        Feed(recorder, frames: 120);
        _writers[0].ThrowOnFinish = true;

        // The recorder must not propagate it: the user pressed stop, and the
        // Engine has to carry on either way.
        var metadata = recorder.Stop();

        Assert.False(recorder.IsRecording);
        Assert.True(_writers[0].Disposed);

        // And it must not claim success. An MP4 whose index was never written will
        // not open anywhere, so returning metadata would put a broken entry in the
        // gallery looking like a good recording.
        Assert.Null(metadata);

        // The partial file is left in place rather than deleted, and no sidecar is
        // written for it.
        Assert.False(File.Exists(ClipMetadata.SidecarPathFor(PathFor("s.mp4"))));
    }

    [Fact]
    public void DisposeFinalisesARecordingInProgress()
    {
        // Without this the MP4 has no index and will not open anywhere.
        var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);
        Feed(recorder, frames: 120);

        recorder.Dispose();

        Assert.True(_writers[0].Finished);
        Assert.True(_writers[0].Disposed);
    }

    [Fact]
    public void BookmarksRecordOffsetsFromTheStartOfTheRecording()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);

        Feed(recorder, frames: 300, keyFrameEvery: 120);
        Assert.True(recorder.TryAddBookmark(BookmarkSource.Manual, "the shot", out _));

        Feed(recorder, frames: 300, keyFrameEvery: 120, startFrame: 300);
        Assert.True(recorder.TryAddBookmark(BookmarkSource.AudioLoudnessSpike, null, out _));

        var metadata = recorder.Stop();
        Assert.NotNull(metadata);
        Assert.Equal(2, metadata!.Bookmarks.Count);

        AssertCloseTo(TimeSpan.FromSeconds(5), metadata.Bookmarks[0].Offset);
        Assert.Equal("the shot", metadata.Bookmarks[0].Label);
        Assert.Equal(BookmarkSource.Manual, metadata.Bookmarks[0].Source);

        AssertCloseTo(TimeSpan.FromSeconds(10), metadata.Bookmarks[1].Offset);
        Assert.Equal(BookmarkSource.AudioLoudnessSpike, metadata.Bookmarks[1].Source);
    }

    [Fact]
    public void BookmarkingWithoutARecordingIsRefusedWithAReason()
    {
        using var recorder = Recorder();

        Assert.False(recorder.TryAddBookmark(BookmarkSource.Manual, null, out var error));
        Assert.Contains("no session recording", error);
    }

    [Fact]
    public void BookmarkingBeforeTheFirstFrameIsRefused()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);

        Assert.False(recorder.TryAddBookmark(BookmarkSource.Manual, null, out var error));
        Assert.Contains("not written a frame", error);
    }

    [Fact]
    public void BookmarksAreClearedBetweenRecordings()
    {
        using var recorder = Recorder();

        recorder.TryStart(PathFor("a.mp4"), 1920, 1080, "H264", null, out _);
        Feed(recorder, frames: 120);
        recorder.TryAddBookmark(BookmarkSource.Manual, "first session", out _);
        recorder.Stop();

        recorder.TryStart(PathFor("b.mp4"), 1920, 1080, "H264", null, out _);
        Feed(recorder, frames: 120, startFrame: 120);
        var second = recorder.Stop();

        Assert.Empty(second!.Bookmarks);
    }

    [Fact]
    public void NotRecordingCostsOneVolatileReadPerSample()
    {
        // The recorder is permanently in the encoder's fan-out, so the disabled
        // path runs for every frame of every session whether or not anyone ever
        // records.
        using var recorder = Recorder();
        var payload = new byte[1000];

        AllocationAssert.NoPerIterationAllocation(
            i => recorder.TryWrite(payload, i * FrameTicks, FrameTicks, i % 120 == 0));
    }

    [Fact]
    public void RecordingDoesNotAllocatePerSample()
    {
        using var recorder = Recorder();
        recorder.TryStart(PathFor("s.mp4"), 1920, 1080, "H264", null, out _);

        // The fake writer keeps every sample, which allocates; measure the
        // recorder's own overhead against a writer that discards.
        using var quiet = new FullSessionRecorder(_ => new DiscardingWriter(), NullEngineLog.Instance);
        quiet.TryStart(PathFor("q.mp4"), 1920, 1080, "H264", null, out _);

        var payload = new byte[1000];
        quiet.TryWrite(payload, 0, FrameTicks, isKeyFrame: true);

        AllocationAssert.NoPerIterationAllocation(
            i => quiet.TryWrite(payload, i * FrameTicks, FrameTicks, i % 120 == 0));
    }

    [Fact]
    public void EmptyPathsAreRejected()
    {
        using var recorder = Recorder();
        Assert.Throws<ArgumentException>(
            () => recorder.TryStart("", 1920, 1080, "H264", null, out _));
    }

    [Fact]
    public void NullDependenciesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new FullSessionRecorder(null!, NullEngineLog.Instance));
        Assert.Throws<ArgumentNullException>(() => new FullSessionRecorder(_ => new DiscardingWriter(), null!));
    }

    private sealed class DiscardingWriter : ISessionWriter
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
