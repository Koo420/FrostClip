using System.Diagnostics;
using System.Runtime.InteropServices;
using Frost.Engine.Diagnostics;
using Frost.Engine.Hotkeys;
using Frost.Shared.Hotkeys;

namespace Frost.Engine.Windows;

/// <summary>
/// Global keyboard hook feeding <see cref="HotkeyRouter"/>.
/// </summary>
/// <remarks>
/// <para><b>Why <c>WH_KEYBOARD_LL</c> and not <c>RegisterHotKey</c>.</b>
/// <c>RegisterHotKey</c> delivers through the window message queue, and a
/// fullscreen-exclusive game that owns input focus and does not pump a
/// cooperative message loop can leave those hotkeys simply not arriving — which
/// is the one situation where a clipping app absolutely must work. A low-level
/// hook sits ahead of focus entirely and sees the key regardless.</para>
///
/// <para><b>The callback budget.</b> Every keystroke on the machine passes
/// through this callback — including the ones the user is aiming with — and
/// Windows silently unhooks a callback that exceeds
/// <c>LowLevelHooksTimeout</c> (300ms by default, but the practical budget is
/// microseconds). So the callback reads a few key states, calls into the router,
/// and returns. It never allocates, never locks, never touches disk, and never
/// blocks — the router only queues.</para>
///
/// <para><b>Keys are passed through.</b> <c>CallNextHookEx</c> is always called
/// and the event is never swallowed. A binding may collide with something the
/// game uses, and eating the keystroke would be far worse than an accidental
/// clip.</para>
///
/// <para><b>The hook is not permanent.</b> It can be lost to a timeout, a UAC
/// prompt or a session switch, with no notification. <see cref="HookWatchdog"/>
/// notices and this reinstalls — otherwise the hotkeys stop working silently,
/// which from the user's side looks like the application has given up.</para>
/// </remarks>
internal sealed partial class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;

    private const int WmQuit = 0x0012;

    private readonly HotkeyRouter _router;
    private readonly HookWatchdog _watchdog;
    private readonly IEngineLog _log;
    private readonly HookProc _callback;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private nint _hook;
    private uint _threadId;
    private volatile bool _stopping;
    private long _eventsSeen;

    internal KeyboardHook(HotkeyRouter router, IEngineLog log, HookWatchdog? watchdog = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(log);

        _router = router;
        _log = log;
        _watchdog = watchdog ?? new HookWatchdog();

        // Held in a field for the life of the hook: if the delegate is collected
        // while Windows still holds the function pointer, the next keystroke
        // crashes the process.
        _callback = HookCallback;
    }

    internal bool IsInstalled => _hook != 0;

    internal long EventsSeen => Interlocked.Read(ref _eventsSeen);

    internal long ReinstallCount => _watchdog.ReinstallCount;

    /// <summary>
    /// Installs the hook on a dedicated thread with its own message loop.
    /// </summary>
    /// <remarks>
    /// A low-level hook's callback is dispatched on the thread that installed it,
    /// and only while that thread pumps messages. Giving it a thread of its own
    /// means keystroke handling cannot be delayed behind anything else the
    /// Engine is doing.
    /// </remarks>
    internal void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The keyboard hook is already started.");
        }

        _stopping = false;
        _thread = new Thread(MessageLoop)
        {
            Name = "frost-hotkeys",
            IsBackground = true,

            // Highest is justified here and nowhere else: this thread must never
            // be the reason a keystroke is late, and it does almost no work.
            Priority = ThreadPriority.Highest,
        };

        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("The keyboard hook thread did not start within 5s.");
        }

        if (!IsInstalled)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx(WH_KEYBOARD_LL) failed (Win32 error {Marshal.GetLastWin32Error()}). " +
                "Frost's hotkeys need this to work while a fullscreen game has focus.");
        }
    }

    internal void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopping = true;

        // Wakes the message loop so it can unhook on the same thread that hooked.
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
        }

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _log.Warn("The keyboard hook thread did not stop within 5s.");
        }

        _thread = null;
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();

        if (!Install())
        {
            _ready.Set();
            return;
        }

        _ready.Set();

        // A 1s timer gives the watchdog a heartbeat without polling: MsgWaitFor
        // returns on either a message or the timeout.
        while (!_stopping)
        {
            var result = GetMessageW(out var message, 0, 0, 0);

            if (result == 0 || result == -1)
            {
                break;
            }

            if (message.Message == WmQuit)
            {
                break;
            }

            if (message.Message == WmReinstall)
            {
                // Unhook and rehook on this thread, which is the only thread
                // allowed to own the hook.
                Uninstall();
                if (!Install())
                {
                    _log.Error("Reinstalling the keyboard hook failed; hotkeys are not working.");
                    break;
                }

                continue;
            }

            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        Uninstall();
    }

    private bool Install()
    {
        // Module handle is not required for WH_KEYBOARD_LL with a thread ID of 0,
        // but passing the executable's keeps older Windows builds happy.
        var module = GetModuleHandleW(null);
        _hook = SetWindowsHookExW(WhKeyboardLl, _callback, module, 0);

        if (_hook == 0)
        {
            _log.Error(
                $"SetWindowsHookEx(WH_KEYBOARD_LL) failed (Win32 error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        _watchdog.NoteInstalled(MonotonicTicks());
        _log.Info($"Keyboard hook installed for {_router.Count} hotkeys.");
        return true;
    }

    private void Uninstall()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
            _log.Info($"Keyboard hook removed after {EventsSeen} events.");
        }
    }

    /// <summary>
    /// Checks whether Windows has quietly dropped the hook, and reinstalls if so.
    /// Called from the Engine's slow housekeeping tick, not from the hook.
    /// </summary>
    internal void CheckHealth()
    {
        if (_thread is null || _stopping)
        {
            return;
        }

        var now = MonotonicTicks();

        if (!_watchdog.ShouldReinstall(now, LastSystemInputTicks(now)))
        {
            return;
        }

        _log.Warn("The keyboard hook appears to have been dropped by Windows; reinstalling.");

        // Reinstalling must happen on the thread that owns the hook, so ask the
        // message loop to do it rather than doing it here.
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmReinstall, 0, 0);
        }

        // Recorded before the hook thread acts on the message, so the minimum
        // interval starts now and a slow reinstall cannot queue a second one.
        _watchdog.NoteReinstalled(now);
        _router.ResetHeldState();
    }

    /// <summary>Private message asking the hook thread to reinstall.</summary>
    private const int WmReinstall = 0x0400 + 0x0F01;

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        // Everything in here is on the critical path of every keystroke on the
        // machine. Keep it to arithmetic and a queue.
        if (code != HcAction)
        {
            return CallNextHookEx(0, code, wParam, lParam);
        }

        try
        {
            var message = (int)wParam;
            var virtualKey = Marshal.ReadInt32(lParam);

            switch (message)
            {
                case WmKeyDown or WmSysKeyDown:
                    Interlocked.Increment(ref _eventsSeen);
                    _watchdog.NoteHookEvent(MonotonicTicks());
                    _router.OnKeyDown(virtualKey, CurrentModifiers(), MonotonicTicks());
                    break;

                case WmKeyUp or WmSysKeyUp:
                    _watchdog.NoteHookEvent(MonotonicTicks());
                    _router.OnKeyUp(virtualKey);
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            // An exception escaping a hook callback tears down the process.
            // Whatever went wrong, losing the whole Engine over one keystroke is
            // not an acceptable outcome.
            _log.Error("The keyboard hook callback threw; the keystroke was ignored.", ex);
        }

        // Always pass the key on. Swallowing it would break whatever the game
        // wanted that key for.
        return CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>
    /// Current modifier state. <c>GetAsyncKeyState</c> rather than tracking
    /// key-downs ourselves, because a modifier pressed before the hook was
    /// installed (or during a focus change) would otherwise be invisible.
    /// </summary>
    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;

        if (IsDown(VkControl))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsDown(VkMenu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsDown(VkShift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsDown(VkLeftWindows) || IsDown(VkRightWindows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static long MonotonicTicks()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var frequency = Stopwatch.Frequency;

        if (frequency == TimeSpan.TicksPerSecond)
        {
            return timestamp;
        }

        var seconds = timestamp / frequency;
        var remainder = timestamp % frequency;
        return (seconds * TimeSpan.TicksPerSecond) + (remainder * TimeSpan.TicksPerSecond / frequency);
    }

    /// <summary>
    /// When Windows last saw any input, on the same monotonic scale as
    /// <see cref="MonotonicTicks"/>.
    /// </summary>
    private static long LastSystemInputTicks(long nowTicks)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };

        if (!GetLastInputInfo(ref info))
        {
            // Cannot tell; report "just now" so the watchdog does not act on it.
            return nowTicks;
        }

        var idleMilliseconds = unchecked((uint)Environment.TickCount - info.TickCount);
        return nowTicks - (idleMilliseconds * TimeSpan.TicksPerMillisecond);
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint SetWindowsHookExW(int idHook, HookProc callback, nint module, uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out Msg message, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref Msg message);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);
}
