using Frost.Engine.Audio;
using Frost.Engine.Clips;
using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Engine.Hotkeys;
using Frost.Engine.Recording;
using Frost.Shared.Hotkeys;
using Frost.Shared.Settings;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class MicrophoneStateTests
{
    [Fact]
    public void StartsUnmutedAtUnityGainByDefault()
    {
        var state = new MicrophoneState();

        Assert.False(state.IsMuted);
        Assert.Equal(1.0, state.Gain);
        Assert.Equal(0, state.MuteChanges);
    }

    [Fact]
    public void CanStartMuted() => Assert.True(new MicrophoneState(mutedAtStart: true).IsMuted);

    [Fact]
    public void TogglingFlipsAndReportsTheNewState()
    {
        var state = new MicrophoneState();

        Assert.True(state.Toggle());
        Assert.True(state.IsMuted);

        Assert.False(state.Toggle());
        Assert.False(state.IsMuted);
        Assert.Equal(2, state.MuteChanges);
    }

    [Fact]
    public void SettingTheSameStateTwiceIsNotCountedAsAChange()
    {
        var state = new MicrophoneState();

        state.SetMuted(true);
        state.SetMuted(true);

        Assert.Equal(1, state.MuteChanges);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(5.0, 2.0)]
    public void GainIsClampedSoABadSettingCannotDeafenAnyone(double set, double expected)
    {
        var state = new MicrophoneState { Gain = set };
        Assert.Equal(expected, state.Gain);
    }

    [Fact]
    public void ConcurrentTogglesDoNotLoseAFlip()
    {
        // Two threads must not both read "unmuted" and both write "muted".
        var state = new MicrophoneState();
        const int perThread = 20_000;

        var a = Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                state.Toggle();
            }
        });

        var b = Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                state.Toggle();
            }
        });

        Task.WaitAll(a, b);

        // Every toggle is accounted for, and an even total returns to unmuted.
        Assert.Equal(perThread * 2, state.MuteChanges);
        Assert.False(state.IsMuted);
    }
}

public sealed class MicrophoneTrackTests
{
    private static readonly AudioFormat Pcm = AudioFormat.Default.AsInt16;

    private static byte[] Packet(short value = 8000)
    {
        var frames = Pcm.SampleRate / 100;
        var bytes = new byte[frames * Pcm.BytesPerFrame];

        for (var i = 0; i < bytes.Length; i += 2)
        {
            bytes[i] = (byte)(value & 0xFF);
            bytes[i + 1] = (byte)((value >> 8) & 0xFF);
        }

        return bytes;
    }

    private static long PacketTicks => Pcm.FramesToTicks(Pcm.SampleRate / 100);

    [Fact]
    public void SystemAudioAndMicrophoneWriteToSeparateTracks()
    {
        // Separate tracks, not a mix, so the mic can be muted, re-levelled or
        // dropped in an editor afterwards without touching the game audio.
        var systemSource = new FakeAudioSource();
        var micSource = new FakeAudioSource();

        using var system = new AudioCapturePipeline(
            systemSource, TimeSpan.FromSeconds(10), NullEngineLog.Instance, trackIndex: 0);
        using var mic = new AudioCapturePipeline(
            micSource, TimeSpan.FromSeconds(10), NullEngineLog.Instance, trackIndex: 1);

        var writer = new MultiTrackSessionWriter(tracks: 2);
        system.SetSessionWriter(writer);
        mic.SetSessionWriter(writer);

        system.Start();
        mic.Start();

        for (var i = 0; i < 50; i++)
        {
            systemSource.Deliver(Packet(10_000), i * PacketTicks);
            micSource.Deliver(Packet(2_000), i * PacketTicks);
        }

        Assert.Equal(50, writer.Tracks[0].Count);
        Assert.Equal(50, writer.Tracks[1].Count);

        // And each track got its own audio, not the other's.
        Assert.Equal(10_000, FirstSampleOf(writer.Tracks[0][0]));
        Assert.Equal(2_000, FirstSampleOf(writer.Tracks[1][0]));
    }

    [Fact]
    public void EachTrackHasItsOwnClipBuffer()
    {
        var systemSource = new FakeAudioSource();
        var micSource = new FakeAudioSource();

        using var system = new AudioCapturePipeline(
            systemSource, TimeSpan.FromSeconds(10), NullEngineLog.Instance, trackIndex: 0);
        using var mic = new AudioCapturePipeline(
            micSource, TimeSpan.FromSeconds(10), NullEngineLog.Instance, trackIndex: 1);

        system.Start();
        mic.Start();

        for (var i = 0; i < 100; i++)
        {
            systemSource.Deliver(Packet(10_000), i * PacketTicks);
        }

        for (var i = 0; i < 40; i++)
        {
            micSource.Deliver(Packet(2_000), i * PacketTicks);
        }

        Assert.Equal(100, system.Buffer.BlocksWritten);
        Assert.Equal(40, mic.Buffer.BlocksWritten);
        Assert.NotSame(system.Buffer, mic.Buffer);
    }

