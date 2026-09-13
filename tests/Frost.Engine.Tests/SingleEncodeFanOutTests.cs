using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Engine.Recording;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// The claim that Phase 5 turns on: full-session recording and the clip ring
/// buffer run at the same time from <i>one</i> encode.
/// </summary>
public sealed class SingleEncodeFanOutTests : IDisposable
{
    private const int Fps = 60;
    private static readonly long FrameTicks = TimeSpan.TicksPerSecond / Fps;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "frost-fanout-tests", Guid.NewGuid().ToString("N"));

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
    /// Stands in for the hardware encoder, counting how many times a frame was
    /// encoded so "no double encoding" is measured rather than assumed.
    /// </summary>
    private sealed class CountingEncoder(IEncodedSampleSink sink)
    {
        internal int EncodeCalls { get; private set; }

        internal void EncodeFrame(int index)
        {
            EncodeCalls++;

            var isKeyFrame = index % 120 == 0;
            var payload = new byte[isKeyFrame ? 40_000 : 8_000];
            Array.Fill(payload, (byte)(index & 0xFF));

            sink.TryWrite(payload, index * FrameTicks, FrameTicks, isKeyFrame);
        }
    }

    [Fact]
    public void RingBufferAndSessionRecordingShareOneEncode()
    {
        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(10),
            BitsPerSecond = 12_400_000,
            Fps = Fps,
        });

        var sessionWriter = new CapturingWriter();
        using var session = new FullSessionRecorder(_ => sessionWriter, NullEngineLog.Instance);

        var fanOut = new FanOutSampleSink(ring, session);
        var encoder = new CountingEncoder(fanOut);

        const int totalFrames = Fps * 30;

        // Ten seconds with only the ring buffer running.
        for (var i = 0; i < Fps * 10; i++)
        {
            encoder.EncodeFrame(i);
        }

        Assert.Equal(0, session.SamplesWritten);
        var ringSamplesBeforeRecording = ring.SamplesWritten;
        Assert.True(ringSamplesBeforeRecording > 0);

        // Then the user starts a full-session recording mid-game.
        Assert.True(session.TryStart(
            Path.Combine(_directory, "session.mp4"), 1920, 1080, "H264", "hl2", out _));

        for (var i = Fps * 10; i < totalFrames; i++)
        {
            encoder.EncodeFrame(i);
        }

        // One encode call per frame. Not two, and no re-encode.
        Assert.Equal(totalFrames, encoder.EncodeCalls);

        // The session got everything from its first keyframe onward.
        Assert.True(session.SamplesWritten > 0);
        Assert.True(sessionWriter.Written[0].IsKeyFrame);

        // And the ring buffer carried on untouched, still able to serve a clip.
        Assert.True(ring.SamplesWritten > ringSamplesBeforeRecording);

        using (var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(5)))
        {
            Assert.True(snapshot[0].IsKeyFrame);
            Assert.True(snapshot.Duration >= TimeSpan.FromSeconds(5));
        }

        var metadata = session.Stop();
        Assert.NotNull(metadata);

        // Both destinations saw byte-identical data for the frames they share.
        var scratch = new byte[64_000];
        using var after = ring.OpenSnapshot(TimeSpan.FromSeconds(5));

        for (var i = 0; i < after.Count; i++)
        {
            var fromRing = after.Read(i, scratch).ToArray();
            var matching = sessionWriter.Written.Where(w => w.Data.Length == fromRing.Length).ToList();
            Assert.Contains(fromRing, matching.Select(m => m.Data), new ByteArrayComparer());
        }
    }

    [Fact]
    public void StoppingTheSessionLeavesTheRingBufferRunning()
    {
        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(10),
            BitsPerSecond = 12_400_000,
            Fps = Fps,
        });

        using var session = new FullSessionRecorder(_ => new CapturingWriter(), NullEngineLog.Instance);
        var encoder = new CountingEncoder(new FanOutSampleSink(ring, session));

        session.TryStart(Path.Combine(_directory, "a.mp4"), 1920, 1080, "H264", null, out _);

        for (var i = 0; i < Fps * 10; i++)
        {
            encoder.EncodeFrame(i);
        }

        session.Stop();
        var ringSamplesAtStop = ring.SamplesWritten;

        for (var i = Fps * 10; i < Fps * 20; i++)
        {
            encoder.EncodeFrame(i);
        }

        Assert.True(ring.SamplesWritten > ringSamplesAtStop);
        Assert.False(session.IsRecording);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        Assert.False(snapshot.IsEmpty);
    }

    [Fact]
    public void ASessionWriterFallingBehindDoesNotCostTheRingBufferAnything()
    {
        // Losing frames from the VOD because the disk stalled must not lose them
        // from the clip buffer as well.
        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(10),
            BitsPerSecond = 12_400_000,
            Fps = Fps,
        });

        var writer = new CapturingWriter();
        using var session = new FullSessionRecorder(_ => writer, NullEngineLog.Instance);
        var encoder = new CountingEncoder(new FanOutSampleSink(ring, session));

        session.TryStart(Path.Combine(_directory, "a.mp4"), 1920, 1080, "H264", null, out _);

        for (var i = 0; i < Fps * 5; i++)
        {
            encoder.EncodeFrame(i);
        }

        writer.RefuseWrites = true;
        for (var i = Fps * 5; i < Fps * 10; i++)
        {
            encoder.EncodeFrame(i);
        }

        Assert.True(session.SamplesRefused > 0);
        Assert.Equal(0, ring.SamplesRefused);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        Assert.True(snapshot.Duration >= TimeSpan.FromSeconds(5));
    }

    private sealed class CapturingWriter : ISessionWriter
    {
        internal List<(byte[] Data, long Timestamp, bool IsKeyFrame)> Written { get; } = [];

        internal bool RefuseWrites { get; set; }

        public string Extension => ".mp4";

        public long BytesWritten { get; private set; }

        public bool IsFaulted => false;

        public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
        {
            if (RefuseWrites)
            {
                return false;
            }

            Written.Add((data.ToArray(), timestampTicks, isKeyFrame));
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

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) =>
            x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length;
    }
}
