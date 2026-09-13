using System.Diagnostics;
using System.Runtime.InteropServices;
using Frost.Engine.Diagnostics;
using Frost.Engine.Overlay;

namespace Frost.Engine.Windows.Overlay;

/// <summary>
/// The "clip saved" toast: one pre-rendered image, shown briefly.
/// </summary>
/// <remarks>
/// <para><b>What this deliberately is not.</b> Not an overlay that renders every
/// frame, not a counter, not anything that touches the game's swap chain. The
/// spec allows exactly one UI cost while a game has focus — a static,
/// pre-rendered image on screen for under 1.5s — and this is built to be only
/// that.</para>
///
/// <para><b>How that is achieved.</b> Every toast image is drawn once, at
/// startup, into a GDI bitmap. Showing one is a single
/// <c>UpdateLayeredWindow</c> call that hands Windows the finished pixels;
/// there is no <c>WM_PAINT</c> handler and no painting at show time. Hiding is a
/// <c>ShowWindow</c> call. Between toasts the overlay thread sits in a timed
/// wait, so an idle Engine does no overlay work at all.</para>
///
/// <para><b>Window style.</b> <c>WS_EX_LAYERED</c> for per-pixel alpha,
/// <c>WS_EX_TRANSPARENT</c> so clicks pass through to the game,
/// <c>WS_EX_NOACTIVATE</c> so it never steals focus — stealing focus from a
/// fullscreen game is the single worst thing an overlay can do —
/// <c>WS_EX_TOOLWINDOW</c> to stay out of Alt-Tab, and topmost so it is visible
/// over a borderless game.</para>
/// </remarks>
internal sealed class ToastOverlay : IDisposable
{
    private const int WmQuit = 0x0012;
    private const int WmShowToast = 0x0400 + 10;

    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExToolWindow = 0x00000080;

    private const uint WsPopup = 0x80000000;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    /// <summary>Toast size in pixels at 100% scale.</summary>
    private const int ToastWidth = 260;
    private const int ToastHeight = 64;

    private readonly ToastPolicy _policy;
    private readonly IEngineLog _log;
    private readonly Dictionary<ToastKind, nint> _bitmaps = [];
    private readonly object _requestGate = new();

    private Thread? _thread;
    private nint _window;
    private uint _threadId;
    private ToastKind? _pending;
    private volatile bool _stopping;
    private bool _disposed;

    internal ToastOverlay(ToastPolicy policy, IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(log);

        _policy = policy;
        _log = log;
    }

    internal bool IsRunning => _thread is { IsAlive: true } && !_stopping;

    /// <summary>Toasts actually put on screen.</summary>
    internal long ToastsShown => _policy.ToastsShown;

