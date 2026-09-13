using System.Runtime.InteropServices;
using Frost.Engine.Diagnostics;
using Frost.Engine.Tray;
using Frost.Shared.Ipc;

namespace Frost.Engine.Windows.Tray;

/// <summary>
/// The Engine's notification-area icon.
/// </summary>
/// <remarks>
/// <para>Runs its own thread with a message-only window, because
/// <c>Shell_NotifyIcon</c> delivers its callbacks as window messages and needs a
/// thread that pumps them. That thread is separate from the keyboard hook's: the
/// hook must never wait behind a menu the user has left open, and a popup menu
/// blocks its thread for as long as it is on screen.</para>
///
/// <para>Cost when nobody is looking at it: the thread sits in
/// <c>GetMessage</c>, which is a kernel wait, and the icon is only touched when
/// its tooltip actually changes. The menu is built on demand, so the state it
/// shows cannot be stale.</para>
///
/// <para>The icon is re-added when Explorer restarts (the
/// <c>TaskbarCreated</c> broadcast). Without that, an Explorer crash silently
/// loses the only way to reach the app.</para>
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmQuit = 0x0012;
    private const int WmCommand = 0x0111;
    private const int WmRightButtonUp = 0x0205;
    private const int WmLeftButtonDoubleClick = 0x0203;

    /// <summary>Our private message for the tray callback.</summary>
    private const int WmTrayCallback = 0x0400 + 1;

    /// <summary>Our private message asking the thread to refresh the tooltip.</summary>
    private const int WmRefresh = 0x0400 + 2;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfGrayed = 0x00000001;
    private const uint MfDefault = 0x00001000;

    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private const int IdiApplication = 32512;

    /// <summary>First menu command id. Zero means "nothing chosen".</summary>
    private const int FirstCommandId = 100;

    private readonly IEngineLog _log;
    private readonly Func<EngineStatus?> _statusProvider;
    private readonly Func<bool> _hasClipFolder;
    private readonly Action<TrayCommand> _onCommand;
    private readonly WndProc _wndProc;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private nint _window;
    private nint _icon;
    private uint _threadId;
    private uint _taskbarCreatedMessage;
    private string _tooltip = string.Empty;
    private IReadOnlyList<TrayMenuItem> _currentMenu = [];
    private volatile bool _stopping;
    private bool _disposed;

    internal TrayIcon(
        Func<EngineStatus?> statusProvider,
        Func<bool> hasClipFolder,
        Action<TrayCommand> onCommand,
        IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(statusProvider);
        ArgumentNullException.ThrowIfNull(hasClipFolder);
        ArgumentNullException.ThrowIfNull(onCommand);
        ArgumentNullException.ThrowIfNull(log);

        _statusProvider = statusProvider;
        _hasClipFolder = hasClipFolder;
        _onCommand = onCommand;
        _log = log;

        // Held for the window's lifetime: if the delegate is collected while
        // Windows still holds the pointer, the next message crashes the process.
        _wndProc = WindowProcedure;
    }

    internal bool IsVisible => _window != 0;

    /// <summary>Commands the user has chosen, for diagnostics.</summary>
    internal long CommandsInvoked { get; private set; }

    internal void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The tray icon is already started.");
        }

        _stopping = false;
        _thread = new Thread(MessageLoop)
        {
            Name = "frost-tray",
            IsBackground = true,

            // Below normal: a menu the user left open must never delay capture,
            // encode or the keyboard hook.
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("The tray thread did not start within 5s.");
        }

        if (!IsVisible)
        {
            throw new InvalidOperationException(
                $"The tray icon could not be created (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>
    /// Refreshes the tooltip if the state it describes has changed.
    /// </summary>
    /// <remarks>
    /// Cheap to call on a status tick: it compares strings and only touches the
    /// shell when the text actually differs, so an idle Engine does no shell work
    /// at all.
    /// </remarks>
    internal void Refresh()
    {
        if (_window == 0 || _stopping)
        {
            return;
        }

        var tooltip = TrayMenuModel.BuildTooltip(_statusProvider());

        if (string.Equals(tooltip, _tooltip, StringComparison.Ordinal))
        {
            return;
        }

        _tooltip = tooltip;

        // Posted rather than applied here: Shell_NotifyIcon must be called from
        // the thread that owns the window.
        PostMessageW(_window, WmRefresh, 0, 0);
    }

    internal void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopping = true;

        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
        }

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _log.Warn("The tray thread did not stop within 5s.");
        }

        _thread = null;
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();

        if (!CreateWindowAndIcon())
        {
            _ready.Set();
            return;
        }

        _ready.Set();

        while (!_stopping)
        {
            var result = GetMessageW(out var message, 0, 0, 0);

            if (result is 0 or -1)
            {
                break;
            }

            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        RemoveIcon();
    }

    private bool CreateWindowAndIcon()
    {
        var className = "FrostTrayWindow";
        var moduleHandle = GetModuleHandleW(null);

        var windowClass = new WndClass
        {
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = moduleHandle,
            LpszClassName = className,
        };

        // A class name already in use is fine: a previous instance in this process
        // registered it.
        RegisterClassW(ref windowClass);

        // HWND_MESSAGE: a window with no presence on screen or in the taskbar,
        // which is all Shell_NotifyIcon needs.
        _window = CreateWindowExW(
            0, className, "Frost", 0, 0, 0, 0, 0, HwndMessage, 0, moduleHandle, 0);

        if (_window == 0)
        {
            _log.Error($"CreateWindowEx for the tray failed (Win32 error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        // Explorer broadcasts this when it restarts; without handling it, an
        // Explorer crash silently loses the only way to reach the app.
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        _icon = LoadIconW(0, IdiApplication);
        _tooltip = TrayMenuModel.BuildTooltip(_statusProvider());

        return AddIcon();
    }

    private bool AddIcon()
    {
        var data = BuildIconData();

        if (!Shell_NotifyIconW(NimAdd, ref data))
        {
            _log.Error($"Shell_NotifyIcon(ADD) failed (Win32 error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        _log.Info("Tray icon added.");
        return true;
    }

    private void RemoveIcon()
    {
        if (_window == 0)
        {
            return;
        }

        var data = BuildIconData();
        Shell_NotifyIconW(NimDelete, ref data);

        DestroyWindow(_window);
        _window = 0;
        _log.Info("Tray icon removed.");
    }

    private NotifyIconData BuildIconData() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = WmTrayCallback,
        Icon = _icon,
        Tip = _tooltip,
    };

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
            {
                AddIcon();
                return 0;
            }

            switch (message)
            {
                case WmTrayCallback:
                    HandleTrayCallback((int)lParam);
                    return 0;

                case WmCommand:
                    HandleCommand((int)(wParam & 0xFFFF));
                    return 0;

                case WmRefresh:
                {
                    var data = BuildIconData();
                    Shell_NotifyIconW(NimModify, ref data);
                    return 0;
                }

                case WmClose:
                case WmDestroy:
                    return 0;

                default:
                    return DefWindowProcW(window, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // An exception escaping a window procedure tears down the process, and
            // losing the Engine because a menu misbehaved is not acceptable.
            _log.Error("The tray window procedure threw.", ex);
            return 0;
        }
    }

    private void HandleTrayCallback(int message)
    {
        switch (message)
        {
            case WmRightButtonUp:
                ShowMenu();
                break;

            case WmLeftButtonDoubleClick:
                Invoke(TrayCommand.OpenGallery);
                break;

            default:
                break;
        }
    }

    private void ShowMenu()
    {
        // Built on demand, so what it shows cannot be stale.
        _currentMenu = TrayMenuModel.Build(_statusProvider(), _hasClipFolder());

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            for (var i = 0; i < _currentMenu.Count; i++)
            {
                var item = _currentMenu[i];

                if (item.IsSeparator)
                {
                    AppendMenuW(menu, MfSeparator, 0, null);
                    continue;
                }

                var flags = MfString;

                if (!item.IsEnabled)
                {
                    flags |= MfGrayed;
                }

                if (item.IsDefault)
                {
                    flags |= MfDefault;
                }

                AppendMenuW(menu, flags, (nuint)(FirstCommandId + i), item.Text);
            }

            // Required before TrackPopupMenu, or the menu does not dismiss when
            // the user clicks elsewhere.
            SetForegroundWindow(_window);

            if (!GetCursorPos(out var cursor))
            {
                return;
            }

            // TPM_RETURNCMD makes this return the chosen id rather than posting
            // WM_COMMAND, which keeps the handling on this thread and in one place.
            var chosen = TrackPopupMenu(
                menu, TpmRightButton | TpmReturnCmd, cursor.X, cursor.Y, 0, _window, 0);

            if (chosen != 0)
            {
                HandleCommand(chosen);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleCommand(int commandId)
    {
        var index = commandId - FirstCommandId;

        if ((uint)index >= (uint)_currentMenu.Count)
        {
            return;
        }

        var item = _currentMenu[index];

        if (item.IsSeparator || !item.IsEnabled)
        {
            return;
        }

        Invoke(item.Command);
    }

    private void Invoke(TrayCommand command)
    {
        CommandsInvoked++;

        try
        {
            // The handler queues work; it must not do anything slow on this thread,
            // which is also the one drawing the menu.
            _onCommand(command);
        }
        catch (Exception ex)
        {
            _log.Error($"The tray command {command} failed.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _ready.Dispose();
    }

    private static readonly nint HwndMessage = -3;

    private delegate nint WndProc(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string LpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMessageW(out Msg message, nint window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint instance, int name);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? item);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);
}
