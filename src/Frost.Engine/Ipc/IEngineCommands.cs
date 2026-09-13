using Frost.Shared.Clips;
using Frost.Shared.Ipc;
using Frost.Shared.Settings;

namespace Frost.Engine.Ipc;

/// <summary>
/// What the Shell can ask the Engine to do.
/// </summary>
/// <remarks>
/// The seam between the IPC transport and the Engine itself. Implemented by the
/// Engine host; faked in tests, which is how the whole request/response surface
/// is exercised without a capture device.
/// <para>
/// Implementations are called on the IPC thread and must not block on it for
/// long: queue work and return. Saving a clip, for instance, returns as soon as
/// the request is queued — the Shell hears about the finished file through a
/// <see cref="IpcMessageKind.ClipSaved"/> notification.
/// </para>
/// </remarks>
public interface IEngineCommands
{
    EngineStatus GetStatus();

    /// <summary>
    /// Queues a clip. Returns false with a reason when the request cannot be
    /// accepted at all (nothing buffered yet, queue full).
    /// </summary>
    bool TryRequestClip(TimeSpan duration, string? label, out string? error);

    /// <summary>Starts or stops full-session recording. Returns the new state.</summary>
    bool ToggleFullSessionRecording(out string? error);

    /// <summary>Marks the current moment in the session recording.</summary>
    bool TryAddBookmark(out string? error);

    FrostSettings GetSettings();

    /// <summary>
    /// Applies new settings, restarting whatever they changed. Returns false with
    /// a reason if they could not be applied.
    /// </summary>
    bool TryApplySettings(FrostSettings settings, out string? error);

    IReadOnlyList<DisplayDescriptor> ListDisplays();

    IReadOnlyList<WindowDescriptor> ListWindows();

    IReadOnlyList<ClipMetadata> ListClips();
}
