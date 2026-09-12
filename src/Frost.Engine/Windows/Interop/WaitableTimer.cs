using System.Runtime.InteropServices;

namespace Frost.Engine.Windows.Interop;

/// <summary>
/// A high-resolution waitable timer, used to pace the capture thread.
/// </summary>
/// <remarks>
/// The alternatives are all worse for this job: <c>Thread.Sleep</c> is bound to
/// the system timer tick (up to ~15.6ms, which at 60fps is an entire frame),
/// spinning burns a core, and raising the global timer resolution with
/// <c>timeBeginPeriod</c> degrades power behaviour system-wide — an unacceptable
/// thing for a background process to do to a laptop.
/// <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c> gives sub-millisecond wakeups for
/// this thread alone.
/// </remarks>
internal sealed partial class WaitableTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint WaitObject0 = 0;
    private const uint WaitFailed = 0xFFFFFFFF;

    private nint _handle;

    private WaitableTimer(nint handle, bool highResolution)
    {
        _handle = handle;
        IsHighResolution = highResolution;
    }

    /// <summary>
    /// False on builds older than Windows 10 1803, where the timer falls back to
    /// the coarse system tick. Capture still works; pacing is just less exact.
    /// </summary>
    internal bool IsHighResolution { get; }

    internal static WaitableTimer Create()
    {
        var handle = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (handle != 0)
        {
            return new WaitableTimer(handle, highResolution: true);
        }

        handle = CreateWaitableTimerExW(0, null, 0, TimerAllAccess);
        if (handle == 0)
        {
            throw new InvalidOperationException(
                $"CreateWaitableTimerExW failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        return new WaitableTimer(handle, highResolution: false);
    }

    /// <summary>
    /// Blocks for <paramref name="ticks"/> 100ns units, or returns immediately if
    /// the delay is not positive. Allocation-free.
    /// </summary>
    internal void Wait(long ticks)
    {
        if (ticks <= 0 || _handle == 0)
        {
            return;
        }

        // Negative due time means "relative", in 100ns units.
        var dueTime = -ticks;
        if (!SetWaitableTimer(_handle, ref dueTime, 0, 0, 0, false))
        {
            throw new InvalidOperationException(
                $"SetWaitableTimer failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var result = WaitForSingleObject(_handle, 0xFFFFFFFF);
        if (result is not WaitObject0)
        {
            if (result == WaitFailed)
            {
                throw new InvalidOperationException(
                    $"WaitForSingleObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
    }

    /// <summary>Cancels a pending wait so a blocked capture thread wakes promptly on shutdown.</summary>
    internal void Cancel()
    {
        if (_handle != 0)
        {
            CancelWaitableTimer(_handle);
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            CancelWaitableTimer(handle);
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWaitableTimerExW(nint timerAttributes, string? timerName, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        nint timer,
        ref long dueTime,
        int period,
        nint completionRoutine,
        nint argToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelWaitableTimer(nint timer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
