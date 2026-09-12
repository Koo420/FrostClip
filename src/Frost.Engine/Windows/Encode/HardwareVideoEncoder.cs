using System.Runtime.InteropServices;
using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Engine.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using MfResult = Vortice.MediaFoundation.ResultCode;

namespace Frost.Engine.Windows.Encode;

/// <summary>
/// Drives a hardware encoder MFT directly: NV12 textures in, encoded frames out.
/// </summary>
/// <remarks>
/// <para><b>Why the MFT and not the Sink Writer.</b> The Sink Writer is the easy
/// way to encode to a file, but it gives no access to the encoded frames, and
/// Frost needs them: the ring buffer holds the trailing N seconds of
/// <i>encoded</i> data, and a full-session recording has to be fed from the same
/// encode rather than a second one. Driving the MFT means one encode feeds both.
/// The Sink Writer is still used, as a pure muxer, in
/// <see cref="Mp4Muxer"/>.</para>
///
/// <para><b>Async MFTs.</b> Every real hardware encoder (NVENC, AMF, QuickSync)
/// is an async MFT, so the transform is unlocked for async use and driven by its
/// event generator: <c>METransformNeedInput</c> says when to push a frame,
/// <c>METransformHaveOutput</c> says when an encoded frame is ready. A
/// synchronous fallback is kept for MFTs that report otherwise.</para>
///
/// <para><b>Allocation.</b> Input samples and their DXGI buffers are created once
/// per NV12 pool texture and reused for the life of the session, and encoded
/// bytes are copied straight into the sink's own storage. Where the MFT provides
/// its own output samples (which hardware MFTs generally do) the managed wrapper
/// for each is unavoidable through the projection; it is disposed immediately so
/// it stays a short-lived Gen0 object, and the cost is recorded in PROGRESS.md
/// rather than glossed over.</para>
/// </remarks>
internal sealed class HardwareVideoEncoder : IDisposable
{
    private const int StreamId = 0;

    private readonly EncoderSettings _settings;
    private readonly IEngineLog _log;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly CodecApi? _codecApi;
    private readonly IMFDXGIDeviceManager _deviceManager;

    // One reusable input sample per NV12 texture: the converter cycles through
    // those textures, so the encoder cycles through these in lockstep.
    private readonly IMFSample[] _inputSamples;
    private readonly IMFMediaBuffer[] _inputBuffers;

    // Reusable output sample, used only when the MFT expects the caller to
    // allocate. Hardware MFTs usually provide their own instead.
    private readonly IMFSample? _outputSample;
    private readonly IMFMediaBuffer? _outputBuffer;

    private byte[] _copyBuffer;
    private long _sequence;
    private bool _streaming;
    private bool _disposed;

