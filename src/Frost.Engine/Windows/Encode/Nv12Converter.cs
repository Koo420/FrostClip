using Frost.Engine.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Frost.Engine.Windows.Encode;

/// <summary>
/// Converts captured BGRA frames to the NV12 the hardware encoder wants, on the
/// GPU's video processor.
/// </summary>
/// <remarks>
/// The conversion has to happen somewhere, and the only acceptable place is the
/// GPU. Letting the Sink Writer insert a converter risks getting Microsoft's
/// <i>software</i> Color Converter MFT, which would put a per-pixel colour
/// conversion on the CPU for every frame — precisely the cost this app exists to
/// avoid. <c>ID3D11VideoContext::VideoProcessorBlt</c> runs on the same fixed
/// function hardware the display pipeline uses.
/// <para>
/// Every allocation is up front: NV12 textures, input views for the capture
/// pool's textures, output views for the NV12 textures, and the one-element
/// stream array the blt takes. Per frame the converter does an array-index
/// lookup and one blt.
/// </para>
/// </remarks>
internal sealed class Nv12Converter : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly IEngineLog _log;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;

    private readonly ID3D11Texture2D[] _nv12Textures;
    private readonly ID3D11VideoProcessorOutputView[] _outputViews;

    // Input views are keyed by the source texture pointer: the capture pool is a
    // fixed handful of textures, so after the first frames this never grows.
    private readonly nint[] _inputHandles;
    private readonly ID3D11VideoProcessorInputView?[] _inputViews;

    private readonly VideoProcessorStream[] _streams = new VideoProcessorStream[1];

    private int _next;

    internal Nv12Converter(
        GraphicsDevice device,
        int width,
        int height,
        int fps,
        int poolSize,
        int expectedSourceTextures,
        IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(log);

        if (poolSize < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(poolSize), poolSize,
                "At least two NV12 textures are needed so conversion and encode never contend for one.");
        }

        _device = device;
        _log = log;
        Width = width;
        Height = height;

        _videoDevice = device.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.Context.QueryInterface<ID3D11VideoContext>();

        var description = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            InputFrameRate = new Rational((uint)fps, 1),
            OutputFrameRate = new Rational((uint)fps, 1),

            // Speed, not quality: there is no scaling or deinterlacing to do, and
            // this runs inside every frame's budget.
            Usage = VideoUsage.OptimalSpeed,
        };

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(description);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        ConfigureColorSpaces();

        _nv12Textures = new ID3D11Texture2D[poolSize];
        _outputViews = new ID3D11VideoProcessorOutputView[poolSize];

        for (var i = 0; i < poolSize; i++)
        {
            _nv12Textures[i] = device.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.NV12,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,

                // RenderTarget is required for a video processor output view;
                // ShaderResource lets the encoder MFT sample it directly.
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });

            _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(
                _nv12Textures[i],
                _enumerator,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                });
        }

        _inputHandles = new nint[expectedSourceTextures + 2];
        _inputViews = new ID3D11VideoProcessorInputView?[_inputHandles.Length];

        log.Debug($"NV12 converter ready: {width}x{height}, {poolSize} output textures.");
    }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>NV12 textures, for the encoder to bind its input samples to.</summary>
    internal ID3D11Texture2D TextureAt(int index) => _nv12Textures[index];

    internal int PoolSize => _nv12Textures.Length;

    /// <summary>
    /// Converts a captured BGRA texture into the next NV12 texture and returns
    /// its index. Allocation-free once the input-view cache is warm.
    /// </summary>
    internal int Convert(ID3D11Texture2D source)
    {
        var index = _next;
        _next = index + 1 == _nv12Textures.Length ? 0 : index + 1;

        var inputView = ResolveInputView(source);

        _streams[0] = new VideoProcessorStream
        {
            Enable = true,
            InputSurface = inputView,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
        };

        _device.EnterDeviceLock();
        try
        {
            _videoContext.VideoProcessorBlt(_processor, _outputViews[index], 0, 1, _streams);
        }
        finally
        {
            _device.LeaveDeviceLock();
        }

        return index;
    }

    private ID3D11VideoProcessorInputView ResolveInputView(ID3D11Texture2D source)
    {
        var handle = source.NativePointer;

        for (var i = 0; i < _inputHandles.Length; i++)
        {
            if (_inputHandles[i] == handle)
            {
                return _inputViews[i]!;
            }
        }

        for (var i = 0; i < _inputHandles.Length; i++)
        {
            if (_inputHandles[i] == 0)
            {
                var view = _videoDevice.CreateVideoProcessorInputView(
                    source,
                    _enumerator,
                    new VideoProcessorInputViewDescription
                    {
                        FourCC = 0,
                        ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    });

                _inputHandles[i] = handle;
                _inputViews[i] = view;
                return view;
            }
        }

        // More distinct source textures than the capture pool should ever produce.
        // Rebuild rather than grow: this is a sign the pool was recreated.
        _log.Debug("NV12 input view cache full; rebuilding.");
        ReleaseInputViews();
        return ResolveInputView(source);
    }

    /// <summary>
    /// Tells the video processor what it is reading and writing.
    /// </summary>
    /// <remarks>
    /// Screen capture is full-range RGB. Video files are conventionally limited
    /// range BT.709 YCbCr, and players assume that, so getting this wrong shows
    /// up as washed-out or crushed blacks in every clip.
    /// </remarks>
    private void ConfigureColorSpaces()
    {
        var input = new VideoProcessorColorSpace
        {
            Usage = 0,          // video processing, not playback scaling
            RGB_Range = 0,      // 0 = full range (0-255), which is what the desktop is
            YCbCr_Matrix = 1,   // BT.709
            YCbCr_xvYCC = 0,
            Nominal_Range = 2,  // 0-255
        };

        var output = new VideoProcessorColorSpace
        {
            Usage = 0,
            RGB_Range = 0,
            YCbCr_Matrix = 1,   // BT.709, matching what we write into the MP4
            YCbCr_xvYCC = 0,
            Nominal_Range = 1,  // 16-235 studio range
        };

        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, input);
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, output);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);

        // No frame-rate conversion: one input frame in, one output frame out.
        _videoContext.VideoProcessorSetStreamOutputRate(
            _processor, 0, VideoProcessorOutputRate.Normal, false, null);
    }

    private void ReleaseInputViews()
    {
        for (var i = 0; i < _inputHandles.Length; i++)
        {
            _inputViews[i]?.Dispose();
            _inputViews[i] = null;
            _inputHandles[i] = 0;
        }
    }

    public void Dispose()
    {
        ReleaseInputViews();

        foreach (var view in _outputViews)
        {
            view?.Dispose();
        }

        foreach (var texture in _nv12Textures)
        {
            texture?.Dispose();
        }

        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
