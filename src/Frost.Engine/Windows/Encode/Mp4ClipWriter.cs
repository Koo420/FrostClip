using Frost.Engine.Clips;
using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Shared.Clips;
using Vortice.MediaFoundation;

namespace Frost.Engine.Windows.Encode;

/// <summary>
/// Writes a ring-buffer snapshot to an MP4 by muxing the already-encoded frames.
/// </summary>
/// <remarks>
/// No encoding happens here — the frames were encoded once, as they were
/// captured, and this only packages them. That is why saving a 30-second clip
/// costs about as much as copying 45MB, and why it does not touch the GPU's
/// encoder at all while the game is still using it.
/// </remarks>
internal sealed class Mp4ClipWriter : IClipWriter
{
    private readonly Func<IMFMediaType> _mediaTypeFactory;
    private readonly EncoderSettings _settings;
    private readonly IEngineLog _log;
    private readonly ClipKind _kind;

    /// <param name="mediaTypeFactory">
    /// Supplies the encoder's current output media type, which carries the codec
    /// private data (H.264's SPS/PPS). Called per clip because the encoder can be
    /// restarted between clips.
    /// </param>
    internal Mp4ClipWriter(
        Func<IMFMediaType> mediaTypeFactory,
        EncoderSettings settings,
        IEngineLog log,
        ClipKind kind = ClipKind.InstantClip)
    {
        ArgumentNullException.ThrowIfNull(mediaTypeFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _mediaTypeFactory = mediaTypeFactory;
        _settings = settings;
        _log = log;
        _kind = kind;
    }

    public string Extension => ".mp4";

    public ClipMetadata Write(RingSnapshot snapshot, string path, ClipRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var mediaType = _mediaTypeFactory();

        // Queue sized to the clip: the muxer thread drains while we feed it, so
        // this only has to absorb a disk stall, not the whole clip.
        using var muxer = new Mp4Muxer(
            path,
            mediaType,
            _log,
            queueBytes: Math.Clamp(snapshot.TotalBytes / 4, 4L * 1024 * 1024, 64L * 1024 * 1024),
            queueSamples: Math.Clamp(snapshot.Count, 64, 4096));

        var scratch = new byte[Math.Max(1, snapshot.LargestSampleLength)];

        for (var i = 0; i < snapshot.Count; i++)
        {
            var sample = snapshot[i];
            var bytes = snapshot.Read(i, scratch);

            // The muxer refuses when its queue is full, which means the disk is
            // behind. Waiting is correct here: this is the clip thread, and
            // dropping frames out of the middle of a clip the user asked for
            // would be worse than taking a moment longer.
            var attempts = 0;
            while (!muxer.TryWrite(bytes, sample.TimestampTicks, sample.DurationTicks, sample.IsKeyFrame))
            {
                if (muxer.IsFaulted)
                {
                    throw new IOException($"Writing {path} failed; see the log for the cause.");
                }

                if (++attempts > 2000)
                {
                    throw new IOException(
                        $"Writing {path} stalled: the muxer queue stayed full for over 20 seconds.");
                }

                Thread.Sleep(10);
            }
        }

        muxer.Finish(TimeSpan.FromSeconds(30));

        if (muxer.IsFaulted)
        {
            throw new IOException($"Finalising {path} failed; the file may be unplayable.");
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            throw new IOException($"{path} was not written.");
        }

        var metadata = new ClipMetadata
        {
            FilePath = path,
            DisplayName = Path.GetFileNameWithoutExtension(path),
            CreatedUtc = DateTimeOffset.UtcNow,
            DurationTicks = snapshot.Duration.Ticks,
            SizeBytes = info.Length,
            Width = _settings.Width,
            Height = _settings.Height,
            Codec = _settings.Codec.ToString().ToUpperInvariant(),
            Kind = _kind,
            GameName = request.GameName,
        };

        ClipMetadataStore.Save(metadata);
        return metadata;
    }
}