    internal HardwareVideoEncoder(
        DiscoveredEncoder encoder,
        GraphicsDevice device,
        EncoderSettings settings,
        Nv12Converter converter,
        IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(log);

        settings.Validate();

        if (!encoder.Descriptor.IsHardware)
        {
            // Belt and braces: EncoderSelector already refuses software encoders,
            // but nothing downstream of here should ever be able to start one.
            throw new InvalidOperationException(
                $"'{encoder.Descriptor.Name}' is a software encoder. Frost only uses hardware encoders.");
        }

        _settings = settings;
        _log = log;
        Descriptor = encoder.Descriptor;

        _transform = encoder.Activate.ActivateObject<IMFTransform>();

        try
        {
            IsAsync = UnlockAsyncIfNeeded(_transform, log);

            if (IsAsync)
            {
                _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            }

            _deviceManager = CreateDeviceManager(device);

            // The encoder has to share our D3D11 device, or it would copy every
            // frame out to system memory and back to encode it.
            _transform.ProcessMessage(
                TMessageType.MessageSetD3DManager, (nuint)(nint)_deviceManager.NativePointer);

            _codecApi = CodecApi.TryCreate(_transform.NativePointer, log);
            ConfigureCodecApi();

            // Output type before input type: encoders reject an input type they
            // have not been told the output format for.
            SetOutputType();
            SetInputType();

            var outputInfo = _transform.GetOutputStreamInfo(StreamId);
            MftProvidesOutputSamples = (outputInfo.Flags & OutputSampleProvidedFlags) != 0;

            _inputSamples = new IMFSample[converter.PoolSize];
            _inputBuffers = new IMFMediaBuffer[converter.PoolSize];
            for (var i = 0; i < converter.PoolSize; i++)
            {
                _inputBuffers[i] = CreateSurfaceBuffer(converter.TextureAt(i), settings);
                _inputSamples[i] = MediaFactory.MFCreateSample();
                _inputSamples[i].AddBuffer(_inputBuffers[i]);
            }

            if (!MftProvidesOutputSamples)
            {
                var size = Math.Max(outputInfo.Size, 1 << 20);
                _outputBuffer = MediaFactory.MFCreateMemoryBuffer(size);
                _outputSample = MediaFactory.MFCreateSample();
                _outputSample.AddBuffer(_outputBuffer);
                log.Debug($"Encoder expects caller-allocated output samples ({size} bytes).");
            }

            // Sized for a generous keyframe; grown on demand, which in practice
            // means once, during the first second.
            _copyBuffer = new byte[Math.Max(1 << 20, outputInfo.Size)];

            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
            _streaming = true;

            log.Info(
                $"Encoder started: {Descriptor.Name}, {settings.Codec}, " +
                $"{settings.Width}x{settings.Height}@{settings.Fps}, " +
                $"{settings.EffectiveBitsPerSecond / 1_000_000.0:F1}Mbps, " +
                $"keyframe every {settings.KeyFrameIntervalFrames} frames, " +
                $"{(IsAsync ? "async" : "sync")} MFT.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary><c>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES</c>.</summary>
    private const int OutputSampleProvidedFlags = 0x100;

    internal EncoderDescriptor Descriptor { get; }

    internal bool IsAsync { get; }

    /// <summary>Whether the MFT hands back its own output samples.</summary>
    internal bool MftProvidesOutputSamples { get; }

    internal long SamplesEncoded { get; private set; }

    internal long KeyFramesEncoded { get; private set; }

    internal long BytesEncoded { get; private set; }

    /// <summary>
    /// The encoder's negotiated output media type, for the muxer.
    /// </summary>
    /// <remarks>
    /// Taken from the encoder rather than rebuilt by hand because it carries the
    /// codec private data — H.264's SPS/PPS in
    /// <c>MF_MT_MPEG_SEQUENCE_HEADER</c>. An MP4 written without it has no
    /// decoder configuration record and will not play anywhere.
    /// </remarks>
    internal IMFMediaType GetOutputMediaType() => _transform.GetOutputCurrentType(StreamId);

    /// <summary>
    /// Encodes one converted frame. Blocks until the encoder accepts it, pushing
    /// any finished frames to <paramref name="sink"/> while it waits.
    /// </summary>
    /// <remarks>
    /// Called on the encode thread only. Waiting here is correct: the capture
    /// thread is not blocked — it drops into the queue's backpressure instead —
    /// and the wait is on a GPU completion, not on disk or a lock.
    /// </remarks>
    internal void Encode(int nv12Index, long timestampTicks, long durationTicks, IEncodedSampleSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sample = _inputSamples[nv12Index];
        sample.SampleTime = timestampTicks;
        sample.SampleDuration = durationTicks;

        if (!IsAsync)
        {
            _transform.ProcessInput(StreamId, sample, 0);
            DrainSynchronousOutputs(sink);
            return;
        }

        var delivered = false;
        while (!delivered)
        {
            using var mediaEvent = _events!.GetEvent(0);

            switch (mediaEvent.EventType)
            {
                case MediaEventTypes.TransformNeedInput:
                    _transform.ProcessInput(StreamId, sample, 0);
                    delivered = true;
                    break;

                case MediaEventTypes.TransformHaveOutput:
                    CollectOutput(sink);
                    break;

                case MediaEventTypes.TransformDrainComplete:
                    // Not expected mid-stream; restart streaming so the next
                    // frame is not silently dropped.
                    _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Flushes everything still inside the encoder. Called when a recording
    /// stops, so the last frames before the stop actually reach the file.
    /// </summary>
    internal void Drain(IEncodedSampleSink sink)
    {
        if (_disposed || !_streaming)
        {
            return;
        }

        if (!IsAsync)
        {
            _transform.ProcessMessage(TMessageType.MessageCommandDrain, 0);
            DrainSynchronousOutputs(sink);
            return;
        }

        _transform.ProcessMessage(TMessageType.MessageCommandDrain, 0);

        var draining = true;
        while (draining)
        {
            using var mediaEvent = _events!.GetEvent(0);

            switch (mediaEvent.EventType)
            {
                case MediaEventTypes.TransformHaveOutput:
                    CollectOutput(sink);
                    break;

                case MediaEventTypes.TransformDrainComplete:
                    draining = false;
                    break;

                case MediaEventTypes.TransformNeedInput:
                    // The encoder is draining; there is nothing more to give it.
                    break;

                default:
                    break;
            }
        }

        _log.Debug($"Encoder drained: {SamplesEncoded} samples, {BytesEncoded} bytes.");
    }

    private void DrainSynchronousOutputs(IEncodedSampleSink sink)
    {
        while (true)
        {
            if (!TryProcessOutput(sink))
            {
                return;
            }
        }
    }

    private void CollectOutput(IEncodedSampleSink sink) => TryProcessOutput(sink);

    /// <summary>
    /// Pulls one encoded frame out of the MFT and hands its bytes to the sink.
    /// </summary>
    private bool TryProcessOutput(IEncodedSampleSink sink)
    {
        var buffer = new OutputDataBuffer
        {
            StreamID = StreamId,
            Sample = MftProvidesOutputSamples ? null! : _outputSample!,
            Status = 0,
        };

        var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);

        if (result == MfResult.TransformNeedMoreInput)
        {
            return false;
        }

        if (result == MfResult.TransformStreamChange)
        {
            // The encoder renegotiated its output type — normal on the first
            // frames of some drivers. Re-apply and carry on.
            _log.Debug("Encoder reported an output stream change; re-applying the output type.");
            SetOutputType();
            return false;
        }

        result.CheckError();

        var sample = buffer.Sample;
        if (sample is null)
        {
            return false;
        }

        try
        {
            Publish(sample, sink);
        }
        finally
        {
            buffer.Events?.Dispose();

            if (MftProvidesOutputSamples)
            {
                // The MFT allocated this one; release it now so it stays a
                // short-lived Gen0 object rather than surviving to a later
                // collection.
                sample.Dispose();
            }
            else
            {
                // Reused next time round: reset its length so the encoder writes
                // from the start.
                _outputBuffer!.CurrentLength = 0;
            }
        }

        return true;
    }

    private void Publish(IMFSample sample, IEncodedSampleSink sink)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();

        contiguous.Lock(out var data, out _, out var length);
        try
        {
            if (length <= 0)
            {
                return;
            }

            if (_copyBuffer.Length < length)
            {
                // Grows at most a handful of times, during the first second, and
                // then never again: every subsequent sample fits.
                _copyBuffer = new byte[length * 2];
                _log.Debug($"Encoder copy buffer grown to {_copyBuffer.Length} bytes.");
            }

            Marshal.Copy(data, _copyBuffer, 0, length);

            var isKeyFrame = IsCleanPoint(sample);
            var timestamp = sample.SampleTime;
            var duration = sample.SampleDuration > 0
                ? sample.SampleDuration
                : TimeSpan.TicksPerSecond / _settings.Fps;

            sink.TryWrite(
                new ReadOnlySpan<byte>(_copyBuffer, 0, length),
                timestamp,
                duration,
                isKeyFrame);

            SamplesEncoded++;
            BytesEncoded += length;
            if (isKeyFrame)
            {
                KeyFramesEncoded++;
            }

            _sequence++;
        }
        finally
        {
            contiguous.Unlock();
        }
    }

    private bool IsCleanPoint(IMFSample sample)
    {
        try
        {
            return sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
        {
            // Some encoders omit the attribute. Falling back to the configured
            // GOP structure is approximate but better than never marking a
            // keyframe, which would make clip trimming impossible.
            return _sequence % _settings.KeyFrameIntervalFrames == 0;
        }
    }

    private static bool UnlockAsyncIfNeeded(IMFTransform transform, IEngineLog log)
    {
        using var attributes = transform.Attributes;

        uint isAsync;
        try
        {
            isAsync = attributes.GetUInt32(TransformAttributeKeys.TransformAsync);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
        {
            log.Debug("Encoder does not advertise MF_TRANSFORM_ASYNC; driving it synchronously.");
            return false;
        }

        if (isAsync == 0)
        {
            return false;
        }

        // An async MFT refuses every call until it is unlocked. This is the
        // documented handshake, not a workaround.
        attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        return true;
    }

    private static IMFDXGIDeviceManager CreateDeviceManager(GraphicsDevice device)
    {
        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device.Device).CheckError();
        return manager;
    }

    private IMFMediaBuffer CreateSurfaceBuffer(ID3D11Texture2D texture, EncoderSettings settings)
    {
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
            ComIids.ID3D11Texture2D, texture, 0, false);

        // NV12 is one full-resolution luma plane plus a half-resolution
        // interleaved chroma plane: 1.5 bytes per pixel.
        buffer.CurrentLength = settings.Width * settings.Height * 3 / 2;
        return buffer;
    }

    private void SetOutputType()
    {
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, SubtypeFor(_settings.Codec));
        type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_settings.EffectiveBitsPerSecond);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2); // Progressive
        SetFrameSize(type, _settings.Width, _settings.Height);
        SetRatio(type, MediaTypeAttributeKeys.FrameRate, (uint)_settings.Fps, 1);
        SetRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);

        if (_settings.Codec == VideoCodec.H264)
        {
            // High profile: what every current decoder supports, and noticeably
            // better quality per bit than Main at the same bitrate.
            type.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)100);
        }

        _transform.SetOutputType(StreamId, type, 0);
    }

    private void SetInputType()
    {
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2);
        SetFrameSize(type, _settings.Width, _settings.Height);
        SetRatio(type, MediaTypeAttributeKeys.FrameRate, (uint)_settings.Fps, 1);
        SetRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);

        _transform.SetInputType(StreamId, type, 0);
    }

    private void ConfigureCodecApi()
    {
        if (_codecApi is null)
        {
            return;
        }

        var mode = _settings.RateControl switch
        {
            RateControlMode.ConstantBitrate => RateControlModeValues.Cbr,
            RateControlMode.VariableBitrate => RateControlModeValues.PeakConstrainedVbr,
            RateControlMode.Quality => RateControlModeValues.Quality,
            _ => RateControlModeValues.Cbr,
        };

        _codecApi.TrySetUInt32(CodecApi.RateControlMode, mode, "rate control mode");
        _codecApi.TrySetUInt32(
            CodecApi.MeanBitRate, (uint)_settings.EffectiveBitsPerSecond, "mean bitrate");

        if (_settings.RateControl == RateControlMode.VariableBitrate)
        {
            _codecApi.TrySetUInt32(
                CodecApi.MaxBitRate, (uint)Math.Min(
                    _settings.EffectiveBitsPerSecond * 2, BitrateCalculator.MaximumBitsPerSecond),
                "peak bitrate");
        }

        _codecApi.TrySetUInt32(
            CodecApi.GopSize, (uint)_settings.KeyFrameIntervalFrames, "GOP size");

        if (_settings.LowLatency)
        {
            // Shortens the encoder's lookahead, so the frames immediately before
            // a hotkey press are already encoded rather than still in flight.
            _codecApi.TrySetUInt32(CodecApi.LowLatencyMode, 1, "low latency mode");
        }

        // 0 = favour speed, 100 = favour quality. 70 keeps the encode comfortably
        // inside a 60fps budget on older hardware while still looking good.
        _codecApi.TrySetUInt32(CodecApi.QualityVsSpeed, 70, "quality vs speed");
    }

    private static Guid SubtypeFor(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => VideoFormatGuids.H264,
        VideoCodec.Hevc => VideoFormatGuids.Hevc,
        VideoCodec.Av1 => FourCc.Av1Subtype,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unknown codec."),
    };

    /// <summary>Packs width and height into the single UINT64 media types use.</summary>
    private static void SetFrameSize(IMFMediaType type, int width, int height) =>
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)(uint)width << 32) | (uint)height);

    /// <summary>Packs a numerator/denominator pair into the UINT64 media types use.</summary>
    private static void SetRatio(IMFMediaType type, Guid key, uint numerator, uint denominator) =>
        type.Set(key, ((ulong)numerator << 32) | denominator);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_streaming)
        {
            try
            {
                _transform?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
                _transform?.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
            }
            catch (Exception ex)
            {
                _log.Debug($"Encoder shutdown message failed: {ex.Message}");
            }

            _streaming = false;
        }

        if (_inputSamples is not null)
        {
            foreach (var sample in _inputSamples)
            {
                sample?.Dispose();
            }
        }

        if (_inputBuffers is not null)
        {
            foreach (var buffer in _inputBuffers)
            {
                buffer?.Dispose();
            }
        }

        _outputSample?.Dispose();
        _outputBuffer?.Dispose();
        _codecApi?.Dispose();
        _events?.Dispose();
        _deviceManager?.Dispose();
        _transform?.Dispose();
    }
}
