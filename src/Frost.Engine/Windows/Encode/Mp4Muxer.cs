using System.Runtime.InteropServices;
using Frost.Engine.Audio;
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

    // Audio is a second stream on the same sink. Null when the recording has no
    // audio, in which case every audio path here is a no-op.
    private readonly SampleArena? _audioQueue;
    private readonly object _audioGate = new();
    private readonly byte[] _audioScratch = [];
    private readonly IMFSample? _audioSample;
    private readonly IMFMediaBuffer? _audioBuffer;
    private readonly int _audioStreamIndex = -1;

    private long _samplesQueued;
    private long _samplesWritten;
    private long _samplesRefused;
    private long _bytesWritten;
    private long _audioSamplesWritten;
    private long _audioSamplesRefused;
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
    /// <param name="audioFormat">
    /// When given, a second stream is added for audio. The Sink Writer inserts the
    /// AAC encoder for it — audio AAC encoding is a fraction of a percent of one
    /// core, so unlike video it is not worth a hardware path and does not conflict
    /// with the hardware-encoding-only rule, which is about the frame-rate-sized
    /// workload.
    /// </param>
    /// <param name="audioBitsPerSecond">AAC bitrate. 160kbps is transparent for game audio.</param>
    internal Mp4Muxer(
        string path,
        IMFMediaType encodedType,
        IEngineLog log,
        long queueBytes = 16L * 1024 * 1024,
        int queueSamples = 512,
        AudioFormat? audioFormat = null,
        int audioBitsPerSecond = 160_000)
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

        if (audioFormat is not null)
        {
            AudioFormat = audioFormat.AsInt16;

            using var aac = CreateAacType(AudioFormat, audioBitsPerSecond);
            _audioStreamIndex = _writer.AddStream(aac);

            using var pcm = CreatePcmType(AudioFormat);
            _writer.SetInputMediaType(_audioStreamIndex, pcm, null);

            // One second of audio of queue: audio blocks are tiny (188KB/s) and the
            // extra depth costs nothing, while a disk stall during a session
            // recording should not lose any.
            _audioQueue = new SampleArena(
                Math.Max(1L << 20, AudioFormat.BytesFor(TimeSpan.FromSeconds(4))), 2048);

            _audioScratch = new byte[Math.Min(_audioQueue.ByteCapacity, 1 << 20)];
            _audioBuffer = MediaFactory.MFCreateMemoryBuffer(_audioScratch.Length);
            _audioSample = MediaFactory.MFCreateSample();
            _audioSample.AddBuffer(_audioBuffer);

            log.Debug($"Muxing audio as AAC {audioBitsPerSecond / 1000}kbps from {AudioFormat}.");
        }

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

    /// <summary>PCM format of the audio stream, or null when there is no audio.</summary>
    internal AudioFormat? AudioFormat { get; }

    /// <summary>Whether this file carries an audio track.</summary>
    internal bool HasAudio => _audioStreamIndex >= 0;

    internal long AudioSamplesWritten => Interlocked.Read(ref _audioSamplesWritten);

    internal long AudioSamplesRefused => Interlocked.Read(ref _audioSamplesRefused);

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
    /// Queues a block of PCM audio. Same contract as
    /// <see cref="TryWrite"/>: never blocks on disk, never allocates.
    /// </summary>
    public bool TryWriteAudio(ReadOnlySpan<byte> data, long timestampTicks)
    {
        if (_audioQueue is null || _stopping || _faulted || data.IsEmpty)
        {
            return false;
        }

        var format = AudioFormat!;
        var duration = format.FramesToTicks(data.Length / format.BytesPerFrame);

        bool queued;
        lock (_audioGate)
        {
            queued = _audioQueue.TryAppend(data, timestampTicks, duration, isKeyFrame: true, out _);
        }

        if (!queued)
        {
            Interlocked.Increment(ref _audioSamplesRefused);
            return false;
        }

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
                $"Wrote {Path}: {SamplesWritten} video sample(s)" +
                $"{(HasAudio ? $", {AudioSamplesWritten} audio block(s)" : string.Empty)}, " +
                $"{BytesWritten / (1024.0 * 1024.0):F2}MB" +
                $"{(SamplesRefused > 0 ? $", {SamplesRefused} video refused" : string.Empty)}" +
                $"{(AudioSamplesRefused > 0 ? $", {AudioSamplesRefused} audio refused" : string.Empty)}.");
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

    /// <summary>
    /// Writes whichever queued sample is oldest.
    /// </summary>
    /// <remarks>
    /// Interleaving by timestamp rather than draining video then audio: the MP4
    /// sink buffers whatever it is given out of order, and feeding it a whole
    /// session's video before any audio would make it hold the lot in memory.
    /// </remarks>
    private bool DrainOne()
    {
        var videoNext = PeekVideoTimestamp();
        var audioNext = PeekAudioTimestamp();

        if (videoNext is null && audioNext is null)
        {
            return false;
        }

        // Prefer the older stream; when only one has anything, take it.
        if (audioNext is not null && (videoNext is null || audioNext < videoNext))
        {
            return DrainAudio();
        }

        return DrainVideo();
    }

    private long? PeekVideoTimestamp()
    {
        lock (_queueGate)
        {
            return _queue.IsEmpty ? null : _queue.Peek().TimestampTicks;
        }
    }

    private long? PeekAudioTimestamp()
    {
        if (_audioQueue is null)
        {
            return null;
        }

        lock (_audioGate)
        {
            return _audioQueue.IsEmpty ? null : _audioQueue.Peek().TimestampTicks;
        }
    }

    private bool DrainAudio()
    {
        EncodedSample sample;
        int length;

        lock (_audioGate)
        {
            if (_audioQueue!.IsEmpty)
            {
                return false;
            }

            sample = _audioQueue.Peek();
            length = sample.Length;

            if (length > _audioScratch.Length)
            {
                // Cannot happen with the queue's own sizing, but dropping one block
                // beats writing a truncated frame.
                _audioQueue.DropOldest();
                Interlocked.Increment(ref _audioSamplesRefused);
                return true;
            }

            _audioQueue.CopyTo(sample, _audioScratch);
            _audioQueue.DropOldest();
        }

        _audioBuffer!.Lock(out var pointer, out _, out _);
        Marshal.Copy(_audioScratch, 0, pointer, length);
        _audioBuffer.CurrentLength = length;
        _audioBuffer.Unlock();

        if (_firstTimestampTicks < 0)
        {
            _firstTimestampTicks = sample.TimestampTicks;
        }

        // Audio may legitimately start before the first video frame (the encoder
        // holds frames while audio flows). Clamping rather than writing a negative
        // timestamp, which the sink rejects.
        var time = Math.Max(0, sample.TimestampTicks - _firstTimestampTicks);

        _audioSample!.SampleTime = time;
        _audioSample.SampleDuration = sample.DurationTicks;
        _audioSample.SampleFlags = 1;

        _writer.WriteSample(_audioStreamIndex, _audioSample);

        Interlocked.Increment(ref _audioSamplesWritten);
        Interlocked.Add(ref _bytesWritten, length);
        return true;
    }

    private bool DrainVideo()
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

        _sample.SampleTime = Math.Max(0, sample.TimestampTicks - _firstTimestampTicks);
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

    /// <summary>The AAC output type for the audio stream.</summary>
    private static IMFMediaType CreateAacType(AudioFormat format, int bitsPerSecond)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)format.SampleRate);
        type.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)format.Channels);
        type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(bitsPerSecond / 8));

        // 0 = raw AAC, which is what an MP4 container wants (ADTS framing is for
        // streaming).
        type.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
        return type;
    }

    /// <summary>The PCM input type the Sink Writer's AAC encoder reads.</summary>
    private static IMFMediaType CreatePcmType(AudioFormat format)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)format.SampleRate);
        type.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)format.Channels);
        type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        type.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)format.BytesPerFrame);
        type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)format.BytesPerSecond);
        return type;
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
        _audioSample?.Dispose();
        _audioBuffer?.Dispose();
        _writer.Dispose();
        _work.Dispose();
    }
}
