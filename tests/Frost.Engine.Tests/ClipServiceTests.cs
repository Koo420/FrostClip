using Frost.Engine.Clips;
using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Shared.Clips;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>Records what it was asked to write, and can be made slow or made to fail.</summary>
internal sealed class FakeClipWriter : IClipWriter
{
    private readonly ManualResetEventSlim _release = new(initialState: true);

    public string Extension => ".mp4";

    internal List<(string Path, int SampleCount, TimeSpan Duration, byte[][] Bytes)> Written { get; } = [];

    internal bool Throw { get; set; }

    /// <summary>
    /// Copy each sample's bytes out for inspection. Off for tests that only care
    /// about scheduling: reading a 30-second snapshot eight times over is ~100MB of
    /// copying, which is pure noise for those and makes them slow on a busy host.
    /// </summary>
    internal bool CaptureBytes { get; set; } = true;

    /// <summary>Blocks writes until <see cref="Release"/>, to test queueing.</summary>
    internal void Hold() => _release.Reset();

    internal void Release() => _release.Set();

    public ClipMetadata Write(RingSnapshot snapshot, string path, ClipRequest request)
    {
        _release.Wait(TimeSpan.FromSeconds(30));

        if (Throw)
        {
            throw new IOException("disk on fire");
        }

        var bytes = Array.Empty<byte[]>();

        if (CaptureBytes)
        {
            var scratch = new byte[Math.Max(1, snapshot.LargestSampleLength)];
            bytes = new byte[snapshot.Count][];
            for (var i = 0; i < snapshot.Count; i++)
            {
                bytes[i] = snapshot.Read(i, scratch).ToArray();
            }
        }

        lock (Written)
        {
            Written.Add((path, snapshot.Count, snapshot.Duration, bytes));
        }

        return new ClipMetadata
        {
            FilePath = path,
            DisplayName = Path.GetFileNameWithoutExtension(path),
            CreatedUtc = DateTimeOffset.UtcNow,
            DurationTicks = snapshot.Duration.Ticks,
            SizeBytes = snapshot.TotalBytes,
            Width = 1920,
            Height = 1080,
            Codec = "H264",
            GameName = request.GameName,
        };
    }
}

public sealed class ClipServiceTests
{
    private const int Fps = 60;
    private static readonly long FrameTicks = TimeSpan.TicksPerSecond / Fps;

