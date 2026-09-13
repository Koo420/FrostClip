using Frost.Engine.Audio;
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
    private readonly IReadOnlyList<AudioTrackBuffer> _audioTracks;
    private byte[] _audioScratch = [];

    /// <param name="mediaTypeFactory">
    /// Supplies the encoder's current output media type, which carries the codec
    /// private data (H.264's SPS/PPS). Called per clip because the encoder can be
    /// restarted between clips.
    /// </param>
    /// <param name="audioTracks">
    /// Audio buffers to trim to the clip's window — system audio, and the
    /// microphone as its own track when enabled. Empty for a silent clip. These
    /// are the same buffers the audio capture writes into: a clip takes the slice
    /// matching its video rather than keeping a separate copy.
    /// </param>
    internal Mp4ClipWriter(
        Func<IMFMediaType> mediaTypeFactory,
        EncoderSettings settings,
        IEngineLog log,
        ClipKind kind = ClipKind.InstantClip,
        IReadOnlyList<AudioTrackBuffer>? audioTracks = null)
    {
        ArgumentNullException.ThrowIfNull(mediaTypeFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _mediaTypeFactory = mediaTypeFactory;
        _settings = settings;
        _log = log;
        _kind = kind;
        _audioTracks = audioTracks ?? [];
    }

    public string Extension => ".mp4";

    /// <summary>
    /// Copies the audio covering the clip's video window into the muxer.
    /// </summary>
    /// <remarks>
    /// Whole audio blocks are taken, so the audio can start a few milliseconds
    /// before the first video frame. That is deliberate: trimming audio to the
    /// exact frame boundary would cut a word mid-syllable, and the muxer clamps a
    /// negative timestamp to zero.
    /// </remarks>
    private void WriteAudioFor(RingSnapshot snapshot, Mp4Muxer muxer)
    {
        if (_audioTracks.Count == 0 || snapshot.IsEmpty)
        {
            return;
        }

        var startTicks = snapshot[0].TimestampTicks;
        var endTicks = snapshot[snapshot.Count - 1].EndTimestampTicks;

        var needed = _audioTracks.Max(track => track.MaxSnapshotBytes);

        if (_audioScratch.Length < needed)
        {
            // Grown once per writer, never per clip.
            _audioScratch = new byte[needed];
        }

        for (var track = 0; track < _audioTracks.Count; track++)
        {
            WriteTrack(track, _audioTracks[track], startTicks, endTicks, muxer);
        }
    }

    private void WriteTrack(
        int track, AudioTrackBuffer buffer, long startTicks, long endTicks, Mp4Muxer muxer)
    {
        var audio = buffer.Snapshot(startTicks, endTicks, _audioScratch);

        if (audio.IsEmpty)
        {
            _log.Debug($"No audio on track {track} covered the clip's window.");
            return;
        }

        // One block per 20ms, so the muxer's queue sees roughly the shape it would
        // during a live session recording.
        var format = buffer.Format;
        var blockBytes = format.BytesPerFrame * (format.SampleRate / 50);
        var offset = 0;

        while (offset < audio.ByteCount)
        {
            var length = Math.Min(blockBytes, audio.ByteCount - offset);
            var frames = offset / format.BytesPerFrame;

            if (!muxer.TryWriteAudio(
                    track,
                    new ReadOnlySpan<byte>(_audioScratch, offset, length),
                    audio.StartTicks + format.FramesToTicks(frames)))
            {
                // The audio queue is full, which means the disk is far behind.
                // Losing the tail of the audio is better than failing the clip.
                _log.Warn(
                    $"Audio queue full on track {track} while writing {muxer.Path}; " +
                    "that track's audio is short.");
                break;
            }

            offset += length;
        }
    }

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
            queueSamples: Math.Clamp(snapshot.Count, 64, 4096),
            audioFormats: _audioTracks.Count > 0
                ? _audioTracks.Select(track => track.Format).ToList()
                : null);

        // Audio first: the sink interleaves by timestamp anyway, and queueing the
        // audio up front means it is already present when the video catches up,
        // rather than the sink holding video while it waits for a track it has been
        // told to expect.
        WriteAudioFor(snapshot, muxer);

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
