using System.Text.Json;
using System.Text.Json.Serialization;
using Frost.Shared.Clips;
using Frost.Shared.Settings;

namespace Frost.Shared.Ipc;

/// <summary>What a message is.</summary>
public enum IpcMessageKind
{
    Unknown = 0,

    // ---- Shell → Engine requests ----
    GetStatus = 1,
    RequestClip = 2,
    ToggleFullSessionRecording = 3,
    AddBookmark = 4,
    GetSettings = 5,
    ApplySettings = 6,
    ListDisplays = 7,
    ListWindows = 8,
    ListClips = 9,

    // ---- Engine → Shell replies ----
    Status = 100,
    Acknowledged = 101,
    Failed = 102,
    Settings = 103,
    Displays = 104,
    Windows = 105,
    Clips = 106,

    // ---- Engine → Shell notifications (unsolicited) ----
    ClipSaved = 200,
    ClipFailed = 201,
    HotkeyFired = 202,
    RecordingStateChanged = 203,
    EngineStopping = 204,
}

/// <summary>A capture-able display, as the Shell sees it.</summary>
public sealed record DisplayDescriptor(
    string DeviceName,
    string FriendlyName,
    int Width,
    int Height,
    bool IsPrimary);

/// <summary>A capture-able window, as the Shell sees it.</summary>
public sealed record WindowDescriptor(long Handle, string Title, string ProcessName);

/// <summary>What the Engine is doing right now.</summary>
public sealed record EngineStatus
{
    /// <summary>The ring buffer is running, so a hotkey would produce a clip.</summary>
    public bool IsArmed { get; init; }

    /// <summary>A full-session recording is in progress.</summary>
    public bool IsRecordingSession { get; init; }

    /// <summary>Seconds of gameplay currently held in the ring buffer.</summary>
    public double BufferedSeconds { get; init; }

    /// <summary>Longest clip the buffer can currently produce.</summary>
    public double MaxClipSeconds { get; init; }

    /// <summary>Ring-buffer memory in use.</summary>
    public long BufferBytes { get; init; }

    public int CaptureWidth { get; init; }

    public int CaptureHeight { get; init; }

    public int CaptureFps { get; init; }

    /// <summary>Friendly name of the hardware encoder in use.</summary>
    public string? EncoderName { get; init; }

    public string? Codec { get; init; }

    public long BitsPerSecond { get; init; }

    public long FramesCaptured { get; init; }

    public long FramesDropped { get; init; }

    public long ClipsSaved { get; init; }

    /// <summary>Path of the session recording in progress, if any.</summary>
    public string? SessionFilePath { get; init; }

    /// <summary>Engine uptime in seconds.</summary>
    public double UptimeSeconds { get; init; }

    /// <summary>
    /// Set when the Engine is running but cannot record — no hardware encoder,
    /// capture target gone, disk full. The Shell shows this instead of a status.
    /// </summary>
    public string? FaultMessage { get; init; }
}

/// <summary>Ask the Engine to save a clip.</summary>
public sealed record ClipRequestPayload(double Seconds, string? Label = null);

/// <summary>A hotkey fired, so the Shell can reflect it.</summary>
public sealed record HotkeyFiredPayload(string Binding, string Action, string? Label = null);

/// <summary>
/// One message on the wire.
/// </summary>
/// <remarks>
/// <para>A single envelope with optional payloads rather than a polymorphic
/// hierarchy: polymorphic JSON needs type discriminators and is awkward under
/// AOT, and this channel carries a dozen message shapes, not a hundred.</para>
///
/// <para>Every payload is nullable and nothing relies on a property initialiser,
/// because System.Text.Json's source generator does not run them — an absent
/// field arrives as <see langword="null"/>, not as a default. <see cref="Validate"/>
/// is what turns a wire message into something the rest of the code can trust.</para>
/// </remarks>
public sealed record IpcMessage
{
    public IpcMessageKind Kind { get; init; }

    public int Version { get; init; } = FrostIpc.ProtocolVersion;

    /// <summary>
    /// Echoed back on the reply so a client can match them up. Zero for
    /// unsolicited notifications.
    /// </summary>
    public long CorrelationId { get; init; }

    public EngineStatus? Status { get; init; }

    public ClipRequestPayload? ClipRequest { get; init; }

    public ClipMetadata? Clip { get; init; }

    public List<ClipMetadata>? Clips { get; init; }

    public FrostSettings? Settings { get; init; }

    public List<DisplayDescriptor>? Displays { get; init; }

    public List<WindowDescriptor>? Windows { get; init; }

    public HotkeyFiredPayload? Hotkey { get; init; }

    /// <summary>Set on <see cref="IpcMessageKind.Failed"/> and <see cref="IpcMessageKind.ClipFailed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Set on <see cref="IpcMessageKind.RecordingStateChanged"/>.</summary>
    public bool? IsRecordingSession { get; init; }

