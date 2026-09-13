using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Shared.Clips;

namespace Frost.Engine.Recording;

/// <summary>
/// Records a whole play session to disk, fed by the same encode as the ring
/// buffer.
/// </summary>
/// <remarks>
/// <para><b>Why this shape.</b> The recorder is permanently part of the encoder's
/// fan-out and is a no-op when not recording, rather than being added to and
/// removed from the sink chain. Rewiring a live encoder's output would mean
/// mutating what the encode thread reads while it reads it; a bool check per
/// sample costs nothing and cannot race.</para>
///
/// <para><b>One encode, two destinations.</b> Turning this on costs a file handle
/// and some disk bandwidth. It does not start a second encoder, does not
/// re-encode, and does not change what the ring buffer sees — which is the whole
/// point of driving the encoder MFT directly rather than letting a Sink Writer
/// own the encode.</para>
///
/// <para><b>Starting mid-stream.</b> A recording that begins on a P-frame
/// references frames that are not in the file, and no decoder will play the first
/// GOP. So samples are discarded until the first keyframe arrives. At a 2-second
/// keyframe interval that is up to 2 seconds between pressing the key and the
/// first frame in the file — stated plainly rather than hidden, and reported by
/// <see cref="DiscardedBeforeFirstKeyFrame"/>.</para>
/// </remarks>
public sealed class FullSessionRecorder : IEncodedSampleSink, IBookmarkTarget, IDisposable
{
    private readonly Func<string, ISessionWriter> _writerFactory;
    private readonly IEngineLog _log;
    private readonly object _gate = new();
    private readonly List<Bookmark> _bookmarks = [];

    private ISessionWriter? _writer;
    private string? _path;
    private long _firstTimestampTicks = -1;
    private long _lastTimestampTicks;
    private long _samplesWritten;
    private long _samplesRefused;
    private long _discardedBeforeKeyFrame;
    private bool _sawKeyFrame;
    private DateTimeOffset _startedAt;
    private string? _gameName;
    private int _width;
    private int _height;
    private string _codec = "unknown";
    private bool _disposed;

    public FullSessionRecorder(Func<string, ISessionWriter> writerFactory, IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(writerFactory);
        ArgumentNullException.ThrowIfNull(log);

        _writerFactory = writerFactory;
        _log = log;
    }

    /// <summary>Whether a session recording is in progress.</summary>
    public bool IsRecording => Volatile.Read(ref _writer) is not null;

    /// <summary>Path of the file being written, or null.</summary>
    public string? CurrentPath => _path;

    public long SamplesWritten => Interlocked.Read(ref _samplesWritten);

    /// <summary>Samples the writer could not take, usually because the disk is behind.</summary>
    public long SamplesRefused => Interlocked.Read(ref _samplesRefused);

    /// <summary>
    /// Samples dropped while waiting for the first keyframe. Non-zero on every
    /// recording that starts mid-GOP, which is all of them.
    /// </summary>
    public long DiscardedBeforeFirstKeyFrame => Interlocked.Read(ref _discardedBeforeKeyFrame);

