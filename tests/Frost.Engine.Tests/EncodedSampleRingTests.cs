using Frost.Engine.Encoding;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class RingBufferOptionsTests
{
    private static RingBufferOptions Options(double seconds = 30, long bitrate = 12_400_000, int fps = 60) =>
        new()
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(seconds),
            BitsPerSecond = bitrate,
            Fps = fps,
        };

    [Fact]
    public void ArenaIsSizedFromBitrateDurationAndHeadroom()
    {
        var options = Options(seconds: 30);
        options.Validate();

        // 12.4Mbps * 45s (30s * 1.5 headroom) = ~69.75MB
        Assert.Equal(BitrateCalculator.BytesFor(12_400_000, TimeSpan.FromSeconds(45)), options.ArenaBytes);
        Assert.Equal((int)((30 * 60 * 1.5) + 64), options.ArenaSamples);
    }

    [Fact]
    public void ArenaIsCappedSoAMisconfigurationCannotExhaustRam()
    {
        var options = Options(seconds: 1800, bitrate: 150_000_000) with { MaxBytes = 256L * 1024 * 1024 };
        Assert.Equal(256L * 1024 * 1024, options.ArenaBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3600)]
    public void ImpossibleDurationsAreRejected(double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(seconds).Validate());

    [Fact]
    public void TooLittleHeadroomIsRejectedBecauseAClipSaveWouldEvictItsOwnFrames() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (Options() with { HeadroomFactor = 1.0 }).Validate());

    [Fact]
    public void OutOfRangeBitrateAndFpsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (Options(bitrate: 1000)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Options(fps: 0)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Options() with { MaxBytes = 1024 }).Validate());
    }
}

public sealed class EncodedSampleRingTests
{
    private const int Fps = 60;
    private static readonly long FrameTicks = TimeSpan.TicksPerSecond / Fps;

