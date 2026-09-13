using Frost.Engine.Audio;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class AudioFormatTests
{
    [Fact]
    public void DefaultIsFortyEightKilohertzStereoFloat()
    {
        var format = AudioFormat.Default;
        format.Validate();

        Assert.Equal(48_000, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.Equal(AudioSampleType.Float32, format.SampleType);
        Assert.Equal(8, format.BytesPerFrame);
        Assert.Equal(384_000, format.BytesPerSecond);
    }

    [Fact]
    public void SixteenBitViewHalvesTheByteRate()
    {
        var pcm = AudioFormat.Default.AsInt16;

        Assert.Equal(AudioSampleType.Int16, pcm.SampleType);
        Assert.Equal(4, pcm.BytesPerFrame);
        Assert.Equal(192_000, pcm.BytesPerSecond);
        Assert.Same(pcm, pcm.AsInt16);
    }

    [Fact]
    public void FrameAndTickConversionsRoundTrip()
    {
        var format = AudioFormat.Default;

        Assert.Equal(TimeSpan.TicksPerSecond, format.FramesToTicks(48_000));
        Assert.Equal(48_000, format.TicksToFrames(TimeSpan.TicksPerSecond));
        Assert.Equal(480, format.TicksToFrames(format.FramesToTicks(480)));
    }

    [Fact]
    public void ByteBudgetMatchesTheDuration()
    {
        // 60 seconds of 48kHz stereo 16-bit is about 11MB - the figure that makes
        // buffering audio as PCM reasonable next to the video's ~93MB.
        Assert.Equal(11_520_000, AudioFormat.Default.AsInt16.BytesFor(TimeSpan.FromSeconds(60)));
    }

    [Theory]
    [InlineData(1000, 2)]
    [InlineData(48_000, 0)]
    [InlineData(48_000, 16)]
    public void UnsupportedFormatsAreRejected(int sampleRate, int channels) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = channels,
            SampleType = AudioSampleType.Float32,
        }.Validate());

    [Fact]
    public void ToStringDescribesTheFormat() =>
        Assert.Equal("48000Hz 2ch 32-bit float", AudioFormat.Default.ToString());
}

public sealed class AudioConversionTests
{
    [Fact]
    public void FloatConvertsToSixteenBit()
    {
        var source = new[] { 0f, 0.5f, -0.5f, 1f, -1f };
        var destination = new short[5];

        Assert.Equal(5, AudioConversion.FloatToInt16(source, destination));

        Assert.Equal(0, destination[0]);
        Assert.InRange(destination[1], 16_000, 16_600);
        Assert.InRange(destination[2], -16_600, -16_000);
        Assert.Equal(32_767, destination[3]);
        Assert.Equal(-32_767, destination[4]);
    }

    [Fact]
    public void FullScaleDoesNotWrapToANegativeClick()
    {
        // Scaling by 32768 makes exactly 1.0 overflow to -32768, which is an
        // audible click on every clipped peak. Game audio clips constantly.
        var destination = new short[1];
        AudioConversion.FloatToInt16([1.0f], destination);
        Assert.True(destination[0] > 0, $"full scale became {destination[0]}");
    }

    [Fact]
    public void OutOfRangeSamplesAreClampedNotWrapped()
    {
        var destination = new short[4];
        AudioConversion.FloatToInt16([2.5f, -2.5f, 100f, -100f], destination);

        Assert.Equal(32_767, destination[0]);
        Assert.Equal(-32_767, destination[1]);
        Assert.Equal(32_767, destination[2]);
        Assert.Equal(-32_767, destination[3]);
    }

    [Fact]
    public void ByteOutputIsLittleEndianSixteenBit()
    {
        var destination = new byte[4];
        var written = AudioConversion.FloatToInt16Bytes([0.5f, -0.5f], destination);

        Assert.Equal(4, written);

        var first = (short)(destination[0] | (destination[1] << 8));
        var second = (short)(destination[2] | (destination[3] << 8));

        Assert.InRange(first, 16_000, 16_600);
        Assert.InRange(second, -16_600, -16_000);
    }

    [Fact]
    public void ConversionStopsAtTheShorterBuffer()
    {
        Assert.Equal(2, AudioConversion.FloatToInt16([1f, 1f, 1f, 1f], new short[2]));
        Assert.Equal(4, AudioConversion.FloatToInt16Bytes([1f, 1f, 1f], new byte[4]));
    }

    [Fact]
    public void GainScalesAndClamps()
    {
        var samples = new[] { 0.25f, -0.25f, 0.9f };
        AudioConversion.ApplyGain(samples, 2.0);

        Assert.Equal(0.5f, samples[0], 4);
        Assert.Equal(-0.5f, samples[1], 4);
        Assert.Equal(1f, samples[2], 4);
    }

    [Fact]
    public void UnityGainIsASkipNotAMultiply()
    {
        var samples = new[] { 0.123456f, -0.7f };
        var original = samples.ToArray();

        AudioConversion.ApplyGain(samples, 1.0);

        Assert.Equal(original, samples);
    }