    internal void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The toast overlay is already started.");
        }

        _stopping = false;
        _thread = new Thread(OverlayLoop)
        {
            Name = "frost-toast",
            IsBackground = true,

            // Lowest of Frost's threads: a toast is cosmetic and must never
            // compete with capture, encode, or the game.
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
    }

    /// <summary>
    /// Requests a toast. Safe to call from any thread, including the clip thread
    /// and the IPC thread; does no drawing and never blocks.
    /// </summary>
    internal void Show(ToastKind kind)
    {
        if (_stopping || _thread is null)
        {
            return;
        }

        lock (_requestGate)
        {
            // Only the most recent request matters: the overlay replaces rather
            // than queues, so an older pending toast is already superseded.
            _pending = kind;
        }

        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmShowToast, 0, 0);
        }
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
            _log.Warn("The toast overlay thread did not stop within 5s.");
        }

        _thread = null;
    }

    private void OverlayLoop()
    {
        _threadId = GetCurrentThreadId();

        try
        {
            CreateWindow();
            PreRenderAll();

            using var timer = Interop.WaitableTimer.Create();

            while (!_stopping)
            {
                var kind = TakePending();

                if (kind is not null)
                {
                    Present(kind.Value);
                }

                var now = MonotonicTicks();

                if (_policy.ShouldHide(now))
                {
                    Hide();
                    continue;
                }

                var remaining = _policy.TicksUntilHide(now);

                if (remaining is null)
                {
                    // Nothing on screen: block until a toast is requested. An idle
                    // Engine does no overlay work whatsoever.
                    if (GetMessageW(out var message, 0, 0, 0) is 0 or -1 ||
                        message.Message == WmQuit)
                    {
                        break;
                    }

                    continue;
                }

                // Something is up: wait exactly until it should come down, but stay
                // responsive to a replacement arriving.
                timer.Wait(Math.Min(remaining.Value, TimeSpan.TicksPerMillisecond * 50));
            }
        }
        catch (Exception ex)
        {
            _log.Error("The toast overlay faulted; clip feedback will be missing.", ex);
        }
        finally
        {
            Cleanup();
        }
    }

    private ToastKind? TakePending()
    {
        lock (_requestGate)
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }
    }

    private void Present(ToastKind kind)
    {
        var action = _policy.Request(kind, MonotonicTicks());

        if (action is ToastAction.Ignore)
        {
            return;
        }

        if (action is ToastAction.ExtendCurrent)
        {
            // Same image already up: the timer restarted, and there is nothing to
            // draw or move.
            return;
        }

        if (!_bitmaps.TryGetValue(kind, out var bitmap))
        {
            _log.Warn($"No pre-rendered image for the {kind} toast.");
            return;
        }

        ApplyBitmap(bitmap);
        ShowWindow(_window, SwShowNoActivate);
    }

    private void Hide()
    {
        ShowWindow(_window, SwHide);
        _policy.NoteHidden();
    }

    private void CreateWindow()
    {
        var className = "FrostToastWindow";
        var module = GetModuleHandleW(null);

        var windowClass = new WndClass
        {
            LpfnWndProc = GetDefWindowProcPointer(),
            HInstance = module,
            LpszClassName = className,
        };

        RegisterClassW(ref windowClass);

        var (x, y) = TopRightPosition();

        _window = CreateWindowExW(
            WsExLayered | WsExTransparent | WsExTopmost | WsExNoActivate | WsExToolWindow,
            className,
            "Frost",
            WsPopup,
            x, y, ToastWidth, ToastHeight,
            0, 0, module, 0);

        if (_window == 0)
        {
            throw new InvalidOperationException(
                $"Could not create the toast window (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>
    /// Top-right, inset from the edge.
    /// </summary>
    /// <remarks>
    /// Away from the centre of the screen and away from the bottom-left, where
    /// most games put their own killfeed and chat.
    /// </remarks>
    private static (int X, int Y) TopRightPosition()
    {
        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);

        var x = Math.Max(0, screenWidth - ToastWidth - 24);
        var y = Math.Max(0, (int)(screenHeight * 0.06));

        return (x, y);
    }

    /// <summary>
    /// Draws every toast image once.
    /// </summary>
    /// <remarks>
    /// The whole point: after this runs, showing a toast never draws anything.
    /// Done at startup, before any game is likely to be in the foreground.
    /// </remarks>
    private void PreRenderAll()
    {
        foreach (var kind in ToastPolicy.AllKinds)
        {
            try
            {
                _bitmaps[kind] = RenderToast(ToastPolicy.TextFor(kind));
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not pre-render the {kind} toast.", ex);
            }
        }

        _log.Debug($"Pre-rendered {_bitmaps.Count} toast image(s).");
    }

    /// <summary>Renders one toast into a 32-bit premultiplied DIB.</summary>
    private static nint RenderToast(string text)
    {
        var screen = GetDC(0);
        var memoryDc = CreateCompatibleDC(screen);

        try
        {
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = ToastWidth,

                // Negative height for a top-down DIB, which is the orientation
                // UpdateLayeredWindow expects.
                Height = -ToastHeight,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };

            var bitmap = CreateDIBSection(memoryDc, ref header, 0, out var bits, 0, 0);

            if (bitmap == 0 || bits == 0)
            {
                throw new InvalidOperationException("CreateDIBSection failed for the toast bitmap.");
            }

            var previous = SelectObject(memoryDc, bitmap);

            try
            {
                FillBackground(bits);
                DrawText(memoryDc, text);
            }
            finally
            {
                SelectObject(memoryDc, previous);
            }

            return bitmap;
        }
        finally
        {
            DeleteDC(memoryDc);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// Fills the bitmap with a rounded, semi-transparent dark panel.
    /// </summary>
    /// <remarks>
    /// Written straight into the DIB bits rather than through GDI, because GDI's
    /// drawing calls do not produce the premultiplied alpha
    /// <c>UpdateLayeredWindow</c> requires, and a non-premultiplied bitmap shows
    /// up as a bright fringe around the panel.
    /// </remarks>
    private static unsafe void FillBackground(nint bits)
    {
        var pixels = (uint*)bits;
        const int radius = 10;

        // 82% opaque near-black, premultiplied.
        const byte alpha = 210;
        var channel = (byte)(24 * alpha / 255);
        var colour = ((uint)alpha << 24) | ((uint)channel << 16) | ((uint)channel << 8) | channel;

        for (var y = 0; y < ToastHeight; y++)
        {
            for (var x = 0; x < ToastWidth; x++)
            {
                pixels[(y * ToastWidth) + x] = IsInsideRoundedRect(x, y, radius) ? colour : 0u;
            }
        }
    }

    private static bool IsInsideRoundedRect(int x, int y, int radius)
    {
        var left = radius - 1;
        var top = radius - 1;
        var right = ToastWidth - radius;
        var bottom = ToastHeight - radius;

        var dx = x < left ? left - x : x > right ? x - right : 0;
        var dy = y < top ? top - y : y > bottom ? y - bottom : 0;

        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    /// <summary>Draws the label centred, with GDI text on top of the panel.</summary>
    private static void DrawText(nint dc, string text)
    {
        // TRANSPARENT background mode, so GDI does not paint over the alpha the
        // panel fill just established.
        SetBkMode(dc, 1);
        SetTextColor(dc, 0x00F0F0F0);

        var font = CreateFontW(
            -20, 0, 0, 0, 600, false, false, false,
            1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, "Segoe UI");

        var previousFont = SelectObject(dc, font);

        try
        {
            var rect = new Rect { Left = 0, Top = 0, Right = ToastWidth, Bottom = ToastHeight };

            // DT_CENTER | DT_VCENTER | DT_SINGLELINE
            DrawTextW(dc, text, text.Length, ref rect, 0x00000001 | 0x00000004 | 0x00000020);
        }
        finally
        {
            SelectObject(dc, previousFont);
            DeleteObject(font);
        }
    }

    /// <summary>Hands Windows the finished pixels. The only per-toast GPU/GDI work.</summary>
    private void ApplyBitmap(nint bitmap)
    {
        var screen = GetDC(0);
        var memoryDc = CreateCompatibleDC(screen);
        var previous = SelectObject(memoryDc, bitmap);

        try
        {
            var size = new Size { Width = ToastWidth, Height = ToastHeight };
            var source = new Point { X = 0, Y = 0 };

            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };

            if (!UpdateLayeredWindow(
                    _window, screen, 0, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha))
            {
                _log.Debug($"UpdateLayeredWindow failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteDC(memoryDc);
            ReleaseDC(0, screen);
        }
    }

    private void Cleanup()
    {
        foreach (var bitmap in _bitmaps.Values)
        {
            DeleteObject(bitmap);
        }

        _bitmaps.Clear();

        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
        }
    }

    private static nint GetDefWindowProcPointer()
    {
        // The toast has no behaviour: no input reaches it (WS_EX_TRANSPARENT) and
        // it never paints (UpdateLayeredWindow owns its pixels), so the default
        // procedure is all it needs and there is no delegate to keep alive.
        var user32 = GetModuleHandleW("user32.dll");
        return GetProcAddress(user32, "DefWindowProcW");
    }

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        nint window, nint screenDc, nint position, ref Size size,
        nint sourceDc, ref Point source, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(nint dc, string text, int count, ref Rect rect, uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMessageW(out Msg message, nint window, uint filterMin, uint filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(
        nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint dc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        bool italic, bool underline, bool strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
        string faceName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
