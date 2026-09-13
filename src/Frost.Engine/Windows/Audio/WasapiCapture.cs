using System.Diagnostics;
using System.Runtime.InteropServices;
using Frost.Engine.Audio;
using Frost.Engine.Diagnostics;
using Frost.Engine.Windows.Interop;
using Vortice.MediaFoundation;

namespace Frost.Engine.Windows.Audio;

/// <summary>What to capture.</summary>
internal enum WasapiCaptureMode
{
    /// <summary>What the machine is playing: game and system audio.</summary>
    Loopback = 0,

    /// <summary>A capture endpoint: the microphone.</summary>
    Microphone = 1,
}

/// <summary>
/// Captures audio from a WASAPI endpoint on its own thread.
/// </summary>
/// <remarks>
/// <para><b>Shared mode, never exclusive.</b> Exclusive mode would take the device
/// away from the game, which for a recording tool is an absurd trade.</para>
///
/// <para><b>Polling, not the event callback.</b> WASAPI's event callback is not
/// raised for a loopback stream while nothing is playing, so an event-driven
/// loopback capture simply stops until audio resumes — and with it any chance of
/// noticing the gap. Polling at half the device period sees the silence and lets
/// <see cref="AudioTimeline"/> fill it, which is what keeps a recording in sync
/// through a quiet stretch.</para>
///
/// <para><b>No resampling.</b> Whatever the endpoint's mix format is, that is what
/// is captured and muxed. Float32 is converted to int16 because the AAC encoder
/// wants it and that is a multiply and a clamp, but the sample rate and channel
/// count are left alone.</para>
///
/// <para>The capture thread returns WASAPI's buffer immediately after copying out
/// of it: holding it stalls the audio endpoint for every application on the
/// machine.</para>
/// </remarks>
internal sealed class WasapiCapture : IAudioSource
{
    private readonly WasapiCaptureMode _mode;
    private readonly string? _deviceId;
    private readonly IEngineLog _log;
    private readonly double _gain;
    private readonly Func<bool> _isMuted;

    private IMMDevice? _device;
    private AudioClient? _client;
    private AudioCaptureClient? _captureClient;
    private AudioTimeline? _timeline;
    private Thread? _thread;
    private IAudioSink? _sink;

    // Pre-allocated: the capture loop converts into these and never grows them.
    private byte[] _pcmBuffer = [];
    private float[] _floatBuffer = [];
    private byte[] _silenceBuffer = [];

    private AudioFormat _sourceFormat = AudioFormat.Default;
    private long _pollIntervalTicks;
    private volatile bool _stopRequested;
    private long _framesCaptured;
    private long _glitches;
    private bool _disposed;

    /// <param name="mode">Loopback or microphone.</param>
    /// <param name="deviceId">Endpoint ID, or null for the default device.</param>
    /// <param name="gain">Linear gain applied to captured samples.</param>
    /// <param name="isMuted">Polled per block, so muting takes effect immediately.</param>
    internal WasapiCapture(
        WasapiCaptureMode mode,
        string? deviceId,
        IEngineLog log,
        double gain = 1.0,
        Func<bool>? isMuted = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        _mode = mode;
        _deviceId = deviceId;
        _log = log;
        _gain = gain;
        _isMuted = isMuted ?? (static () => false);
    }

    /// <summary>Format handed to the sink: the endpoint's rate and channels, as int16.</summary>
    public AudioFormat Format { get; private set; } = AudioFormat.Default.AsInt16;

    public string DeviceName { get; private set; } = "unknown";

