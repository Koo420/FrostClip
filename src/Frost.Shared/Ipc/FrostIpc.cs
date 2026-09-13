namespace Frost.Shared.Ipc;

/// <summary>
/// Constants for the Engine↔Shell channel.
/// </summary>
/// <remarks>
/// A named pipe carrying small JSON messages. No frame data ever crosses this
/// boundary — the Shell never touches a captured or encoded frame, which is what
/// lets it be closed, hung or killed without affecting a recording.
/// </remarks>
public static class FrostIpc
{
    /// <summary>
    /// Pipe name. Not fully qualified: <see cref="System.IO.Pipes"/> prepends
    /// <c>\\.\pipe\</c> on Windows and uses a socket under the temp directory
    /// elsewhere.
    /// </summary>
    public const string PipeName = "Frost.Engine.v1";

    /// <summary>
    /// Protocol version, sent on every message.
    /// </summary>
    /// <remarks>
    /// The Engine and Shell are separate executables and an MSIX update can
    /// briefly leave a new Shell talking to an old Engine (or the reverse, if the
    /// user has the Engine pinned in the tray across an update). A mismatch has
    /// to produce a clear "restart Frost" rather than a confusing parse error.
    /// </remarks>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Largest frame accepted, in bytes.
    /// </summary>
    /// <remarks>
    /// Messages here are status pings and settings objects — kilobytes. The cap
    /// exists so a corrupt or hostile length prefix cannot make either process
    /// allocate an arbitrary buffer.
    /// </remarks>
    public const int MaxFrameBytes = 1 * 1024 * 1024;

    /// <summary>
    /// How long a client waits for the Engine's pipe before giving up.
    /// </summary>
    /// <remarks>
    /// Generous enough to survive a busy moment — WinUI startup does a lot of
    /// work, and a connect that fails because the Shell's own thread pool was
    /// briefly saturated would look to the user like the Engine is not running.
    /// Short enough that "Engine not running" still appears promptly, and
    /// <see cref="IpcClient.TryConnectAsync"/> exists for callers that must not
    /// wait at all.
    /// </remarks>
    public static TimeSpan ConnectTimeout => TimeSpan.FromSeconds(5);

    /// <summary>How long a request waits for its response.</summary>
    public static TimeSpan RequestTimeout => TimeSpan.FromSeconds(10);
}