    [Fact]
    public void OnlyValidTrackIndexesAreAccepted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioCapturePipeline(
            new FakeAudioSource(), TimeSpan.FromSeconds(10), NullEngineLog.Instance, trackIndex: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioCapturePipeline(
            new FakeAudioSource(), TimeSpan.FromSeconds(10), NullEngineLog.Instance, trackIndex: 2));
    }

    [Fact]
    public void TheMuteHotkeyTogglesTheLiveState()
    {
        var microphone = new MicrophoneState();

        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(10),
            BitsPerSecond = 12_400_000,
            Fps = 60,
        });

        var directory = Path.Combine(
            Path.GetTempPath(), "frost-mic-tests", Guid.NewGuid().ToString("N"));

        using var clips = new ClipService(
            ring, new FakeClipWriter { CaptureBytes = false }, directory, NullEngineLog.Instance, _ => false);
        using var session = new FullSessionRecorder(_ => new NoOpWriter(), NullEngineLog.Instance);

        var actions = new EngineHotkeyActions(
            clips,
            session,
            () => Path.Combine(directory, "s.mp4"),
            () => null,
            () => (1920, 1080, "H264"),
            NullEngineLog.Instance,
            microphone);

        var router = new HotkeyRouter(
        [
            new HotkeyAssignment
            {
                Action = HotkeyAction.ToggleMicrophoneMute,
                Binding = new HotkeyBinding(VirtualKeys.F2, HotkeyModifiers.Alt),
            },
        ]);

        using var dispatcher = new HotkeyActionDispatcher(router, actions, NullEngineLog.Instance);

        router.OnKeyDown(VirtualKeys.F2, HotkeyModifiers.Alt);
        router.OnKeyUp(VirtualKeys.F2);
        Assert.True(dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.True(microphone.IsMuted);

        router.OnKeyDown(VirtualKeys.F2, HotkeyModifiers.Alt);
        router.OnKeyUp(VirtualKeys.F2);
        Assert.True(dispatcher.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.False(microphone.IsMuted);
    }

    [Fact]
    public void TheMuteHotkeyIsHarmlessWhenTheMicrophoneIsOff()
    {
        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(10),
            BitsPerSecond = 12_400_000,
            Fps = 60,
        });

        var directory = Path.Combine(
            Path.GetTempPath(), "frost-mic-tests", Guid.NewGuid().ToString("N"));

        using var clips = new ClipService(
            ring, new FakeClipWriter { CaptureBytes = false }, directory, NullEngineLog.Instance, _ => false);
        using var session = new FullSessionRecorder(_ => new NoOpWriter(), NullEngineLog.Instance);

        // No MicrophoneState: capture is off.
        var actions = new EngineHotkeyActions(
            clips, session, () => Path.Combine(directory, "s.mp4"), () => null,
            () => (1920, 1080, "H264"), NullEngineLog.Instance);

        actions.ToggleMicrophoneMute();
    }

    [Fact]
    public void MutingSilencesTheTrackWithoutShorteningIt()
    {
        // A gap would shift every sample after it and desync the track, so a muted
        // microphone produces silence of exactly the same length.
        var samples = new float[480];
        Array.Fill(samples, 0.4f);

        AudioConversion.Silence(samples);

        Assert.Equal(480, samples.Length);
        Assert.All(samples, s => Assert.Equal(0f, s));
    }

    private static short FirstSampleOf(byte[] block) => (short)(block[0] | (block[1] << 8));

    private sealed class MultiTrackSessionWriter(int tracks) : ISessionWriter
    {
        internal List<List<byte[]>> Tracks { get; } =
            Enumerable.Range(0, tracks).Select(_ => new List<byte[]>()).ToList();

        public string Extension => ".mp4";

        public long BytesWritten { get; private set; }

        public bool IsFaulted => false;

        public int AudioTrackCount => Tracks.Count;

        public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame) =>
            true;

        public bool TryWriteAudio(int track, ReadOnlySpan<byte> data, long timestampTicks)
        {
            if ((uint)track >= (uint)Tracks.Count)
            {
                return false;
            }

            lock (Tracks)
            {
                Tracks[track].Add(data.ToArray());
            }

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

    private sealed class NoOpWriter : ISessionWriter
    {
        public string Extension => ".mp4";

        public long BytesWritten => 0;

        public bool IsFaulted => false;

        public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame) =>
            true;

        public void Finish(TimeSpan timeout)
        {
        }

        public void Dispose()
        {
        }
    }
}
