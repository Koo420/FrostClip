using Frost.Engine.Capture;
using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Engine.Pipeline;
using Vortice.Direct3D11;

namespace Frost.Engine.Windows.Encode;

/// <summary>
/// Capture → NV12 → hardware encode → sink, with capture and encode on separate
/// threads.
/// </summary>
/// <remarks>
/// One encode feeds every consumer. The ring buffer and a full-session recording
/// are both just sinks (see <see cref="FanOutSampleSink"/>), so turning on
/// full-session recording while clipping costs a file handle, not a second
/// encoder.
/// <para>
/// Thread layout: <c>frost-capture</c> polls WGC and fills the frame queue;
/// <c>frost-encode</c> drains it, converts, encodes and pushes samples;
/// <c>frost-mux</c> (inside each <see cref="Mp4Muxer"/>) does the disk I/O. No
/// stage waits on the one after it — a full queue is a counted drop, never a
/// stall, because the stage in front of capture is the compositor.
/// </para>
/// </remarks>
internal sealed class VideoEncodePipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly CaptureConfiguration _captureConfig;
    private readonly EncoderSettings _encoderSettings;
    private readonly IEngineLog _log;

    private readonly WgcCaptureSource _capture;
    private readonly FrameQueue _queue;
    private readonly QueueingFrameSink _frameSink;
    private readonly Nv12Converter _converter;
    private readonly HardwareVideoEncoder _encoder;
    private readonly Thread _encodeThread;
    private readonly SemaphoreSlim _frameAvailable = new(0);

    private IEncodedSampleSink _sampleSink;
    private volatile bool _stopping;
    private long _framesEncoded;
    private long _framesDroppedByEncoder;
    private bool _disposed;

    private VideoEncodePipeline(
        GraphicsDevice device,
        CaptureConfiguration captureConfig,
        EncoderSettings encoderSettings,
        DiscoveredEncoder encoder,
        IEncodedSampleSink sampleSink,
        IEngineLog log)
    {
        _device = device;
        _captureConfig = captureConfig;
        _encoderSettings = encoderSettings;
        _sampleSink = sampleSink;
        _log = log;

        _capture = new WgcCaptureSource(captureConfig, device, log);

        // Two frames of slack beyond the texture pool: enough to absorb an
        // encoder hiccup, not enough to build up visible latency.
        _queue = new FrameQueue(captureConfig.TexturePoolSize + 2);

        _converter = new Nv12Converter(
            device,
            encoderSettings.Width,
            encoderSettings.Height,
            encoderSettings.Fps,
            poolSize: Math.Max(3, captureConfig.TexturePoolSize),
            expectedSourceTextures: captureConfig.TexturePoolSize,
            log);

        _encoder = new HardwareVideoEncoder(encoder, device, encoderSettings, _converter, log);

        _capture.Start(new SignallingFrameSink(_queue, _frameAvailable));
        _frameSink = new QueueingFrameSink(
            _queue,
            _capture.CurrentTexturePool
            ?? throw new InvalidOperationException("Capture started without a texture pool."));

        _encodeThread = new Thread(EncodeLoop)
        {
            Name = "frost-encode",

            // Same band as capture: this thread must keep up with the compositor,
            // but must not compete with the game's render thread.
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true,
        };

        _encodeThread.Start();
    }

    /// <summary>Starts a pipeline, picking the encoder from what the system offers.</summary>
    /// <exception cref="NoHardwareEncoderException">
    /// When no hardware encoder can serve the request. Deliberately fatal — Frost
    /// does not fall back to a software encode.
    /// </exception>
    internal static VideoEncodePipeline Start(
        GraphicsDevice device,
        CaptureConfiguration captureConfig,
        EncoderPreferences preferences,
        Func<int, int, EncoderSettings> settingsFactory,
        IEncodedSampleSink sampleSink,
        IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(captureConfig);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(settingsFactory);
        ArgumentNullException.ThrowIfNull(sampleSink);
        ArgumentNullException.ThrowIfNull(log);

        var discovered = new MediaFoundationEncoderEnumerator(log).EnumerateActivatable();

        try
        {
            var descriptors = discovered.Select(d => d.Descriptor).ToList();

            var resolved = preferences.CaptureAdapterVendorId is null
                ? preferences with { CaptureAdapterVendorId = device.AdapterVendorId }
                : preferences;

            var chosenDescriptor = EncoderSelector.Select(descriptors, resolved);
            var chosen = discovered.First(d => ReferenceEquals(d.Descriptor, chosenDescriptor));

            // Capture geometry decides encode geometry: no scaling, because a
            // rescale would be another per-frame GPU pass for no benefit.
            var item = CaptureItemFactory.Create(captureConfig.Target, log);
            var width = EvenDown(item.Size.Width);
            var height = EvenDown(item.Size.Height);

            var settings = settingsFactory(width, height);
            settings.Validate();

            return new VideoEncodePipeline(
                device, captureConfig, settings, chosen, sampleSink, log);
        }
        finally
        {
            // Every activate except the one handed to the encoder is released
            // here; the encoder's own is released with it.
            foreach (var entry in discovered)
            {
                entry.Dispose();
            }
        }
    }

    internal EncoderDescriptor Encoder => _encoder.Descriptor;

    internal EncoderSettings Settings => _encoderSettings;

    internal long FramesCaptured => _capture.FramesEmitted;

    internal long FramesDroppedByCapture => _capture.FramesDropped;

    internal long FramesEncoded => Interlocked.Read(ref _framesEncoded);

    internal long FramesDroppedByEncoder => Interlocked.Read(ref _framesDroppedByEncoder);

    internal long SamplesEncoded => _encoder.SamplesEncoded;

    internal long KeyFramesEncoded => _encoder.KeyFramesEncoded;

    internal long BytesEncoded => _encoder.BytesEncoded;

    internal int QueueDepth => _frameSink.Depth;

    internal bool IsCaptureRunning => _capture.IsRunning;

    /// <summary>
    /// The encoder's output media type, which a muxer needs for its codec private
    /// data.
    /// </summary>
    internal Vortice.MediaFoundation.IMFMediaType GetEncodedMediaType() => _encoder.GetOutputMediaType();

    /// <summary>
    /// Swaps the sink, so a full-session recording can be switched on or off
    /// without restarting the encoder.
    /// </summary>
    internal void SetSampleSink(IEncodedSampleSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Volatile.Write(ref _sampleSink, sink);
    }

    private void EncodeLoop()
    {
        try
        {
            while (!_stopping)
            {
                if (!_frameSink.TryTake(out var frame))
                {
                    // Waits on a semaphore the capture thread signals: no spinning,
                    // and no fixed poll interval to tune.
                    _frameAvailable.Wait(50);
                    continue;
                }

                try
                {
                    EncodeFrame(frame);
                }
                finally
                {
                    // Returns the capture texture whatever happened, or capture
                    // starves within a few frames.
                    _frameSink.Complete(frame);
                }
            }

            DrainRemaining();
        }
        catch (Exception ex)
        {
            _log.Error("Encode thread faulted; recording has stopped.", ex);
            _stopping = true;
        }
    }

    private void EncodeFrame(in CapturedFrame frame)
    {
        if (frame.Width != _encoderSettings.Width || frame.Height != _encoderSettings.Height)
        {
            // The capture target changed size. The encoder is configured for one
            // geometry, so the pipeline has to be restarted; drop until then
            // rather than feed the encoder a mismatched surface.
            Interlocked.Increment(ref _framesDroppedByEncoder);
            return;
        }

        var source = ResolveCaptureTexture(frame.Texture);
        if (source is null)
        {
            Interlocked.Increment(ref _framesDroppedByEncoder);
            return;
        }

        var nv12Index = _converter.Convert(source);

        _encoder.Encode(
            nv12Index,
            frame.TimestampTicks,
            TimeSpan.TicksPerSecond / _encoderSettings.Fps,
            Volatile.Read(ref _sampleSink));

        Interlocked.Increment(ref _framesEncoded);
    }

    private ID3D11Texture2D? ResolveCaptureTexture(nint handle) => _capture.ResolveSlotTexture(handle);

    private void DrainRemaining()
    {
        // Anything still queued belongs in the file: these are the frames closest
        // to the moment the user stopped.
        while (_frameSink.TryTake(out var frame))
        {
            try
            {
                EncodeFrame(frame);
            }
            finally
            {
                _frameSink.Complete(frame);
            }
        }

        _encoder.Drain(Volatile.Read(ref _sampleSink));
    }

    /// <summary>Stops capture and encode, flushing the encoder.</summary>
    internal void Stop()
    {
        if (_stopping)
        {
            return;
        }

        // Capture first, so the encode thread sees a queue that stops growing.
        _capture.Stop();

        _stopping = true;
        _frameAvailable.Release();

        if (!_encodeThread.Join(TimeSpan.FromSeconds(10)))
        {
            _log.Warn("Encode thread did not stop within 10s.");
        }

        _log.Info(
            $"Pipeline stopped: captured {FramesCaptured}, encoded {FramesEncoded} " +
            $"({KeyFramesEncoded} keyframes, {BytesEncoded / (1024.0 * 1024.0):F2}MB), " +
            $"dropped {FramesDroppedByCapture} in capture / {FramesDroppedByEncoder} in encode.");
    }

    /// <summary>
    /// Rounds down to an even number. NV12 is 4:2:0, so an odd dimension either
    /// fails at encoder configuration or silently crops.
    /// </summary>
    private static int EvenDown(int value) => value & ~1;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Stop();
        _encoder.Dispose();
        _converter.Dispose();
        _capture.Dispose();
        _frameAvailable.Dispose();
    }

    /// <summary>
    /// Enqueues a captured frame and wakes the encode thread.
    /// </summary>
    /// <remarks>
    /// Signalling here rather than letting the encode thread poll keeps encode
    /// latency at a semaphore release instead of a poll interval, which matters
    /// because the whole hotkey-to-file budget is measured in milliseconds.
    /// <c>SemaphoreSlim.Release</c> on an uncontended semaphore is a few
    /// interlocked operations and no allocation.
    /// </remarks>
    private sealed class SignallingFrameSink(FrameQueue queue, SemaphoreSlim signal) : IFrameSink
    {
        public bool TryAccept(in CapturedFrame frame)
        {
            if (!queue.TryEnqueue(frame))
            {
                return false;
            }

            signal.Release();
            return true;
        }
    }
}
