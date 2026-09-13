using Frost.Engine.Diagnostics;
using Frost.Shared.Settings;

namespace Frost.Engine.Hotkeys;

/// <summary>What a hotkey ultimately does.</summary>
/// <remarks>
/// Called on the dispatcher's worker thread, never on the keyboard hook, so
/// implementations may touch the disk.
/// </remarks>
public interface IHotkeyActions
{
    /// <summary>Save the trailing <paramref name="duration"/> from the ring buffer.</summary>
    void SaveClip(TimeSpan duration, string? label);

    /// <summary>Start or stop recording the whole session.</summary>
    void ToggleFullSessionRecording();

    /// <summary>Mark this moment in the session recording. No export.</summary>
    void AddBookmark();

    /// <summary>Mute or unmute the microphone track.</summary>
    void ToggleMicrophoneMute();
}

/// <summary>
/// Moves hotkey actions off the keyboard hook and onto a worker thread.
/// </summary>
/// <remarks>
/// <para>This exists because of where <see cref="HotkeyRouter.Fired"/> is raised:
/// inside the low-level keyboard hook, on the critical path of every keystroke on
/// the machine. Starting a session recording opens a file; saving a clip touches
/// a directory. Doing either there would delay the user's keystroke reaching the
/// game, and a hook callback that overruns is silently removed by Windows.</para>
///
/// <para>So the hook side does the least possible: copy a small struct into a
/// pre-allocated ring and signal. Everything with a cost happens on
/// <c>frost-hotkey-actions</c>.</para>
///
/// <para>The queue is small on purpose. A hotkey backlog deeper than this means
/// keys are being held or spammed, and dropping the extras is better than working
/// through a minute-old queue of toggles.</para>
/// </remarks>
public sealed class HotkeyActionDispatcher : IDisposable
{
    /// <summary>Queued actions allowed before extras are dropped.</summary>
    public const int QueueCapacity = 16;

    private readonly IHotkeyActions _actions;
    private readonly IEngineLog _log;
    private readonly HotkeyRouter _router;
    private readonly Queued[] _queue = new Queued[QueueCapacity];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly Thread _thread;

    private int _count;
    private long _dispatched;
    private long _dropped;
    private long _failed;
    private volatile bool _stopping;
    private bool _disposed;

    public HotkeyActionDispatcher(HotkeyRouter router, IHotkeyActions actions, IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(log);

        _router = router;
        _actions = actions;
        _log = log;

        _router.Fired += OnFired;

        _thread = new Thread(WorkLoop)
        {
            Name = "frost-hotkey-actions",
            IsBackground = true,

            // Normal: these actions are user-initiated and short, and must not
            // compete with capture, encode or the game.
            Priority = ThreadPriority.Normal,
        };

        _thread.Start();
    }

    /// <summary>Actions actually executed.</summary>
    public long Dispatched => Interlocked.Read(ref _dispatched);

    /// <summary>Actions dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Actions whose handler threw.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Waits for the queue to drain. For shutdown and for tests.</summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (Pending == 0 && !_busy)
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return Pending == 0 && !_busy;
    }

    private volatile bool _busy;

    /// <summary>
    /// Hook-thread entry point. Copies a struct and signals; nothing else.
    /// </summary>
    private void OnFired(HotkeyFired fired)
    {
        if (_stopping)
        {
            return;
        }

        var assignment = fired.Assignment;

        lock (_gate)
        {
            if (_count == _queue.Length)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            // fired.Label, not assignment.Label: the default presets set no
            // explicit label, and passing null loses the "(30s)" suffix that tells
            // two clips from the same second apart. The router precomputed it, so
            // reading it here does not allocate.
            _queue[_count++] = new Queued(
                assignment.Action,
                assignment.ClipDuration ?? TimeSpan.Zero,
                fired.Label);
        }

        _work.Release();
    }

    private void WorkLoop()
    {
        while (true)
        {
            _work.Wait();

            while (TryDequeue(out var queued))
            {
                _busy = true;
                try
                {
                    Execute(queued);
                    Interlocked.Increment(ref _dispatched);
                }
                catch (Exception ex)
                {
                    // A failed action must not take the dispatcher down: the next
                    // hotkey press has to still work.
                    Interlocked.Increment(ref _failed);
                    _log.Error($"The {queued.Action} hotkey action failed.", ex);
                }
                finally
                {
                    _busy = false;
                }
            }

            if (_stopping)
            {
                return;
            }
        }
    }

    private bool TryDequeue(out Queued queued)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                queued = default;
                return false;
            }

            queued = _queue[0];

            for (var i = 1; i < _count; i++)
            {
                _queue[i - 1] = _queue[i];
            }

            _queue[--_count] = default;
            return true;
        }
    }

    private void Execute(in Queued queued)
    {
        switch (queued.Action)
        {
            case HotkeyAction.SaveClip:
                _actions.SaveClip(queued.Duration, queued.Label);
                break;

            case HotkeyAction.ToggleFullSessionRecording:
                _actions.ToggleFullSessionRecording();
                break;

            case HotkeyAction.Bookmark:
                _actions.AddBookmark();
                break;

            case HotkeyAction.ToggleMicrophoneMute:
                _actions.ToggleMicrophoneMute();
                break;

            default:
                _log.Warn($"No handler for the {queued.Action} hotkey action.");
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _router.Fired -= OnFired;

        WaitForIdle(TimeSpan.FromSeconds(10));

        _stopping = true;
        _work.Release();
        _thread.Join(TimeSpan.FromSeconds(5));
        _work.Dispose();
    }

    /// <summary>A queued action. A struct, so queuing allocates nothing.</summary>
    private readonly record struct Queued(HotkeyAction Action, TimeSpan Duration, string? Label);
}