    private static EncodedSampleRing Ring(double seconds = 30, long bitrate = 12_400_000) =>
        new(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(seconds),
            BitsPerSecond = bitrate,
            Fps = Fps,
        });

    /// <summary>Feeds frames with a keyframe every <paramref name="keyFrameEvery"/> frames.</summary>
    private static void Feed(
        EncodedSampleRing ring, int frames, int keyFrameEvery = 120, int frameBytes = 25_000, int startFrame = 0)
    {
        var keyFrame = new byte[frameBytes * 4];
        var interFrame = new byte[frameBytes];
        Array.Fill(keyFrame, (byte)0xAA);
        Array.Fill(interFrame, (byte)0x55);

        for (var i = startFrame; i < startFrame + frames; i++)
        {
            var isKey = i % keyFrameEvery == 0;
            ring.TryWrite(isKey ? keyFrame : interFrame, i * FrameTicks, FrameTicks, isKey);
        }
    }

    [Fact]
    public void HoldsRoughlyTheConfiguredDuration()
    {
        var ring = Ring(seconds: 10);

        // Two minutes of frames into a ten-second buffer.
        Feed(ring, frames: Fps * 120);

        var held = ring.HeldDuration;

        // At least the requested window, and no more than the headroom allows.
        Assert.True(held >= TimeSpan.FromSeconds(10), $"held only {held}");
        Assert.True(held <= TimeSpan.FromSeconds(10 * 1.5) + TimeSpan.FromSeconds(2), $"held {held}");
        Assert.Equal(0, ring.SamplesRefused);
    }

    [Fact]
    public void MemoryUseStaysBoundedNoMatterHowLongItRuns()
    {
        // The property that lets the Engine sit armed indefinitely.
        var ring = Ring(seconds: 15);
        Feed(ring, frames: Fps * 600);

        Assert.True(ring.BytesHeld <= ring.ArenaBytes);
        Assert.True(ring.Count <= ring.ArenaSamples);
    }

    [Fact]
    public void SnapshotStartsOnAKeyFrame()
    {
        // Without this the clip's first frame references frames that are not in
        // the file, and no decoder will play it.
        var ring = Ring(seconds: 30);
        Feed(ring, frames: Fps * 60, keyFrameEvery: 120);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(15));

        Assert.False(snapshot.IsEmpty);
        Assert.True(snapshot[0].IsKeyFrame);
    }

    [Fact]
    public void SnapshotCoversAtLeastTheRequestedDuration()
    {
        var ring = Ring(seconds: 30);
        Feed(ring, frames: Fps * 60, keyFrameEvery: 120);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(15));

        Assert.True(
            snapshot.Duration >= TimeSpan.FromSeconds(15),
            $"snapshot covered only {snapshot.Duration}");

        // Keyframe alignment can only add up to one keyframe interval (2s here).
        Assert.True(
            snapshot.Duration <= TimeSpan.FromSeconds(17.1),
            $"snapshot covered {snapshot.Duration}, more than one keyframe interval over");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void OneBufferServesEveryPresetDuration(int seconds)
    {
        // Different hotkeys ask for different lengths from the same buffer; a
        // shorter clip is just a shorter read.
        var ring = Ring(seconds: 60);
        Feed(ring, frames: Fps * 120, keyFrameEvery: 120);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(seconds));

        Assert.True(snapshot[0].IsKeyFrame);
        Assert.True(snapshot.Duration >= TimeSpan.FromSeconds(seconds));
        Assert.True(snapshot.Duration < TimeSpan.FromSeconds(seconds + 2.1));
    }

    [Fact]
    public void AskingForMoreThanIsHeldReturnsEverythingRatherThanFailing()
    {
        // The first seconds after launch: a hotkey press should still save what
        // exists.
        var ring = Ring(seconds: 60);
        Feed(ring, frames: Fps * 5, keyFrameEvery: 120);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(60));

        Assert.Equal(Fps * 5, snapshot.Count);
        Assert.True(snapshot[0].IsKeyFrame);
    }

    [Fact]
    public void SnapshotOfAnEmptyBufferIsEmptyRatherThanAnError()
    {
        var ring = Ring();
        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(15));

        Assert.True(snapshot.IsEmpty);
        Assert.Equal(0, snapshot.Count);
        Assert.Equal(TimeSpan.Zero, snapshot.Duration);
    }

    [Fact]
    public void SnapshotBytesReadBackExactly()
    {
        var ring = Ring(seconds: 5);

        var expected = new List<byte[]>();
        for (var i = 0; i < 120; i++)
        {
            var data = new byte[500 + i];
            Array.Fill(data, (byte)(i & 0xFF));
            expected.Add(data);
            ring.TryWrite(data, i * FrameTicks, FrameTicks, i % 30 == 0);
        }

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        var scratch = new byte[snapshot.LargestSampleLength];

        for (var i = 0; i < snapshot.Count; i++)
        {
            var sample = snapshot[i];
            var bytes = snapshot.Read(i, scratch).ToArray();

            var source = expected.First(e => e.Length == sample.Length);
            Assert.Equal(source, bytes);
        }
    }

    [Fact]
    public void IncomingFramesKeepBeingAcceptedWhileAClipIsBeingWritten()
    {
        // The headroom's whole purpose: saving a clip must not stop recording.
        var ring = Ring(seconds: 10);
        Feed(ring, frames: Fps * 30, keyFrameEvery: 120);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(10));
        var refusedBefore = ring.SamplesRefused;

        // Half a second more of capture while the clip is "being written".
        Feed(ring, frames: 30, keyFrameEvery: 120, startFrame: Fps * 30);

        Assert.Equal(refusedBefore, ring.SamplesRefused);
        Assert.True(snapshot[0].IsKeyFrame);
    }

    [Fact]
    public void PinnedSamplesAreNotEvictedEvenUnderSustainedPressure()
    {
        // If eviction could pass an open snapshot, the clip being written would
        // silently get the wrong bytes. Refusing new frames is the safe answer.
        var ring = Ring(seconds: 5);
        Feed(ring, frames: Fps * 20, keyFrameEvery: 120);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        var firstSample = snapshot[0];
        var scratch = new byte[snapshot.LargestSampleLength];
        var expected = snapshot.Read(0, scratch).ToArray();

        // Far more than the headroom can absorb.
        Feed(ring, frames: Fps * 60, keyFrameEvery: 120, startFrame: Fps * 20);

        Assert.True(ring.SamplesRefused > 0, "expected the ring to refuse frames rather than evict pinned ones");

        // The pinned sample is still readable and still correct.
        Assert.Equal(expected, snapshot.Read(0, scratch).ToArray());
        Assert.Equal(firstSample.SequenceNumber, snapshot[0].SequenceNumber);
    }

    [Fact]
    public void RecordingRecoversOnceTheSnapshotIsClosed()
    {
        var ring = Ring(seconds: 5);
        Feed(ring, frames: Fps * 20, keyFrameEvery: 120);

        var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        Feed(ring, frames: Fps * 60, keyFrameEvery: 120, startFrame: Fps * 20);
        Assert.True(ring.SamplesRefused > 0);
        snapshot.Dispose();

        var refusedAfterClose = ring.SamplesRefused;
        Feed(ring, frames: Fps * 10, keyFrameEvery: 120, startFrame: Fps * 80);

        Assert.Equal(refusedAfterClose, ring.SamplesRefused);
    }

    [Fact]
    public void TwoConcurrentSnapshotsAreRefused()
    {
        var ring = Ring();
        Feed(ring, frames: Fps * 10);

        using var first = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => ring.OpenSnapshot(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ASnapshotCanBeOpenedAgainAfterTheFirstIsDisposed()
    {
        var ring = Ring();
        Feed(ring, frames: Fps * 10);

        ring.OpenSnapshot(TimeSpan.FromSeconds(5)).Dispose();
        using var second = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        Assert.False(second.IsEmpty);
    }

    [Fact]
    public void UsingADisposedSnapshotIsAnError()
    {
        var ring = Ring();
        Feed(ring, frames: Fps * 10);

        var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        snapshot.Dispose();

        Assert.Throws<ObjectDisposedException>(() => snapshot[0]);
    }

    [Fact]
    public void SnapshotIndexIsBoundsChecked()
    {
        var ring = Ring();
        Feed(ring, frames: Fps * 10);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot[snapshot.Count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot[-1]);
    }

    [Fact]
    public void NonPositiveSnapshotDurationsAreRejected()
    {
        var ring = Ring();
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.OpenSnapshot(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.OpenSnapshot(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ClearingWhileASnapshotIsOpenIsRefused()
    {
        var ring = Ring();
        Feed(ring, frames: Fps * 10);

        using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(ring.Clear);
    }

    [Fact]
    public void ClearEmptiesTheBuffer()
    {
        var ring = Ring();
        Feed(ring, frames: Fps * 10);
        ring.Clear();

        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.BytesHeld);
    }

    [Fact]
    public void ASampleLargerThanTheWholeArenaIsRefused()
    {
        var ring = Ring(seconds: 5, bitrate: 2_000_000);
        var absurd = new byte[ring.ArenaBytes + 1];

        Assert.False(ring.TryWrite(absurd, 0, FrameTicks, true));
        Assert.Equal(1, ring.SamplesRefused);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void EmptyWritesAreRefused()
    {
        var ring = Ring();
        Assert.False(ring.TryWrite([], 0, FrameTicks, true));
    }

    [Fact]
    public void SteadyStateWritesDoNotAllocate()
    {
        // This runs on the encode thread for the entire time the Engine is armed.
        var ring = Ring(seconds: 15);
        var frame = new byte[25_000];

        AllocationAssert.NoPerIterationAllocation(
            i => ring.TryWrite(frame, i * FrameTicks, FrameTicks, i % 120 == 0),
            iterations: 100_000);
    }

    [Fact]
    public void WritingAndSnapshottingConcurrentlyStaysConsistent()
    {
        // Real shape: the encode thread writes continuously while the hotkey
        // thread takes snapshots.
        var ring = Ring(seconds: 5);
        var stop = false;
        Exception? failure = null;

        var writer = new Thread(() =>
        {
            try
            {
                var frame = new byte[20_000];
                for (var i = 0; !Volatile.Read(ref stop); i++)
                {
                    ring.TryWrite(frame, i * FrameTicks, FrameTicks, i % 120 == 0);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true };

        writer.Start();

        try
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                using var snapshot = ring.OpenSnapshot(TimeSpan.FromSeconds(2));
                if (snapshot.IsEmpty)
                {
                    continue;
                }

                Assert.True(snapshot[0].IsKeyFrame);

                var scratch = new byte[snapshot.LargestSampleLength];
                for (var i = 0; i < snapshot.Count; i++)
                {
                    Assert.Equal(snapshot[i].Length, snapshot.Read(i, scratch).Length);
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            writer.Join(TimeSpan.FromSeconds(10));
        }

        Assert.Null(failure);
    }
}

public sealed class RingBufferCapTests
{
    [Fact]
    public void AnUncappedBufferReportsTheDurationItWasAskedFor()
    {
        var options = new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(30),
            BitsPerSecond = 12_400_000,
            Fps = 60,
        };

        Assert.False(options.IsLimitedByMemoryCap);
        Assert.Equal(TimeSpan.FromSeconds(30), options.EffectiveMaxTrailingDuration);
    }

    [Fact]
    public void ACappedBufferReportsTheShorterDurationInsteadOfHidingIt()
    {
        // Five minutes at 50Mbps needs ~1.75GB with headroom. Capped at 256MB the
        // buffer holds far less, and a user who asked for five minutes has to be
        // told.
        var options = new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromMinutes(5),
            BitsPerSecond = 50_000_000,
            Fps = 60,
            MaxBytes = 256L * 1024 * 1024,
        };

        Assert.True(options.IsLimitedByMemoryCap);
        Assert.True(options.EffectiveMaxTrailingDuration < TimeSpan.FromMinutes(5));
        Assert.True(options.EffectiveMaxTrailingDuration > TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void TheRingExposesTheSameNumbers()
    {
        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromMinutes(5),
            BitsPerSecond = 50_000_000,
            Fps = 60,
            MaxBytes = 256L * 1024 * 1024,
        });

        Assert.True(ring.IsLimitedByMemoryCap);
        Assert.True(ring.EffectiveMaxTrailingDuration < ring.MaxTrailingDuration);
    }
}
