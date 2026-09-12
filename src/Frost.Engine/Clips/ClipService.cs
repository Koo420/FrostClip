using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Shared.Clips;

namespace Frost.Engine.Clips;

/// <summary>A request to save the trailing N seconds as a clip.</summary>
/// <param name="Duration">How far back to go.</param>
/// <param name="Label">Preset label for the file name, e.g. "30s".</param>
/// <param name="GameName">Foreground process name, if the caller knows it.</param>
/// <param name="RequestedAtTicks">Monotonic tick when the hotkey fired, for latency reporting.</param>
public readonly record struct ClipRequest(
    TimeSpan Duration,
    string? Label = null,
    string? GameName = null,
    long RequestedAtTicks = 0);

/// <summary>Outcome of writing one clip.</summary>
public sealed record ClipResult
{
    public required bool Succeeded { get; init; }

    public ClipMetadata? Metadata { get; init; }

    public string? Error { get; init; }

    /// <summary>Wall time from the request being dequeued to the file being closed.</summary>
    public TimeSpan WriteDuration { get; init; }

    public static ClipResult Failure(string error) => new() { Succeeded = false, Error = error };
}

/// <summary>Writes a pinned ring-buffer snapshot to a file.</summary>
public interface IClipWriter
{
    /// <summary>Container extension, with the dot.</summary>
    string Extension { get; }

    /// <summary>
    /// Muxes <paramref name="snapshot"/> to <paramref name="path"/>. Called on
    /// the clip thread, so blocking on disk here is correct.
    /// </summary>
    ClipMetadata Write(RingSnapshot snapshot, string path, ClipRequest request);
}

/// <summary>
/// Turns hotkey presses into clip files, on its own thread.
/// </summary>
/// <remarks>
/// <para>The hotkey callback runs inside a low-level keyboard hook, where any
/// delay is a delay in the user's keystroke reaching the game. So
/// <see cref="Request"/> does almost nothing: it drops a struct into a bounded
/// queue, signals, and returns. Everything else — opening a snapshot, muxing,
/// writing the sidecar — happens on <c>frost-clip</c>.</para>
///
/// <para>Requests are handled one at a time, because each holds a ring-buffer
/// snapshot and two concurrent writers could together outlast the buffer's
/// headroom. Two hotkeys pressed at once therefore produce two clips in
/// sequence, which is what the user expects anyway.</para>
/// </remarks>
public sealed class ClipService : IDisposable
{
    /// <summary>
    /// Pending requests allowed. Small on purpose: a queue of clip requests
    /// deeper than this means something is holding a key down, and the right
    /// answer is to drop the extras rather than write fifty near-identical files.
    /// </summary>
    public const int MaxPendingRequests = 8;

    private readonly EncodedSampleRing _ring;
    private readonly IClipWriter _writer;
    private readonly string _directory;
    private readonly IEngineLog _log;
    private readonly Func<string, bool> _fileExists;

    private readonly ClipRequest[] _pending = new ClipRequest[MaxPendingRequests];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly Thread _thread;

    private int _pendingCount;
    private long _requested;
    private long _dropped;
    private long _saved;
    private long _failed;
    private volatile bool _stopping;
    private bool _disposed;