    public bool IsRunning => _thread is { IsAlive: true } && !_stopRequested;

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);

    public long SilenceFramesInserted => _timeline?.SilenceFramesInserted ?? 0;

    public long GlitchCount => Interlocked.Read(ref _glitches);

    /// <summary>Frames trimmed because a packet overlapped what was already written.</summary>
    internal long FramesTrimmed => _timeline?.FramesTrimmed ?? 0;

    public void Start(IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_thread is not null)
        {
            throw new InvalidOperationException("Audio capture is already started.");
        }

        _sink = sink;
        Open();

        _stopRequested = false;
        _thread = new Thread(CaptureLoop)
        {
            Name = _mode == WasapiCaptureMode.Loopback ? "frost-audio-loopback" : "frost-audio-mic",
            IsBackground = true,

            // Above normal like capture and encode: a late audio block is a gap in
            // the recording. Still below the game's render thread.
            Priority = ThreadPriority.AboveNormal,
        };

        _client!.Start();
        _thread.Start();

        _log.Info(
            $"{_mode} audio capture started on '{DeviceName}': {_sourceFormat} " +
            $"-> {Format}, polling every {_pollIntervalTicks / (double)TimeSpan.TicksPerMillisecond:F1}ms.");
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopRequested = true;

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _log.Warn($"The {_mode} audio capture thread did not stop within 5s.");
        }

        _thread = null;

        _client?.Stop();
        _captureClient?.Dispose();
        _captureClient = null;
        _client?.Dispose();
        _client = null;
        _device?.Dispose();
        _device = null;

        _log.Info(
            $"{_mode} audio capture stopped: {FramesCaptured} frames, " +
            $"{SilenceFramesInserted} inserted as silence, {GlitchCount} glitch(es).");
    }

    private void Open()
    {
        using var enumerator = new IMMDeviceEnumerator();

        // Loopback captures from a *render* endpoint; the microphone is a capture
        // endpoint. Console role: the device the user's games and apps use.
        var dataFlow = _mode == WasapiCaptureMode.Loopback ? DataFlow.Render : DataFlow.Capture;

        _device = _deviceId is { Length: > 0 }
            ? enumerator.GetDevice(_deviceId)
            : enumerator.GetDefaultAudioEndpoint(dataFlow, Role.Console);

        if (_device is null)
        {
            throw new InvalidOperationException(
                _mode == WasapiCaptureMode.Loopback
                    ? "No audio playback device was found, so game audio cannot be recorded."
                    : "No microphone was found.");
        }

        DeviceName = SafeDeviceName(_device);

        var hr = _device.Activate(WasapiInterop.IAudioClientIid, 0, null, out var clientPointer);
        hr.CheckError();

        _client = new AudioClient(clientPointer);

        var mixFormat = _client.GetMixFormat();
        try
        {
            _sourceFormat = ReadFormat(mixFormat);
            _sourceFormat.Validate();
            Format = _sourceFormat.AsInt16;

            var (defaultPeriod, _) = _client.GetDevicePeriod();

            // A buffer of four device periods: enough that a scheduling hiccup does
            // not lose audio, small enough that a clip's most recent audio is
            // genuinely recent.
            var bufferDuration = Math.Max(defaultPeriod * 4, TimeSpan.TicksPerMillisecond * 40);

            var flags = _mode == WasapiCaptureMode.Loopback ? WasapiInterop.StreamFlagsLoopback : 0u;
            _client.Initialize(WasapiInterop.ShareModeShared, flags, bufferDuration, mixFormat);

            // Half the device period: often enough to never let the endpoint's
            // buffer overrun, and cheap (a GetNextPacketSize call) when idle.
            _pollIntervalTicks = Math.Max(defaultPeriod / 2, TimeSpan.TicksPerMillisecond);
        }
        finally
        {
            Marshal.FreeCoTaskMem(mixFormat);
        }

        _captureClient = new AudioCaptureClient(_client.GetCaptureClient());
        _timeline = new AudioTimeline(Format);

        var bufferFrames = (int)_client.GetBufferSize();

        // Sized for the whole endpoint buffer, so even the largest packet WASAPI
        // can hand over is copied without growing anything.
        _floatBuffer = new float[bufferFrames * _sourceFormat.Channels * 2];
        _pcmBuffer = new byte[bufferFrames * Format.BytesPerFrame * 2];

        // One device period of silence, reused to fill gaps of any length.
        _silenceBuffer = new byte[Math.Max(Format.BytesPerFrame, bufferFrames * Format.BytesPerFrame)];
    }

    private void CaptureLoop()
    {
        var capture = _captureClient!;
        var sink = _sink!;
        using var timer = WaitableTimer.Create();

        try
        {
            while (!_stopRequested)
            {
                var drained = false;

                while (!_stopRequested && capture.GetNextPacketSize() > 0)
                {
                    if (!capture.TryGetBuffer(out var packet))
                    {
                        break;
                    }

                    try
                    {
                        HandlePacket(packet, sink);
                    }
                    finally
                    {
                        // Released immediately: holding a WASAPI buffer stalls the
                        // endpoint for every application on the machine.
                        capture.ReleaseBuffer(packet.FrameCount);
                    }

                    drained = true;
                }

                if (!drained)
                {
                    // Nothing playing. The gap gets filled when audio returns; see
                    // AudioTimeline for why that matters.
                    timer.Wait(_pollIntervalTicks);
                }
                else
                {
                    timer.Wait(_pollIntervalTicks / 2);
                }
            }
        }
        catch (Exception ex) when (IsDeviceInvalidated(ex))
        {
            // The endpoint went away: the user unplugged headphones, or switched
            // default device. Not an error worth failing a recording over — the
            // video keeps going and the audio track ends.
            _log.Warn($"The {_mode} audio device went away; audio capture has stopped.", ex);
            _stopRequested = true;
        }
        catch (Exception ex)
        {
            _log.Error($"The {_mode} audio capture thread faulted.", ex);
            _stopRequested = true;
        }
    }

    private void HandlePacket(in AudioPacket packet, IAudioSink sink)
    {
        var frames = (int)packet.FrameCount;

        if (frames <= 0)
        {
            return;
        }

        if (packet.IsDiscontinuous)
        {
            Interlocked.Increment(ref _glitches);
        }

        // WASAPI's QPC position is in 100ns units on the same clock as capture's
        // timestamps, so no conversion is needed. When it is flagged unreliable,
        // fall back to our own clock rather than writing a bad timestamp.
        var timestamp = packet.HasTimestampError
            ? MonotonicTicks()
            : (long)packet.QpcPosition;

        var placement = _timeline!.Place(timestamp, frames);

        if (placement.NeedsSilence)
        {
            WriteSilence(placement.SilenceFrames, placement.TimestampTicks, placement.SilenceFrames, sink);
        }

        if (placement.IsFullyTrimmed)
        {
            return;
        }

        var keptFrames = placement.FrameCount;

        if (packet.IsSilent)
        {
            // The buffer contents are undefined for a silent packet, so it must not
            // be copied. Write real silence instead.
            WriteSilence(keptFrames, placement.TimestampTicks, 0, sink);
            Interlocked.Add(ref _framesCaptured, keptFrames);
            return;
        }

        var written = ConvertPacket(packet, placement.SkipFrames, keptFrames);

        if (written <= 0)
        {
            return;
        }

        sink.Write(new ReadOnlySpan<byte>(_pcmBuffer, 0, written), placement.TimestampTicks);
        Interlocked.Add(ref _framesCaptured, keptFrames);
    }

    /// <summary>
    /// Converts the kept part of a packet into <see cref="_pcmBuffer"/>, applying
    /// gain and mute.
    /// </summary>
    private unsafe int ConvertPacket(in AudioPacket packet, int skipFrames, int keptFrames)
    {
        var channels = _sourceFormat.Channels;
        var sampleCount = keptFrames * channels;

        if (sampleCount <= 0 || sampleCount > _floatBuffer.Length)
        {
            return 0;
        }

        var source = (byte*)packet.Data + (skipFrames * _sourceFormat.BytesPerFrame);
        var floats = _floatBuffer.AsSpan(0, sampleCount);

        if (_sourceFormat.SampleType == AudioSampleType.Float32)
        {
            new ReadOnlySpan<float>(source, sampleCount).CopyTo(floats);
        }
        else
        {
            var shorts = new ReadOnlySpan<short>(source, sampleCount);

            for (var i = 0; i < sampleCount; i++)
            {
                floats[i] = shorts[i] / 32768f;
            }
        }

        if (_isMuted())
        {
            // Silence rather than skipping: a gap would shift the rest of the track.
            AudioConversion.Silence(floats);
        }
        else
        {
            AudioConversion.ApplyGain(floats, _gain);
        }

        return AudioConversion.FloatToInt16Bytes(floats, _pcmBuffer);
    }

    /// <summary>
    /// Writes <paramref name="frames"/> of silence, in chunks of the reusable
    /// silence buffer.
    /// </summary>
    private void WriteSilence(long frames, long endTimestampTicks, long silenceFrames, IAudioSink sink)
    {
        if (frames <= 0)
        {
            return;
        }

        var framesPerChunk = _silenceBuffer.Length / Format.BytesPerFrame;

        if (framesPerChunk <= 0)
        {
            return;
        }

        // endTimestampTicks is where the *following* audio goes when filling a gap,
        // so silence is written backwards from there.
        var startTicks = silenceFrames > 0
            ? endTimestampTicks - Format.FramesToTicks(silenceFrames)
            : endTimestampTicks;

        var remaining = frames;
        var offsetFrames = 0L;

        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, framesPerChunk);

            sink.Write(
                new ReadOnlySpan<byte>(_silenceBuffer, 0, chunk * Format.BytesPerFrame),
                startTicks + Format.FramesToTicks(offsetFrames));

            offsetFrames += chunk;
            remaining -= chunk;
        }

        Interlocked.Add(ref _framesCaptured, frames);
    }

    private static bool IsDeviceInvalidated(Exception ex) =>
        ex is COMException com && com.HResult == WasapiInterop.DeviceInvalidated;

    private static string SafeDeviceName(IMMDevice device)
    {
        try
        {
            var name = device.FriendlyName;
            return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException)
        {
            // A device whose property store cannot be opened still captures fine.
            return "unknown";
        }
    }

    /// <summary>Reads a <c>WAVEFORMATEX</c>/<c>WAVEFORMATEXTENSIBLE</c> into our own model.</summary>
    private static AudioFormat ReadFormat(nint pointer)
    {
        var format = Marshal.PtrToStructure<WaveFormatEx>(pointer);

        var sampleType = format.FormatTag switch
        {
            WasapiInterop.WaveFormatIeeeFloat => AudioSampleType.Float32,
            WasapiInterop.WaveFormatPcm => AudioSampleType.Int16,
            WasapiInterop.WaveFormatExtensible => ReadExtensibleSampleType(pointer, format),
            _ => throw new NotSupportedException(
                $"The audio endpoint reports format tag {format.FormatTag}, which Frost cannot read."),
        };

        if (sampleType == AudioSampleType.Int16 && format.BitsPerSample != 16)
        {
            // 24- and 32-bit integer endpoints exist. Rather than silently
            // misinterpreting the samples, say so.
            throw new NotSupportedException(
                $"The audio endpoint uses {format.BitsPerSample}-bit integer samples, " +
                "which Frost does not convert. Set the device to 16-bit or 32-bit float in " +
                "Windows' sound settings.");
        }

        return new AudioFormat
        {
            SampleRate = (int)format.SamplesPerSecond,
            Channels = format.Channels,
            SampleType = sampleType,
        };
    }

    private static AudioSampleType ReadExtensibleSampleType(nint pointer, WaveFormatEx format)
    {
        if (format.ExtraSize < 22)
        {
            throw new NotSupportedException(
                "The audio endpoint reports WAVE_FORMAT_EXTENSIBLE without a sub-format.");
        }

        var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(pointer);

        if (extensible.SubFormat == WaveFormatExtensible.SubTypeIeeeFloat)
        {
            return AudioSampleType.Float32;
        }

        if (extensible.SubFormat == WaveFormatExtensible.SubTypePcm)
        {
            return AudioSampleType.Int16;
        }

        throw new NotSupportedException(
            $"The audio endpoint uses sub-format {extensible.SubFormat}, which Frost cannot read.");
    }

    private static long MonotonicTicks()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var frequency = Stopwatch.Frequency;

        if (frequency == TimeSpan.TicksPerSecond)
        {
            return timestamp;
        }

        var seconds = timestamp / frequency;
        var remainder = timestamp % frequency;
        return (seconds * TimeSpan.TicksPerSecond) + (remainder * TimeSpan.TicksPerSecond / frequency);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
