using Frost.Engine.Audio;
using Frost.Engine.Clips;
using Frost.Engine.Diagnostics;
using Frost.Engine.Recording;
using Frost.Shared.Clips;

namespace Frost.Engine.Hotkeys;

/// <summary>
/// Wires hotkey actions to the Engine's clip service and session recorder.
/// </summary>
/// <remarks>
/// The single place where a keypress becomes a thing that happens, which is also
/// what the IPC server's equivalent commands go through — so a clip saved from
/// the Shell and one saved from a hotkey take the same path and cannot drift
/// apart.
/// </remarks>
public sealed class EngineHotkeyActions : IHotkeyActions
{
    private readonly ClipService _clips;
    private readonly FullSessionRecorder _session;
    private readonly Func<string> _sessionPathFactory;
    private readonly Func<string?> _gameNameProvider;
    private readonly Func<(int Width, int Height, string Codec)> _formatProvider;
    private readonly MicrophoneState? _microphone;
    private readonly IEngineLog _log;

    private long _bookmarks;
    private long _sessionsStarted;

    /// <param name="sessionPathFactory">Produces the path for a new session recording.</param>
    /// <param name="gameNameProvider">Foreground process name, for file naming.</param>
    /// <param name="formatProvider">Current capture geometry and codec, for metadata.</param>
    /// <param name="microphone">
    /// Live microphone state, or null when microphone capture is off. Muting takes
    /// effect on the next audio block rather than on a capture restart.
    /// </param>
    public EngineHotkeyActions(
        ClipService clips,
        FullSessionRecorder session,
        Func<string> sessionPathFactory,
        Func<string?> gameNameProvider,
        Func<(int Width, int Height, string Codec)> formatProvider,
        IEngineLog log,
        MicrophoneState? microphone = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sessionPathFactory);
        ArgumentNullException.ThrowIfNull(gameNameProvider);
        ArgumentNullException.ThrowIfNull(formatProvider);
        ArgumentNullException.ThrowIfNull(log);

        _clips = clips;
        _session = session;
        _sessionPathFactory = sessionPathFactory;
        _gameNameProvider = gameNameProvider;
        _formatProvider = formatProvider;
        _log = log;
        _microphone = microphone;
    }

    /// <summary>Manual bookmarks placed.</summary>
    public long BookmarksPlaced => Interlocked.Read(ref _bookmarks);

    /// <summary>Session recordings started.</summary>
    public long SessionsStarted => Interlocked.Read(ref _sessionsStarted);

    public void SaveClip(TimeSpan duration, string? label) =>
        _clips.Request(new ClipRequest(duration, label, _gameNameProvider()));

    public void ToggleFullSessionRecording()
    {
        if (_session.IsRecording)
        {
            _session.Stop();
            return;
        }

        var (width, height, codec) = _formatProvider();

        if (!_session.TryStart(
                _sessionPathFactory(), width, height, codec, _gameNameProvider(), out var error))
        {
            _log.Warn($"Could not start session recording: {error}");
            return;
        }

        Interlocked.Increment(ref _sessionsStarted);
    }

    /// <summary>
    /// Marks the current moment in the session recording.
    /// </summary>
    /// <remarks>
    /// Deliberately cheap: it appends an offset to the in-memory bookmark list and
    /// nothing else. No file is written, no clip is exported, the ring buffer is
    /// not read. That is the whole point — flagging a moment mid-fight should cost
    /// nothing, and the review happens later.
    /// </remarks>
    public void AddBookmark()
    {
        if (_session.TryAddBookmark(BookmarkSource.Manual, label: null, out var error))
        {
            Interlocked.Increment(ref _bookmarks);
            return;
        }

        // The common case is no session recording running. Worth telling the user
        // once, because pressing bookmark and getting nothing is confusing.
        _log.Info($"Bookmark ignored: {error}");
    }

    public void ToggleMicrophoneMute()
    {
        if (_microphone is null)
        {
            // Telling the user beats silently doing nothing when they press a key
            // and expect a change.
            _log.Info("Microphone capture is off, so there is nothing to mute.");
            return;
        }

        var muted = _microphone.Toggle();
        _log.Info(muted ? "Microphone muted." : "Microphone unmuted.");
    }
}
