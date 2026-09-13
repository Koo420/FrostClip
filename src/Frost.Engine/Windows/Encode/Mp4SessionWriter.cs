using Frost.Engine.Audio;
using Frost.Engine.Diagnostics;
using Frost.Engine.Recording;
using Vortice.MediaFoundation;

namespace Frost.Engine.Windows.Encode;

/// <summary>
/// Adapts <see cref="Mp4Muxer"/> to <see cref="ISessionWriter"/> for
/// full-session recording.
/// </summary>
/// <remarks>
/// A full session can run for hours, so the queue is sized larger than a clip's:
/// it has to absorb the occasional multi-hundred-millisecond stall when Windows
/// decides to flush, without the encode thread ever noticing. Refusing a sample
/// is still better than blocking — a dropped frame in a VOD is a blemish, a
/// stalled encode thread is a stutter in the game.
/// </remarks>
internal sealed class Mp4SessionWriter : ISessionWriter
{
    private readonly Mp4Muxer _muxer;

    internal Mp4SessionWriter(
        string path,
        IMFMediaType encodedType,
        IEngineLog log,
        IReadOnlyList<AudioFormat>? audioFormats = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(encodedType);
        ArgumentNullException.ThrowIfNull(log);

        _muxer = new Mp4Muxer(
            path,
            encodedType,
            log,
            queueBytes: 64L * 1024 * 1024,
            queueSamples: 4096,
            audioFormats: audioFormats);
    }

    public string Extension => ".mp4";

    public long BytesWritten => _muxer.BytesWritten;

    public bool IsFaulted => _muxer.IsFaulted;

    public int AudioTrackCount => _muxer.AudioTrackCount;

    public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame) =>
        _muxer.TryWrite(data, timestampTicks, durationTicks, isKeyFrame);

    public bool TryWriteAudio(int track, ReadOnlySpan<byte> data, long timestampTicks) =>
        _muxer.TryWriteAudio(track, data, timestampTicks);

    public void Finish(TimeSpan timeout) => _muxer.Finish(timeout);

    public void Dispose() => _muxer.Dispose();
}