    public ClipService(
        EncodedSampleRing ring,
        IClipWriter writer,
        string directory,
        IEngineLog log,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(log);

        _ring = ring;
        _writer = writer;
        _directory = directory;
        _log = log;
        _fileExists = fileExists ?? File.Exists;

        _thread = new Thread(WriteLoop)
        {
            Name = "frost-clip",
            IsBackground = true,

            // Below capture and encode: a clip write must never cost frames. The
            // user perceives the save as instant because the toast fires on the
            // keypress, not because the file is written in that instant.
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
    }

    /// <summary>
    /// Raised the moment a request is accepted, on the calling (hotkey) thread.
    /// Keep handlers trivial — this is what drives the sub-150ms toast.
    /// </summary>
    public event Action<ClipRequest>? ClipRequested;

    /// <summary>Raised on the clip thread when a clip has been written.</summary>
    public event Action<ClipResult>? ClipCompleted;

    public long RequestsAccepted => Interlocked.Read(ref _requested);

    /// <summary>Requests dropped because the queue was full.</summary>
    public long RequestsDropped => Interlocked.Read(ref _dropped);

    public long ClipsSaved => Interlocked.Read(ref _saved);

    public long ClipsFailed => Interlocked.Read(ref _failed);

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pendingCount;
            }
        }
    }

    /// <summary>
    /// Accepts a clip request. Returns false only if the queue is full or the
    /// service is shutting down. Does no I/O and does not allocate.
    /// </summary>
    public bool Request(ClipRequest request)
    {
        if (_stopping || request.Duration <= TimeSpan.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            if (_pendingCount == _pending.Length)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            _pending[_pendingCount++] = request;
        }

        Interlocked.Increment(ref _requested);

        // Fires before the file exists, deliberately: the user gets feedback on
        // the keypress rather than after the disk catches up.
        ClipRequested?.Invoke(request);

        _work.Release();
        return true;
    }

    /// <summary>Waits for the queue to drain. For shutdown and for tests.</summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (PendingCount == 0 && !_writing)
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return PendingCount == 0 && !_writing;
    }

    private volatile bool _writing;

    private void WriteLoop()
    {
        while (true)
        {
            _work.Wait();

            while (TryDequeue(out var request))
            {
                _writing = true;
                try
                {
                    Handle(request);
                }
                catch (Exception ex)
                {
                    // A failed clip must not take the service down: the next
                    // hotkey press has to still work.
                    Interlocked.Increment(ref _failed);
                    _log.Error("Writing a clip failed.", ex);
                    ClipCompleted?.Invoke(ClipResult.Failure(ex.Message));
                }
                finally
                {
                    _writing = false;
                }
            }

            if (_stopping)
            {
                return;
            }
        }
    }

    private bool TryDequeue(out ClipRequest request)
    {
        lock (_gate)
        {
            if (_pendingCount == 0)
            {
                request = default;
                return false;
            }

            request = _pending[0];
            for (var i = 1; i < _pendingCount; i++)
            {
                _pending[i - 1] = _pending[i];
            }

            _pending[--_pendingCount] = default;
            return true;
        }
    }

    private void Handle(ClipRequest request)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        using var snapshot = _ring.OpenSnapshot(request.Duration);

        if (snapshot.IsEmpty)
        {
            Interlocked.Increment(ref _failed);
            const string message =
                "Nothing to clip yet — the buffer is still filling. Give it a few seconds.";
            _log.Warn(message);
            ClipCompleted?.Invoke(ClipResult.Failure(message));
            return;
        }

        Directory.CreateDirectory(_directory);

        var fileName = ClipNaming.BuildFileName(
            DateTimeOffset.Now, request.GameName, request.Label, _writer.Extension);

        var path = ClipNaming.MakeUniquePath(_directory, fileName, _fileExists);

        var metadata = _writer.Write(snapshot, path, request);

        Interlocked.Increment(ref _saved);
        clock.Stop();

        _log.Info(
            $"Saved {path}: {snapshot.Duration.TotalSeconds:F1}s, " +
            $"{snapshot.TotalBytes / (1024.0 * 1024.0):F2}MB, written in {clock.ElapsedMilliseconds}ms.");

        ClipCompleted?.Invoke(new ClipResult
        {
            Succeeded = true,
            Metadata = metadata,
            WriteDuration = clock.Elapsed,
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WaitForIdle(TimeSpan.FromSeconds(30));

        _stopping = true;
        _work.Release();
        _thread.Join(TimeSpan.FromSeconds(10));
        _work.Dispose();
    }
}
