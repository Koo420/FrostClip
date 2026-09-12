namespace Frost.Engine.Capture;

/// <summary>
/// A running capture. One implementation on Windows (WGC); fakes in tests.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Pixel dimensions currently being produced.</summary>
    (int Width, int Height) FrameSize { get; }

    /// <summary>Frames handed to the sink, including fillers.</summary>
    long FramesEmitted { get; }

    /// <summary>
    /// Frames captured but thrown away, either because they arrived faster than
    /// <see cref="CaptureConfiguration.TargetFps"/> or because the sink refused
    /// them / no pool slot was free.
    /// </summary>
    long FramesDropped { get; }

    bool IsRunning { get; }

    /// <summary>Starts capture on its own dedicated thread.</summary>
    void Start(IFrameSink sink);

    /// <summary>Stops capture and joins the capture thread.</summary>
    void Stop();
}