    /// <summary>True for a message the Shell sent and expects a reply to.</summary>
    public bool IsRequest => (int)Kind is >= 1 and < 100;

    /// <summary>True for a reply to a request.</summary>
    public bool IsReply => (int)Kind is >= 100 and < 200;

    /// <summary>True for an unsolicited Engine → Shell message.</summary>
    public bool IsNotification => (int)Kind >= 200;

    /// <summary>
    /// Checks the message is coherent, returning a reason when it is not.
    /// </summary>
    /// <remarks>
    /// Both ends validate. The Engine acts on what the Shell sends (saves files,
    /// changes settings), so it cannot assume the payload for a kind is present
    /// just because the kind says it should be — a truncated or hand-crafted
    /// message must produce a clear <see cref="IpcMessageKind.Failed"/>, not a
    /// <see cref="NullReferenceException"/> on a background thread.
    /// </remarks>
    public bool Validate(out string? reason)
    {
        if (Kind == IpcMessageKind.Unknown || !Enum.IsDefined(typeof(IpcMessageKind), Kind))
        {
            reason = $"Unknown message kind {(int)Kind}.";
            return false;
        }

        if (Version != FrostIpc.ProtocolVersion)
        {
            reason =
                $"Protocol version {Version} does not match this build's {FrostIpc.ProtocolVersion}. " +
                "Restart Frost so the Engine and the window are the same version.";
            return false;
        }

        switch (Kind)
        {
            case IpcMessageKind.RequestClip when ClipRequest is null:
                reason = "RequestClip has no clip request payload.";
                return false;

            case IpcMessageKind.RequestClip when ClipRequest.Seconds is <= 0 or > 1800:
                reason = $"A clip of {ClipRequest.Seconds}s is not a length Frost can save.";
                return false;

            case IpcMessageKind.ApplySettings when Settings is null:
                reason = "ApplySettings has no settings payload.";
                return false;

            case IpcMessageKind.Status when Status is null:
                reason = "Status reply has no status payload.";
                return false;

            case IpcMessageKind.Settings when Settings is null:
                reason = "Settings reply has no settings payload.";
                return false;

            case IpcMessageKind.Failed when string.IsNullOrEmpty(Error):
                reason = "Failed reply has no error message.";
                return false;

            case IpcMessageKind.ClipSaved when Clip is null:
                reason = "ClipSaved has no clip metadata.";
                return false;

            case IpcMessageKind.HotkeyFired when Hotkey is null:
                reason = "HotkeyFired has no hotkey payload.";
                return false;

            default:
                reason = null;
                return true;
        }
    }

    // ---- Construction helpers, so call sites stay readable ----

    public static IpcMessage Request(IpcMessageKind kind, long correlationId) =>
        new() { Kind = kind, CorrelationId = correlationId };

    public static IpcMessage ClipRequestFor(double seconds, string? label, long correlationId) =>
        new()
        {
            Kind = IpcMessageKind.RequestClip,
            CorrelationId = correlationId,
            ClipRequest = new ClipRequestPayload(seconds, label),
        };

    public static IpcMessage Ack(long correlationId) =>
        new() { Kind = IpcMessageKind.Acknowledged, CorrelationId = correlationId };

    public static IpcMessage Failure(string error, long correlationId) =>
        new() { Kind = IpcMessageKind.Failed, CorrelationId = correlationId, Error = error };

    public static IpcMessage StatusReply(EngineStatus status, long correlationId) =>
        new() { Kind = IpcMessageKind.Status, CorrelationId = correlationId, Status = status };

    public static IpcMessage SettingsReply(FrostSettings settings, long correlationId) =>
        new() { Kind = IpcMessageKind.Settings, CorrelationId = correlationId, Settings = settings };

    public static IpcMessage ClipSavedNotification(ClipMetadata clip) =>
        new() { Kind = IpcMessageKind.ClipSaved, Clip = clip };

    public static IpcMessage ClipFailedNotification(string error) =>
        new() { Kind = IpcMessageKind.ClipFailed, Error = error };

    public static IpcMessage HotkeyFiredNotification(string binding, string action, string? label) =>
        new()
        {
            Kind = IpcMessageKind.HotkeyFired,
            Hotkey = new HotkeyFiredPayload(binding, action, label),
        };

    public static IpcMessage RecordingStateNotification(bool isRecording) =>
        new() { Kind = IpcMessageKind.RecordingStateChanged, IsRecordingSession = isRecording };
}

/// <summary>Source-generated JSON for the IPC channel. See the note in FrostClipJsonContext.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcMessage))]
public partial class FrostIpcJsonContext : JsonSerializerContext;
