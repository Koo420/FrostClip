namespace Frost.Engine.Audio;

/// <summary>
/// Where captured audio goes.
/// </summary>
/// <remarks>
/// Called on the audio capture thread. Implementations must not allocate or block
/// on disk: WASAPI hands over a pointer into its own buffer and expects it back
/// promptly, and holding a WASAPI buffer stalls the audio endpoint for every
/// application on the machine, not just Frost.
/// </remarks>
public interface IAudioSink
{
    /// <summary>
    /// Accepts a block of PCM in the source's format.
    /// </summary>
    /// <param name="data">PCM bytes. Only valid for the duration of the call.</param>
    /// <param name="timestampTicks">Capture time of the first frame, on the capture clock.</param>
    void Write(ReadOnlySpan<byte> data, long timestampTicks);
}

/// <summary>A running audio capture.</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Format being produced.</summary>
    AudioFormat Format { get; }

    /// <summary>Human-readable device name, for the settings UI and logs.</summary>
    string DeviceName { get; }

    bool IsRunning { get; }

    /// <summary>Frames handed to the sink, including inserted silence.</summary>
    long FramesCaptured { get; }

    /// <summary>Frames of silence inserted to keep the track continuous.</summary>
    long SilenceFramesInserted { get; }

    /// <summary>Frames WASAPI reported as lost before a packet.</summary>
    long GlitchCount { get; }

    void Start(IAudioSink sink);

    void Stop();
}
