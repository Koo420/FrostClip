using System.Runtime.InteropServices;
using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Vortice.MediaFoundation;

namespace Frost.Engine.Windows.Encode;

/// <summary>
/// Writes already-encoded frames into an MP4, on its own thread.
/// </summary>
/// <remarks>
/// <para>This is the Media Foundation Sink Writer used as a <i>muxer</i>, not an
/// encoder: the stream's input media type is the same H.264/HEVC/AV1 type as its
/// output, so the Sink Writer inserts no transform and passes samples straight
/// into the MP4 sink. That is what makes "one encode, two destinations" possible
/// — a clip and a full-session recording are two muxers fed from the same
/// encoded bytes, and neither costs a second encode.</para>
///
/// <para><b>Why a thread.</b> Writing to disk blocks, sometimes for tens of
/// milliseconds when the OS decides to flush. The encode thread cannot afford
/// that, so samples are copied into a pre-allocated <see cref="SampleArena"/>
/// and a writer thread drains it. The lock around the arena is held for a
/// <c>memcpy</c> and nothing else; the file write happens outside it.</para>
/// </remarks>
internal sealed class Mp4Muxer : IEncodedSampleSink, IDisposable
{
    private readonly IEngineLog _log;
    private readonly SampleArena _queue;
    private readonly object _queueGate = new();
    private readonly byte[] _writeScratch;
    private readonly IMFSinkWriter _writer;
    private readonly IMFSample _sample;
    private readonly IMFMediaBuffer _buffer;
    private readonly int _streamIndex;
    private readonly Thread _thread;
    private readonly SemaphoreSlim _work = new(0);

    private long _samplesQueued;
    private long _samplesWritten;
    private long _samplesRefused;
    private long _bytesWritten;
    private long _firstTimestampTicks = -1;
    private volatile bool _stopping;
    private volatile bool _faulted;
    private bool _disposed;

    /// <param name="path">Destination .mp4. Overwritten if it exists.</param>
    /// <param name="encodedType">
    /// The encoder's own output media type, which carries the codec private data.
    /// </param>
    /// <param name="queueBytes">Pre-allocated write queue size.</param>
    /// <param name="queueSamples">Pre-allocated write queue depth.</param>
    internal Mp4Muxer(
        string path,
        IMFMediaType encodedType,
        IEngineLog log,
        long queueBytes = 16L * 1024 * 1024,
        int queueSamples = 512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(encodedType);
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        Path = path;
        _queue = new SampleArena(queueBytes, queueSamples);

        // Big enough for any single encoded frame the queue can hold.
        _writeScratch = new byte[Math.Min(_queue.ByteCapacity, 8L * 1024 * 1024)];

        Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))
            ?? throw new ArgumentException($"'{path}' has no directory.", nameof(path)));

        using var attributes = MediaFactory.MFCreateAttributes(4);

        // Hardware transforms stay enabled even though we expect none to be
        // inserted: if a future codec needs a hardware repackager, it should be
        // allowed, and a software one should not.
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);

        // The samples are already encoded and already paced; throttling would
        // only add latency between a hotkey press and the finished file.
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);

        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);
        _streamIndex = _writer.AddStream(encodedType);

        // Input type identical to the output type: passthrough, no transform.
        _writer.SetInputMediaType(_streamIndex, encodedType, null);
        _writer.BeginWriting();

        _buffer = MediaFactory.MFCreateMemoryBuffer((int)_writeScratch.Length);
        _sample = MediaFactory.MFCreateSample();
        _sample.AddBuffer(_buffer);

        _thread = new Thread(WriteLoop)
        {
            Name = "frost-mux",
            IsBackground = true,

            // Below the capture and encode threads: falling behind on disk costs
            // queue depth, while falling behind on capture costs frames.
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
        log.Info($"Muxing to {path}.");
    }

    internal string Path { get; }

    internal long SamplesWritten => Interlocked.Read(ref _samplesWritten);

    internal long SamplesRefused => Interlocked.Read(ref _samplesRefused);

    internal long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>True once the writer thread hit an error; no further samples are accepted.</summary>
    internal bool IsFaulted => _faulted;

    /// <summary>
    /// Queues an encoded frame. Returns false if the queue is full or the writer
    /// has faulted. Never blocks on disk and never allocates.
    /// </summary>
    public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
    {
        if (_stopping || _faulted || data.IsEmpty)
        {
            return false;
        }

        bool queued;
        lock (_queueGate)
        {
            queued = _queue.TryAppend(data, timestampTicks, durationTicks, isKeyFrame, out _);
        }

        if (!queued)
        {
            // The disk is not keeping up. Refusing is right: the alternative is
            // stalling the encode thread, which stalls capture.
            Interlocked.Increment(ref _samplesRefused);
            return false;
        }

        Interlocked.Increment(ref _samplesQueued);
        _work.Release();
        return true;
    }

    /// <summary>
    /// Drains the queue, finalises the MP4 (writing its index) and joins the
    /// writer thread. Safe to call once.
    /// </summary>
    internal void Finish(TimeSpan timeout)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _work.Release();

        if (!_thread.Join(timeout))
        {
            _log.Warn($"Muxer thread for {Path} did not finish within {timeout.TotalSeconds:F0}s.");
        }

        try
        {
            // Finalize writes the moov atom. Without it the file is unplayable,
            // so this is the one step that must not be skipped on shutdown.
            _writer.Finalize();
            _log.Info(
                $"Wrote {Path}: {SamplesWritten} samples, " +
                $"{BytesWritten / (1024.0 * 1024.0):F2}MB" +
                $"{(SamplesRefused > 0 ? $", {SamplesRefused} refused" : string.Empty)}.");
        }
        catch (Exception ex)
        {
            _faulted = true;
            _log.Error($"Finalising {Path} failed; the file may be unplayable.", ex);
        }
    }

    private void WriteLoop()
    {
        try
        {
            while (true)
            {
                _work.Wait();

                while (DrainOne())
                {
                }

                if (_stopping)
                {
                    // One last pass, in case a sample arrived between the drain
                    // and the stop flag being observed.
                    while (DrainOne())
                    {
                    }

                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            _log.Error($"Muxer thread for {Path} faulted; recording to this file has stopped.", ex);
        }
    }

    private bool DrainOne()
    {
        EncodedSample sample;
        int length;

        lock (_queueGate)
        {
            if (_queue.IsEmpty)
            {
                return false;
            }

            sample = _queue.Peek();
            length = sample.Length;
            _queue.CopyTo(sample, _writeScratch);
            _queue.DropOldest();
        }

        Marshal.Copy(_writeScratch, 0, LockBuffer(), length);
        _buffer.CurrentLength = length;
        _buffer.Unlock();

        // MP4 timestamps start at zero; the capture clock does not.
        if (_firstTimestampTicks < 0)
        {
            _firstTimestampTicks = sample.TimestampTicks;
        }

        _sample.SampleTime = sample.TimestampTicks - _firstTimestampTicks;
        _sample.SampleDuration = sample.DurationTicks;
        _sample.SampleFlags = sample.IsKeyFrame ? 1 : 0;

        _writer.WriteSample(_streamIndex, _sample);

        Interlocked.Increment(ref _samplesWritten);
        Interlocked.Add(ref _bytesWritten, length);
        return true;
    }

    private nint LockBuffer()
    {
        _buffer.Lock(out var pointer, out _, out _);
        return pointer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_stopping)
        {
            Finish(TimeSpan.FromSeconds(10));
        }

        _sample.Dispose();
        _buffer.Dispose();
        _writer.Dispose();
        _work.Dispose();
    }
}
