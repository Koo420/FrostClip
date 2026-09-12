using Frost.Engine.Diagnostics;
using Vortice.MediaFoundation;

namespace Frost.Engine.Windows;

/// <summary>
/// Owns the <c>MFStartup</c>/<c>MFShutdown</c> pair for the process.
/// </summary>
/// <remarks>
/// Media Foundation is reference counted per process and must be started before
/// any MF call and shut down exactly as many times. Doing it once here, rather
/// than per encoder session, avoids the tear-down-and-restart churn that shows
/// up as a stall on the first frame after a clip is saved.
/// </remarks>
internal sealed class MediaFoundationRuntime : IDisposable
{
    private static readonly object Gate = new();
    private static int _refCount;

    private readonly IEngineLog _log;
    private bool _disposed;

    internal MediaFoundationRuntime(IEngineLog log)
    {
        _log = log;

        lock (Gate)
        {
            if (_refCount == 0)
            {
                // false selects the full Media Foundation platform rather than the
                // "lite" subset, which excludes the encoder MFTs we need.
                MediaFactory.MFStartup(false).CheckError();
                log.Debug("Media Foundation started.");
            }

            _refCount++;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (Gate)
        {
            _refCount--;
            if (_refCount == 0)
            {
                MediaFactory.MFShutdown();
                _log.Debug("Media Foundation shut down.");
            }
        }
    }
}
