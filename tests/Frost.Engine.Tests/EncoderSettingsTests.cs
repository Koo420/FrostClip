using Frost.Engine.Encoding;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class BitrateCalculatorTests
{
    [Theory]
    [InlineData(1920, 1080, 60, 12_400_000)]
    [InlineData(1920, 1080, 30, 6_200_000)]
    [InlineData(2560, 1440, 60, 22_100_000)]
    [InlineData(1280, 720, 60, 5_500_000)]
    public void H264RecommendationsScaleWithPixelsAndRate(int width, int height, int fps, long expected) =>
        Assert.Equal(expected, BitrateCalculator.Recommend(VideoCodec.H264, width, height, fps));

    [Fact]
    public void NewerCodecsGetFewerBitsForTheSamePicture()
    {
        var h264 = BitrateCalculator.Recommend(VideoCodec.H264, 1920, 1080, 60);
        var hevc = BitrateCalculator.Recommend(VideoCodec.Hevc, 1920, 1080, 60);
        var av1 = BitrateCalculator.Recommend(VideoCodec.Av1, 1920, 1080, 60);

        Assert.True(hevc < h264);
        Assert.True(av1 < hevc);
    }

    [Fact]
    public void SmallCapturesGetTheFloorRatherThanSomethingUnwatchable()
    {
        Assert.Equal(
            BitrateCalculator.MinimumBitsPerSecond,
            BitrateCalculator.Recommend(VideoCodec.H264, 320, 240, 30));
    }

    [Fact]
    public void HugeCapturesAreCapped()
    {
        Assert.Equal(
            BitrateCalculator.MaximumBitsPerSecond,
            BitrateCalculator.Recommend(VideoCodec.H264, 7680, 4320, 120));
    }

    [Fact]
    public void RecommendationsAreRoundNumbers()
    {
        foreach (var fps in new[] { 30, 60, 120, 144 })
        {
            var bitrate = BitrateCalculator.Recommend(VideoCodec.H264, 1920, 1080, fps);
            Assert.Equal(0, bitrate % 100_000);
        }
    }

    [Theory]
    [InlineData(0, 1080, 60)]
    [InlineData(1920, 0, 60)]
    [InlineData(1920, 1080, 0)]
    public void InvalidGeometryIsRejected(int width, int height, int fps) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BitrateCalculator.Recommend(VideoCodec.H264, width, height, fps));

    [Fact]
    public void ByteBudgetMatchesBitrateTimesDuration()
    {
        // 12.4Mbps for 30 seconds: what a 30s ring buffer has to hold.
        Assert.Equal(46_500_000, BitrateCalculator.BytesFor(12_400_000, TimeSpan.FromSeconds(30)));
        Assert.Equal(0, BitrateCalculator.BytesFor(12_400_000, TimeSpan.Zero));
    }

    [Fact]
    public void ByteBudgetRejectsNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BitrateCalculator.BytesFor(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BitrateCalculator.BytesFor(1_000_000, TimeSpan.FromSeconds(-1)));
    }
}

public sealed class EncoderSettingsTests
{
    [Fact]
    public void DefaultsAreUsableForOneEightyPSixty()
    {
        var settings = EncoderSettings.For(VideoCodec.H264, 1920, 1080, 60);
        settings.Validate();

        Assert.Equal(12_400_000, settings.EffectiveBitsPerSecond);
        Assert.Equal(120, settings.KeyFrameIntervalFrames);
        Assert.Equal(RateControlMode.ConstantBitrate, settings.RateControl);
        Assert.True(settings.LowLatency);
    }

    [Fact]
    public void AnExplicitBitrateOverridesTheRecommendation()
    {
        var settings = EncoderSettings.For(VideoCodec.H264, 1920, 1080, 60) with { BitsPerSecond = 40_000_000 };
        settings.Validate();
        Assert.Equal(40_000_000, settings.EffectiveBitsPerSecond);
    }

    [Fact]
    public void OddFrameDimensionsAreRejectedBecauseNv12IsFourTwoZero()
    {
        Assert.Throws<ArgumentException>(
            () => (EncoderSettings.For(VideoCodec.H264, 1921, 1080, 60)).Validate());
        Assert.Throws<ArgumentException>(
            () => (EncoderSettings.For(VideoCodec.H264, 1920, 1081, 60)).Validate());
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(20000, 1080)]
    public void ImpossibleFrameSizesAreRejected(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (EncoderSettings.For(VideoCodec.H264, width, height, 60)).Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void ImpossibleFrameRatesAreRejected(int fps) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (EncoderSettings.For(VideoCodec.H264, 1920, 1080, fps)).Validate());

    [Theory]
    [InlineData(1_000)]
    [InlineData(500_000_000)]
    public void OutOfRangeBitratesAreRejected(long bitrate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (EncoderSettings.For(VideoCodec.H264, 1920, 1080, 60) with { BitsPerSecond = bitrate }).Validate());

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(31.0)]
    public void OutOfRangeKeyframeIntervalsAreRejected(double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (EncoderSettings.For(VideoCodec.H264, 1920, 1080, 60) with
            {
                KeyFrameIntervalSeconds = seconds,
            }).Validate());

    [Fact]
    public void KeyframeIntervalIsAtLeastOneFrameEvenWhenTiny()
    {
        var settings = EncoderSettings.For(VideoCodec.H264, 1920, 1080, 60) with
        {
            KeyFrameIntervalSeconds = 0.001,
        };

        Assert.Equal(1, settings.KeyFrameIntervalFrames);
    }
}

public sealed class EncodedSampleTests
{
    [Fact]
    public void DescribesAWindowIntoTheArena()
    {
        var sample = new EncodedSample(
            offset: 4096, length: 1200, timestampTicks: 10_000_000,
            durationTicks: 166_666, isKeyFrame: true, sequenceNumber: 7);

        Assert.Equal(4096, sample.Offset);
        Assert.Equal(1200, sample.Length);
        Assert.Equal(10_166_666, sample.EndTimestampTicks);
        Assert.True(sample.IsKeyFrame);
        Assert.False(sample.IsEmpty);
    }

    [Fact]
    public void DefaultSampleIsEmpty() => Assert.True(default(EncodedSample).IsEmpty);
}