    private static EncodedSampleRing FilledRing(int seconds = 30)
    {
        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(60),
            BitsPerSecond = 12_400_000,
            Fps = Fps,
        });

        var keyFrame = new byte[40_000];
        var interFrame = new byte[10_000];

        for (var i = 0; i < seconds * Fps; i++)
        {
            var isKey = i % 120 == 0;
            ring.TryWrite(isKey ? keyFrame : interFrame, i * FrameTicks, FrameTicks, isKey);
        }

        return ring;
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "frost-clip-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AHotkeyRequestProducesAClip()
    {
        var ring = FilledRing();
        var writer = new FakeClipWriter();
        var directory = TempDirectory();
        using var service = new ClipService(ring, writer, directory, NullEngineLog.Instance, _ => false);

        ClipResult? completed = null;
        service.ClipCompleted += result => completed = result;

        Assert.True(service.Request(new ClipRequest(TimeSpan.FromSeconds(15), "15s", "Half-Life 2")));
        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(60)));

        var written = Assert.Single(writer.Written);
        Assert.StartsWith(Path.Combine(directory, "Half-Life 2 "), written.Path);
        Assert.EndsWith("(15s).mp4", written.Path);
        Assert.True(written.Duration >= TimeSpan.FromSeconds(15));

        Assert.NotNull(completed);
        Assert.True(completed!.Succeeded);
        Assert.Equal("Half-Life 2", completed.Metadata!.GameName);
        Assert.Equal(1, service.ClipsSaved);
    }

    [Fact]
    public void FeedbackFiresOnTheKeypressNotAfterTheFileIsWritten()
    {
        // The 150ms toast budget depends on this: the request event is raised
        // synchronously, before any disk work happens.
        var ring = FilledRing();
        var writer = new FakeClipWriter();
        writer.Hold();

        using var service = new ClipService(
            ring, writer, TempDirectory(), NullEngineLog.Instance, _ => false);

        var requestedThread = -1;
        service.ClipRequested += _ => requestedThread = Environment.CurrentManagedThreadId;

        service.Request(new ClipRequest(TimeSpan.FromSeconds(15)));

        Assert.Equal(Environment.CurrentManagedThreadId, requestedThread);
        Assert.Empty(writer.Written);

        writer.Release();
        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void RequestDoesNotBlockWhileAClipIsBeingWritten()
    {
        var ring = FilledRing();
        var writer = new FakeClipWriter();
        writer.Hold();

        using var service = new ClipService(
            ring, writer, TempDirectory(), NullEngineLog.Instance, _ => false);

        service.Request(new ClipRequest(TimeSpan.FromSeconds(15)));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(service.Request(new ClipRequest(TimeSpan.FromSeconds(30))));
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 250, $"Request took {clock.ElapsedMilliseconds}ms");

        writer.Release();
        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(60)));
        Assert.Equal(2, writer.Written.Count);
    }

    [Fact]
    public void ClipsAreWrittenOneAtATime()
    {
        // Each write pins the ring buffer; two at once could together outlast its
        // headroom.
        var ring = FilledRing();
        var writer = new SnapshotOverlapDetectingWriter();

        using var service = new ClipService(
            ring, writer, TempDirectory(), NullEngineLog.Instance, _ => false);

        for (var i = 0; i < 5; i++)
        {
            service.Request(new ClipRequest(TimeSpan.FromSeconds(5)));
        }

        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(60)));
        Assert.False(writer.SawOverlap);
        Assert.Equal(5, writer.Completed);
    }

    [Fact]
    public void AFullQueueDropsExtraRequestsRatherThanWritingFiftyFiles()
    {
        var ring = FilledRing();
        var writer = new FakeClipWriter { CaptureBytes = false };
        writer.Hold();

        using var service = new ClipService(
            ring, writer, TempDirectory(), NullEngineLog.Instance, _ => false);

        // Request until one is refused. Asserting an exact accept count would be a
        // race: the clip thread may already have dequeued the first request (it is
        // blocked inside the writer), freeing a slot.
        var accepted = 0;
        while (service.Request(new ClipRequest(TimeSpan.FromSeconds(5))))
        {
            accepted++;

            Assert.True(
                accepted <= ClipService.MaxPendingRequests + 1,
                $"the queue accepted {accepted} requests; it is supposed to be bounded at " +
                $"{ClipService.MaxPendingRequests}");
        }

        Assert.InRange(accepted, ClipService.MaxPendingRequests, ClipService.MaxPendingRequests + 1);
        Assert.Equal(1, service.RequestsDropped);

        writer.Release();
        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void AnEmptyBufferReportsAClearReasonInsteadOfWritingAnEmptyFile()
    {
        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(30),
            BitsPerSecond = 12_400_000,
            Fps = Fps,
        });

        var writer = new FakeClipWriter();
        using var service = new ClipService(
            ring, writer, TempDirectory(), NullEngineLog.Instance, _ => false);

        ClipResult? completed = null;
        service.ClipCompleted += result => completed = result;

        service.Request(new ClipRequest(TimeSpan.FromSeconds(15)));
        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(60)));

        Assert.Empty(writer.Written);
        Assert.NotNull(completed);
        Assert.False(completed!.Succeeded);
        Assert.Contains("buffer is still filling", completed.Error);
        Assert.Equal(1, service.ClipsFailed);
    }

    [Fact]
    public void AFailedWriteDoesNotTakeTheServiceDown()
    {
        var ring = FilledRing();
        var writer = new FakeClipWriter { Throw = true };

        using var service = new ClipService(
            ring, writer, TempDirectory(), NullEngineLog.Instance, _ => false);

        var results = new List<ClipResult>();
        service.ClipCompleted += results.Add;

        service.Request(new ClipRequest(TimeSpan.FromSeconds(5)));
        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(60)));

        writer.Throw = false;
        service.Request(new ClipRequest(TimeSpan.FromSeconds(5)));
        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(60)));

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Succeeded);
        Assert.Contains("disk on fire", results[0].Error);
        Assert.True(results[1].Succeeded);
        Assert.Equal(1, service.ClipsSaved);
        Assert.Equal(1, service.ClipsFailed);
    }

    [Fact]
    public void ZeroLengthRequestsAreRejected()
    {
        var ring = FilledRing();
        using var service = new ClipService(
            ring, new FakeClipWriter(), TempDirectory(), NullEngineLog.Instance, _ => false);

        Assert.False(service.Request(new ClipRequest(TimeSpan.Zero)));
        Assert.False(service.Request(new ClipRequest(TimeSpan.FromSeconds(-1))));
    }

    [Fact]
    public void ClipBytesMatchWhatTheRingHeld()
    {
        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(10),
            BitsPerSecond = 12_400_000,
            Fps = Fps,
        });

        var expected = new List<byte[]>();
        for (var i = 0; i < 300; i++)
        {
            var data = new byte[1000 + i];
            Array.Fill(data, (byte)(i & 0xFF));
            expected.Add(data);
            ring.TryWrite(data, i * FrameTicks, FrameTicks, i % 60 == 0);
        }

        var writer = new FakeClipWriter();
        using var service = new ClipService(
            ring, writer, TempDirectory(), NullEngineLog.Instance, _ => false);

        service.Request(new ClipRequest(TimeSpan.FromSeconds(5)));
        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(60)));

        var written = Assert.Single(writer.Written);
        foreach (var actual in written.Bytes)
        {
            var source = expected.First(e => e.Length == actual.Length);
            Assert.Equal(source, actual);
        }
    }

    /// <summary>Fails loudly if two snapshots are ever live at once.</summary>
    private sealed class SnapshotOverlapDetectingWriter : IClipWriter
    {
        private int _active;

        public string Extension => ".mp4";

        internal bool SawOverlap { get; private set; }

        internal int Completed { get; private set; }

        public ClipMetadata Write(RingSnapshot snapshot, string path, ClipRequest request)
        {
            if (Interlocked.Increment(ref _active) != 1)
            {
                SawOverlap = true;
            }

            Thread.Sleep(10);
            Interlocked.Decrement(ref _active);
            Completed++;

            return new ClipMetadata
            {
                FilePath = path,
                DisplayName = "x",
                CreatedUtc = DateTimeOffset.UtcNow,
                DurationTicks = snapshot.Duration.Ticks,
                SizeBytes = snapshot.TotalBytes,
                Width = 1920,
                Height = 1080,
                Codec = "H264",
            };
        }
    }
}
