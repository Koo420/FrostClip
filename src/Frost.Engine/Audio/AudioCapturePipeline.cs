using Frost.Engine.Diagnostics;
using Frost.Engine.Recording;

namespace Frost.Engine.Audio;

/// <summary>
/// Routes captured audio to everything that wants it.
/// </summary>
/// <remarks>
/// <para>One capture, several consumers, exactly as the video side works: the
/// rolling buffer a clip is cut from, the session recording when one is running,
/// and the autoclip loudness detector. The capture thread writes once and each
/// consumer takes what it needs.</para>
///
/// <para>Audio is the one place where "it kept going" matters more than "it was
/// perfect". A device that disappears mid-session (headphones unplugged, default
/// device changed) must not stop the video, so every consumer here treats a
/// missing audio track as a silent recording rather than a failure.</para>
/// </remarks>
public sealed class AudioCapturePipeline : IAudioSink, IDisposable
{
    private readonly IAudioSource _source;
    private readonly AudioTrackBuffer _buffer;
    private readonly IEngineLog _log;
    private readonly AutoclipBookmarker? _bookmarker;
    private readonly float[] _detectorScratch;

    private ISessionWriter? _sessionWriter;
    private long _blocksRouted;
    private long _bytesRouted;
    private bool _disposed;

    /// <param name="source">The capture device.</param>
    /// <param name="clipWindow">
    /// Trailing duration to keep for clips. Should match the video ring's window
    /// including its headroom, so a clip never finds audio missing for video it
    /// has.
    /// </param>
    /// <param name="bookmarker">
    /// Autoclip detector, or null. When its feature is off it costs a field check.
    /// </param>
    public AudioCapturePipeline(
        IAudioSource source,
        TimeSpan clipWindow,
        IEngineLog log,
        AutoclipBookmarker? bookmarker = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(log);

        _source = source;
        _log = log;
        _bookmarker = bookmarker;
        _buffer = new AudioTrackBuffer(source.Format.AsInt16, clipWindow);

        // The detector wants floats; capture hands over int16. Sized for a
        // generous packet and reused, so conversion never allocates.
        _detectorScratch = new float[source.Format.SampleRate * source.Format.Channels / 4];
    }

    /// <summary>The rolling buffer a clip's audio is cut from.</summary>
    public AudioTrackBuffer Buffer => _buffer;

    public AudioFormat Format => _buffer.Format;

    public string DeviceName => _source.DeviceName;

    public bool IsRunning => _source.IsRunning;

    public long BlocksRouted => Interlocked.Read(ref _blocksRouted);

    public long BytesRouted => Interlocked.Read(ref _bytesRouted);

    /// <summary>Frames of silence the source inserted to keep the track continuous.</summary>
    public long SilenceFramesInserted => _source.SilenceFramesInserted;

    public void Start() => _source.Start(this);

    public void Stop() => _source.Stop();

    /// <summary>
    /// Attaches a session recording, or detaches with null.
    /// </summary>
    /// <remarks>
    /// A volatile swap rather than reconfiguring the capture, for the same reason
    /// the video side does it: the capture thread reads this field per block, and
    /// rewiring it while that happens would be a race.
    /// </remarks>
    public void SetSessionWriter(ISessionWriter? writer) => Volatile.Write(ref _sessionWriter, writer);

    /// <summary>Capture-thread entry point.</summary>
    public void Write(ReadOnlySpan<byte> data, long timestampTicks)
    {
        if (data.IsEmpty)
        {
            return;
        }

        _buffer.Write(data, timestampTicks);

        // A session recording that cannot take a block loses that block; it must
        // never stall the capture thread, because that stalls the audio endpoint
        // for every application on the machine.
        Volatile.Read(ref _sessionWriter)?.TryWriteAudio(data, timestampTicks);

        FeedDetector(data, timestampTicks);

        Interlocked.Increment(ref _blocksRouted);
        Interlocked.Add(ref _bytesRouted, data.Length);
    }

    /// <summary>Converts int16 back to float for the loudness detector.</summary>
    private void FeedDetector(ReadOnlySpan<byte> data, long timestampTicks)
    {
        if (_bookmarker is not { IsEnabled: true })
        {
            return;
        }

        var sampleCount = data.Length / 2;

        if (sampleCount <= 0 || sampleCount > _detectorScratch.Length)
        {
            return;
        }

        var samples = _detectorScratch.AsSpan(0, sampleCount);

        for (var i = 0; i < sampleCount; i++)
        {
            var value = (short)(data[i * 2] | (data[(i * 2) + 1] << 8));
            samples[i] = value / 32767f;
        }

        _bookmarker.ProcessAudio(samples, timestampTicks);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _source.Dispose();

        _log.Debug(
            $"Audio pipeline stopped: {BlocksRouted} block(s), " +
            $"{BytesRouted / (1024.0 * 1024.0):F1}MB routed, " +
            $"{SilenceFramesInserted} silence frame(s) inserted.");
    }
}