    /// <summary>Duration written so far.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _firstTimestampTicks < 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromTicks(_lastTimestampTicks - _firstTimestampTicks);
            }
        }
    }

    /// <summary>Bookmarks placed in the current recording.</summary>
    public IReadOnlyList<Bookmark> Bookmarks
    {
        get
        {
            lock (_gate)
            {
                return _bookmarks.ToArray();
            }
        }
    }

    /// <summary>
    /// Starts recording to <paramref name="path"/>.
    /// </summary>
    /// <param name="width">Frame width, for the metadata sidecar.</param>
    /// <param name="height">Frame height, for the metadata sidecar.</param>
    /// <param name="codec">Codec name, for the metadata sidecar.</param>
    /// <param name="gameName">Foreground process name, if known.</param>
    /// <param name="error">Why it could not start.</param>
    public bool TryStart(
        string path,
        int width,
        int height,
        string codec,
        string? gameName,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_writer is not null)
            {
                error = "A session recording is already in progress.";
                return false;
            }

            try
            {
                var writer = _writerFactory(path);

                _path = path;
                _width = width;
                _height = height;
                _codec = codec;
                _gameName = gameName;
                _startedAt = DateTimeOffset.UtcNow;
                _firstTimestampTicks = -1;
                _lastTimestampTicks = 0;
                _sawKeyFrame = false;
                _bookmarks.Clear();
                Interlocked.Exchange(ref _samplesWritten, 0);
                Interlocked.Exchange(ref _samplesRefused, 0);
                Interlocked.Exchange(ref _discardedBeforeKeyFrame, 0);

                // Published last: until this is set, the encode thread's TryWrite
                // is a no-op, so no sample can reach a half-initialised recorder.
                Volatile.Write(ref _writer, writer);

                _log.Info($"Session recording started: {path}.");
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _path = null;
                error = $"Could not start recording to {path}: {ex.Message}";
                _log.Error(error, ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Encode-thread entry point. A single volatile read when not recording.
    /// </summary>
    public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
    {
        var writer = Volatile.Read(ref _writer);

        if (writer is null || data.IsEmpty)
        {
            return false;
        }

        if (!_sawKeyFrame)
        {
            if (!isKeyFrame)
            {
                // The file has to begin on a keyframe or its first GOP is
                // undecodable.
                Interlocked.Increment(ref _discardedBeforeKeyFrame);
                return false;
            }

            _sawKeyFrame = true;
        }

        if (!writer.TryWrite(data, timestampTicks, durationTicks, isKeyFrame))
        {
            Interlocked.Increment(ref _samplesRefused);
            return false;
        }

        lock (_gate)
        {
            if (_firstTimestampTicks < 0)
            {
                _firstTimestampTicks = timestampTicks;
            }

            _lastTimestampTicks = timestampTicks + durationTicks;
        }

        Interlocked.Increment(ref _samplesWritten);
        return true;
    }

    /// <summary>
    /// Marks the current moment.
    /// </summary>
    /// <remarks>
    /// The cheap alternative to saving a clip: it records an offset and nothing
    /// else, so the user can flag a moment mid-fight and find it later without
    /// paying for an export.
    /// </remarks>
    public bool TryAddBookmark(BookmarkSource source, string? label, out string? error)
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                error = "There is no session recording to bookmark.";
                return false;
            }

            if (_firstTimestampTicks < 0)
            {
                error = "The recording has not written a frame yet.";
                return false;
            }

            var offset = _lastTimestampTicks - _firstTimestampTicks;
            _bookmarks.Add(new Bookmark(offset, source, label));
            _log.Info($"Bookmark at {TimeSpan.FromTicks(offset):hh\\:mm\\:ss} ({source}).");
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Stops recording, finalises the file and writes its sidecar.
    /// </summary>
    /// <returns>Metadata for the finished file, or null if nothing was recorded.</returns>
    public ClipMetadata? Stop()
    {
        ISessionWriter? writer;
        string? path;
        Bookmark[] bookmarks;
        TimeSpan elapsed;

        lock (_gate)
        {
            writer = _writer;

            if (writer is null)
            {
                return null;
            }

            // Cleared first, so the encode thread stops feeding a writer that is
            // about to be finalised.
            Volatile.Write(ref _writer, null);

            path = _path;
            bookmarks = [.. _bookmarks];
            elapsed = _firstTimestampTicks < 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(_lastTimestampTicks - _firstTimestampTicks);
        }

        var finalised = true;

        try
        {
            writer.Finish(TimeSpan.FromSeconds(30));
            finalised = !writer.IsFaulted;
        }
        catch (Exception ex)
        {
            finalised = false;
            _log.Error($"Finalising {path} failed.", ex);
        }
        finally
        {
            writer.Dispose();
        }

        if (path is null || SamplesWritten == 0)
        {
            _log.Warn("The session recording contained no frames; nothing was saved.");
            return null;
        }

        if (!finalised)
        {
            // An MP4 whose moov atom was never written will not open anywhere, so
            // returning metadata would put a broken entry in the gallery looking
            // like a good recording. The bytes are left on disk rather than
            // deleted — a repair tool may get something out of them, and throwing
            // away someone's session unasked is worse than an orphan file — but
            // the path is named so they can find it.
            _log.Error(
                $"The session recording at {path} could not be finalised and is probably unplayable. " +
                "The partial file has been left in place.");
            return null;
        }

        var size = 0L;
        try
        {
            var info = new FileInfo(path);
            size = info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            // Size is cosmetic; a recording that exists but cannot be stat'd is
            // still a recording.
        }

        var metadata = new ClipMetadata
        {
            FilePath = path,
            DisplayName = Path.GetFileNameWithoutExtension(path),
            CreatedUtc = _startedAt,
            DurationTicks = elapsed.Ticks,
            SizeBytes = size,
            Width = _width,
            Height = _height,
            Codec = _codec,
            Kind = ClipKind.FullSession,
            GameName = _gameName,
            Bookmarks = bookmarks,
        };

        try
        {
            ClipMetadataStore.Save(metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The video is what matters; a missing sidecar degrades the gallery
            // entry rather than losing the recording.
            _log.Warn($"Could not write the sidecar for {path}.", ex);
        }

        _log.Info(
            $"Session recording stopped: {path}, {elapsed:hh\\:mm\\:ss}, " +
            $"{size / (1024.0 * 1024.0):F1}MB, {bookmarks.Length} bookmark(s)" +
            $"{(SamplesRefused > 0 ? $", {SamplesRefused} sample(s) refused" : string.Empty)}.");

        return metadata;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A recording in progress at shutdown must still produce a playable file:
        // without Finish() the MP4 has no index and will not open anywhere.
        Stop();
    }
}
