using Frost.Engine.Audio;
using Frost.Engine.Diagnostics;
using Frost.Engine.Recording;
using Frost.Shared.Clips;
using Frost.Shared.Settings;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>A capture device the test drives by hand.</summary>
internal sealed class FakeAudioSource : IAudioSource
{
    private IAudioSink? _sink;

    internal FakeAudioSource(AudioFormat? format = null) =>
        Format = (format ?? AudioFormat.Default).AsInt16;

    public AudioFormat Format { get; }

    public string DeviceName => "Fake Endpoint";

    public bool IsRunning { get; private set; }

    public long FramesCaptured { get; private set; }

    public long SilenceFramesInserted { get; set; }

    public long GlitchCount => 0;

    internal bool Disposed { get; private set; }

    public void Start(IAudioSink sink)
    {
        _sink = sink;
        IsRunning = true;
    }

    public void Stop() => IsRunning = false;

    /// <summary>Delivers a block as the device would.</summary>
    internal void Deliver(ReadOnlySpan<byte> data, long timestampTicks)
    {
        FramesCaptured += data.Length / Format.BytesPerFrame;
        _sink?.Write(data, timestampTicks);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Session writer that records audio as well as video.</summary>
internal sealed class AudioAwareSessionWriter : ISessionWriter
{
    internal List<(byte[] Data, long Timestamp)> Audio { get; } = [];

    internal int VideoSamples { get; private set; }

    internal bool RefuseAudio { get; set; }

    public string Extension => ".mp4";

    public long BytesWritten { get; private set; }

    public bool IsFaulted => false;

    public bool HasAudio => true;

    public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
    {
        VideoSamples++;
        BytesWritten += data.Length;
        return true;
    }

    public bool TryWriteAudio(ReadOnlySpan<byte> data, long timestampTicks)
    {
        if (RefuseAudio)
        {
            return false;
        }

        Audio.Add((data.ToArray(), timestampTicks));
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

public sealed class AudioCapturePipelineTests
{
    private static readonly AudioFormat Pcm = AudioFormat.Default.AsInt16;

    /// <summary>A 10ms packet of int16 stereo at the given amplitude.</summary>
    private static byte[] Packet(double amplitude = 0.25)
    {
        var frames = Pcm.SampleRate / 100;
        var bytes = new byte[frames * Pcm.BytesPerFrame];
        var value = (short)(amplitude * 32767);

        for (var i = 0; i < bytes.Length; i += 2)
        {
            bytes[i] = (byte)(value & 0xFF);
            bytes[i + 1] = (byte)((value >> 8) & 0xFF);
        }

        return bytes;
    }

    private static long PacketTicks => Pcm.FramesToTicks(Pcm.SampleRate / 100);

    [Fact]
    public void CapturedAudioReachesTheClipBuffer()
    {
        var source = new FakeAudioSource();
        using var pipeline = new AudioCapturePipeline(
            source, TimeSpan.FromSeconds(10), NullEngineLog.Instance);

        pipeline.Start();
        Assert.True(pipeline.IsRunning);

        var packet = Packet();
        for (var i = 0; i < 500; i++)
        {
            source.Deliver(packet, i * PacketTicks);
        }

        Assert.Equal(500, pipeline.BlocksRouted);
        Assert.InRange(pipeline.Buffer.HeldDuration, TimeSpan.FromSeconds(4.9), TimeSpan.FromSeconds(5.1));
    }

    [Fact]
    public void ASessionWriterAttachedMidCaptureStartsReceivingAudio()
    {
        var source = new FakeAudioSource();
        using var pipeline = new AudioCapturePipeline(
            source, TimeSpan.FromSeconds(10), NullEngineLog.Instance);

        pipeline.Start();
        var packet = Packet();

        for (var i = 0; i < 100; i++)
        {
            source.Deliver(packet, i * PacketTicks);
        }

        var writer = new AudioAwareSessionWriter();
        pipeline.SetSessionWriter(writer);

        for (var i = 100; i < 200; i++)
        {
            source.Deliver(packet, i * PacketTicks);
        }

        Assert.Equal(100, writer.Audio.Count);
        Assert.Equal(100 * PacketTicks, writer.Audio[0].Timestamp);

        // And the clip buffer got everything, before and after.
        Assert.Equal(200, pipeline.BlocksRouted);
    }

    [Fact]
    public void DetachingTheSessionWriterStopsAudioReachingIt()
    {
        var source = new FakeAudioSource();
        using var pipeline = new AudioCapturePipeline(
            source, TimeSpan.FromSeconds(10), NullEngineLog.Instance);

        pipeline.Start();
        var writer = new AudioAwareSessionWriter();
        pipeline.SetSessionWriter(writer);

        var packet = Packet();
        source.Deliver(packet, 0);
        pipeline.SetSessionWriter(null);
        source.Deliver(packet, PacketTicks);

        Assert.Single(writer.Audio);
        Assert.Equal(2, pipeline.BlocksRouted);
    }

    [Fact]
    public void ASessionWriterRefusingAudioDoesNotCostTheClipBuffer()
    {
        // Losing audio from the VOD because the disk stalled must not lose it from
        // the clip buffer as well.
        var source = new FakeAudioSource();
        using var pipeline = new AudioCapturePipeline(
            source, TimeSpan.FromSeconds(10), NullEngineLog.Instance);

        pipeline.Start();
        pipeline.SetSessionWriter(new AudioAwareSessionWriter { RefuseAudio = true });

        var packet = Packet();
        for (var i = 0; i < 100; i++)
        {
            source.Deliver(packet, i * PacketTicks);
        }

        Assert.Equal(100, pipeline.BlocksRouted);
        Assert.Equal(100, pipeline.Buffer.BlocksWritten);
    }

    [Fact]
    public void AudioFeedsTheLoudnessDetectorWhenItIsEnabled()
    {
        var source = new FakeAudioSource();
        var target = new CountingBookmarkTarget();
        var bookmarker = new AutoclipBookmarker(
            new AutoclipSettings { DetectLoudnessSpikes = true },
            Pcm.SampleRate, Pcm.Channels, target, NullEngineLog.Instance);

        using var pipeline = new AudioCapturePipeline(
            source, TimeSpan.FromSeconds(10), NullEngineLog.Instance, bookmarker);

        pipeline.Start();

        var quiet = Packet(amplitude: 0.03);
        var loud = Packet(amplitude: 0.4);
        var clock = 0L;

        for (var i = 0; i < 1200; i++)
        {
            source.Deliver(quiet, clock);
            clock += PacketTicks;
        }

        for (var i = 0; i < 100; i++)
        {
            source.Deliver(loud, clock);
            clock += PacketTicks;
        }

        Assert.True(bookmarker.MarksPlaced + bookmarker.MarksDeclined > 0,
            "the detector never saw a spike through the pipeline");
    }

    [Fact]
    public void TheDetectorIsNotFedWhenTheFeatureIsOff()
    {
        var source = new FakeAudioSource();
        var target = new CountingBookmarkTarget();
        var bookmarker = new AutoclipBookmarker(
            new AutoclipSettings(), Pcm.SampleRate, Pcm.Channels, target, NullEngineLog.Instance);

        using var pipeline = new AudioCapturePipeline(
            source, TimeSpan.FromSeconds(10), NullEngineLog.Instance, bookmarker);

        pipeline.Start();

        var loud = Packet(amplitude: 0.9);
        for (var i = 0; i < 2000; i++)
        {
            source.Deliver(loud, i * PacketTicks);
        }

        Assert.Equal(0, bookmarker.MarksPlaced);
        Assert.Equal(0, bookmarker.MarksDeclined);
    }

    [Fact]
    public void RoutingDoesNotAllocate()
    {
        // Runs on the audio capture thread, and WASAPI expects its buffer back
        // promptly - holding it stalls the endpoint for every app on the machine.
        var source = new FakeAudioSource();
        var target = new CountingBookmarkTarget();
        var bookmarker = new AutoclipBookmarker(
            new AutoclipSettings { DetectLoudnessSpikes = true },
            Pcm.SampleRate, Pcm.Channels, target, NullEngineLog.Instance);

        using var pipeline = new AudioCapturePipeline(
            source, TimeSpan.FromSeconds(5), NullEngineLog.Instance, bookmarker);

        pipeline.Start();
        pipeline.SetSessionWriter(new DiscardingAudioWriter());

        var packet = Packet();

        AllocationAssert.NoPerIterationAllocation(
            i => pipeline.Write(packet, i * PacketTicks),
            iterations: 20_000,
            warmUpIterations: 2_000);
    }

    [Fact]
    public void EmptyBlocksAreIgnored()
    {
        var source = new FakeAudioSource();
        using var pipeline = new AudioCapturePipeline(
            source, TimeSpan.FromSeconds(10), NullEngineLog.Instance);

        pipeline.Start();
        pipeline.Write([], 0);

        Assert.Equal(0, pipeline.BlocksRouted);
    }

    [Fact]
    public void DisposeStopsAndDisposesTheSource()
    {
        var source = new FakeAudioSource();
        var pipeline = new AudioCapturePipeline(
            source, TimeSpan.FromSeconds(10), NullEngineLog.Instance);

        pipeline.Start();
        pipeline.Dispose();

        Assert.False(source.IsRunning);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void NullDependenciesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new AudioCapturePipeline(
            null!, TimeSpan.FromSeconds(10), NullEngineLog.Instance));
        Assert.Throws<ArgumentNullException>(() => new AudioCapturePipeline(
            new FakeAudioSource(), TimeSpan.FromSeconds(10), null!));
    }

    private sealed class CountingBookmarkTarget : IBookmarkTarget
    {
        public bool TryAddBookmark(BookmarkSource source, string? label, out string? error)
        {
            error = null;
            return true;
        }
    }

    private sealed class DiscardingAudioWriter : ISessionWriter
    {
        public string Extension => ".mp4";

        public long BytesWritten => 0;

        public bool IsFaulted => false;

        public bool HasAudio => true;

        public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame) =>
            true;

        public bool TryWriteAudio(ReadOnlySpan<byte> data, long timestampTicks) => true;

        public void Finish(TimeSpan timeout)
        {
        }

        public void Dispose()
        {
        }
    }
}