    [Fact]
    public void ZeroGainSilences()
    {
        var samples = new[] { 0.5f, -0.5f };
        AudioConversion.ApplyGain(samples, 0);
        Assert.All(samples, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void MutingSilencesRatherThanDroppingTheBlock()
    {
        // A gap would shift every sample after it and desync the track.
        var samples = new[] { 0.5f, -0.5f, 0.25f };
        AudioConversion.Silence(samples);

        Assert.Equal(3, samples.Length);
        Assert.All(samples, s => Assert.Equal(0f, s));
    }

    [Theory]
    [InlineData(1.0f, 0.0)]
    [InlineData(0.5f, -6.0)]
    [InlineData(0.1f, -20.0)]
    public void LevelIsReportedInDecibels(float amplitude, double expectedDb)
    {
        var samples = new float[480];
        Array.Fill(samples, amplitude);

        Assert.Equal(expectedDb, AudioConversion.LevelDb(samples), 1);
    }

    [Fact]
    public void SilenceReportsAFloorNotNegativeInfinity()
    {
        Assert.Equal(LoudnessSpikeDetector.SilenceDb, AudioConversion.LevelDb(new float[480]));
        Assert.Equal(LoudnessSpikeDetector.SilenceDb, AudioConversion.LevelDb([]));
    }

    [Fact]
    public void ConversionDoesNotAllocate()
    {
        // Runs on the audio capture thread.
        var source = new float[960];
        var destination = new byte[1920];
        Array.Fill(source, 0.3f);

        AllocationAssert.NoPerIterationAllocation(
            _ =>
            {
                AudioConversion.ApplyGain(source, 1.5);
                AudioConversion.FloatToInt16Bytes(source, destination);
                AudioConversion.LevelDb(source);
            },
            iterations: 20_000,
            warmUpIterations: 1_000);
    }
}

public sealed class AudioTrackBufferTests
{
    private static readonly AudioFormat Pcm = AudioFormat.Default.AsInt16;

    private static AudioTrackBuffer Buffer(double windowSeconds = 30) =>
        new(Pcm, TimeSpan.FromSeconds(windowSeconds));

    /// <summary>A 10ms block, as WASAPI would deliver.</summary>
    private static byte[] Block(byte fill)
    {
        var block = new byte[Pcm.BytesPerFrame * Pcm.SampleRate / 100];
        Array.Fill(block, fill);
        return block;
    }

    private static long BlockTicks => Pcm.FramesToTicks(Pcm.SampleRate / 100);

    [Fact]
    public void SizesItselfFromTheFormatAndWindow()
    {
        var buffer = Buffer(windowSeconds: 30);

        // 30s of 48kHz stereo 16-bit is ~5.8MB, rounded up to a power of two.
        Assert.True(buffer.ByteCapacity >= Pcm.BytesFor(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(30), buffer.Window);
    }

    [Fact]
    public void HoldsRoughlyTheWindowAndNoMore()
    {
        var buffer = Buffer(windowSeconds: 5);

        // A minute of audio into a five-second buffer.
        for (var i = 0; i < 6000; i++)
        {
            buffer.Write(Block((byte)i), i * BlockTicks);
        }

        Assert.InRange(buffer.HeldDuration, TimeSpan.FromSeconds(4.9), TimeSpan.FromSeconds(5.2));
        Assert.True(buffer.BytesHeld <= buffer.ByteCapacity);
        Assert.Equal(0, buffer.BlocksRefused);
    }

    [Fact]
    public void SnapshotReturnsTheRequestedWindowByteForByte()
    {
        var buffer = Buffer(windowSeconds: 10);

        for (var i = 0; i < 1000; i++)
        {
            buffer.Write(Block((byte)(i & 0xFF)), i * BlockTicks);
        }

        // The last second.
        var end = 1000 * BlockTicks;
        var start = end - TimeSpan.TicksPerSecond;

        var destination = new byte[buffer.MaxSnapshotBytes];
        var snapshot = buffer.Snapshot(start, end, destination);

        Assert.False(snapshot.IsEmpty);
        Assert.InRange(snapshot.Duration, TimeSpan.FromMilliseconds(990), TimeSpan.FromMilliseconds(1020));
        Assert.Equal(100, snapshot.BlockCount);
        Assert.Equal(100 * Block(0).Length, snapshot.ByteCount);

        // The first block copied is the one covering the window's start.
        Assert.InRange(snapshot.StartTicks, start - BlockTicks, start + BlockTicks);
    }

    [Fact]
    public void SnapshotReportsTheSpanItActuallyCoveredNotTheRequest()
    {
        // Whole blocks are taken, so a caller must timestamp from what it got.
        var buffer = Buffer(windowSeconds: 10);

        for (var i = 0; i < 500; i++)
        {
            buffer.Write(Block(1), i * BlockTicks);
        }

        var destination = new byte[buffer.MaxSnapshotBytes];

        // Request a window offset by half a block.
        var start = (100 * BlockTicks) + (BlockTicks / 2);
        var snapshot = buffer.Snapshot(start, start + TimeSpan.TicksPerSecond, destination);

        Assert.True(snapshot.StartTicks <= start, "the snapshot should start at or before the request");
        Assert.True(snapshot.EndTicks >= start + TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void ABlockStraddlingTheWindowStartIsIncluded()
    {
        // It carries audio the clip needs; dropping it would clip the first word
        // of whatever was said.
        var buffer = Buffer(windowSeconds: 10);
        buffer.Write(Block(7), 0);
        buffer.Write(Block(8), BlockTicks);

        var destination = new byte[buffer.MaxSnapshotBytes];
        var snapshot = buffer.Snapshot(BlockTicks / 2, BlockTicks * 2, destination);

        Assert.Equal(2, snapshot.BlockCount);
        Assert.Equal(0, snapshot.StartTicks);
    }

    [Fact]
    public void AnEmptyOrInvertedWindowIsEmpty()
    {
        var buffer = Buffer();
        buffer.Write(Block(1), 0);

        var destination = new byte[buffer.MaxSnapshotBytes];

        Assert.True(buffer.Snapshot(100, 100, destination).IsEmpty);
        Assert.True(buffer.Snapshot(200, 100, destination).IsEmpty);
    }

    [Fact]
    public void AWindowWithNoAudioIsEmpty()
    {
        var buffer = Buffer();
        buffer.Write(Block(1), 10 * TimeSpan.TicksPerSecond);

        var destination = new byte[buffer.MaxSnapshotBytes];
        Assert.True(buffer.Snapshot(0, TimeSpan.TicksPerSecond, destination).IsEmpty);
    }

    [Fact]
    public void ATooSmallDestinationIsFilledAsFarAsItGoes()
    {
        // Reporting what was copied beats truncating mid-frame.
        var buffer = Buffer(windowSeconds: 10);

        for (var i = 0; i < 500; i++)
        {
            buffer.Write(Block(1), i * BlockTicks);
        }

        var block = Block(1).Length;
        var destination = new byte[block * 3];
        var snapshot = buffer.Snapshot(0, 500 * BlockTicks, destination);

        Assert.Equal(3, snapshot.BlockCount);
        Assert.Equal(block * 3, snapshot.ByteCount);
    }

    [Fact]
    public void EmptyWritesAreRefused() => Assert.False(Buffer().Write([], 0));

    [Fact]
    public void ClearEmptiesTheBuffer()
    {
        var buffer = Buffer();
        buffer.Write(Block(1), 0);
        buffer.Clear();

        Assert.Equal(0, buffer.BytesHeld);
        Assert.Equal(TimeSpan.Zero, buffer.HeldDuration);
    }

    [Fact]
    public void WritingDoesNotAllocate()
    {
        var buffer = Buffer(windowSeconds: 5);
        var block = Block(42);

        AllocationAssert.NoPerIterationAllocation(
            i => buffer.Write(block, i * BlockTicks),
            iterations: 50_000,
            warmUpIterations: 2_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveWindowsAreRejected(double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AudioTrackBuffer(Pcm, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void NullFormatIsRejected() =>
        Assert.Throws<ArgumentNullException>(
            () => new AudioTrackBuffer(null!, TimeSpan.FromSeconds(30)));

    [Fact]
    public void AnInvalidFormatIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioTrackBuffer(
            new AudioFormat { SampleRate = 100, Channels = 2, SampleType = AudioSampleType.Int16 },
            TimeSpan.FromSeconds(30)));
}

public sealed class AudioFormatRoundingTests
{
    private static readonly AudioFormat Format = AudioFormat.Default.AsInt16;

    [Fact]
    public void RoundTrippingFramesThroughTicksIsLossless()
    {
        // Truncating in both directions turns 200 frames into 199, which showed up
        // as an off-by-one in the gap filler's overlap trimming.
        for (var frames = 1; frames <= 2048; frames++)
        {
            var ticks = Format.FramesToTicks(frames);
            Assert.Equal(frames, Format.TicksToFramesRounded(ticks));
        }
    }

    [Fact]
    public void RoundingSendsASubFrameDurationToZero()
    {
        // What the gap filler needs: a fraction of a frame must not become a whole
        // one, or it would accumulate into drift.
        Assert.Equal(0, Format.TicksToFramesRounded(3));
        Assert.Equal(0, Format.TicksToFramesRounded(-3));
    }

    [Fact]
    public void RoundingHandlesNegativeDurationsSymmetrically()
    {
        var ticks = Format.FramesToTicks(500);
        Assert.Equal(-500, Format.TicksToFramesRounded(-ticks));
    }

    [Fact]
    public void TruncatingConversionIsStillAvailableAndStillTruncates() =>
        Assert.Equal(0, Format.TicksToFrames(Format.FramesToTicks(1) - 1));
}
