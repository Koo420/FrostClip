using System.Runtime.InteropServices;
using Frost.Engine.Diagnostics;

namespace Frost.Engine.Windows.Diagnostics;

/// <summary>Utilisation of one GPU engine, as a percentage.</summary>
/// <param name="EngineType">
/// <c>VideoEncode</c>, <c>3D</c>, <c>Compute</c>, <c>Copy</c>, <c>VideoDecode</c>.
/// </param>
public readonly record struct GpuEngineUsage(string EngineType, double UtilizationPercent);

/// <summary>
/// Reads Windows' per-process GPU engine counters, so "the video encode engine
/// is doing the work, not the CPU or the 3D engine" is something Frost can
/// check rather than something a human squints at in Task Manager.
/// </summary>
/// <remarks>
/// These are the same counters Task Manager's GPU columns come from
/// (<c>\GPU Engine(...)\Utilization Percentage</c>), read through PDH. Instance
/// names look like <c>pid_1234_luid_0x00000000_0x0000C64D_phys_0_eng_1_engtype_VideoEncode</c>,
/// so filtering on our own PID gives Frost's contribution specifically rather
/// than the whole machine's.
/// <para>
/// Diagnostic code: it runs on the soak/encode-test path, never on the capture
/// or encode threads.
/// </para>
/// </remarks>
internal sealed partial class GpuEngineCounters : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";

    private readonly IEngineLog _log;
    private readonly string _pidPrefix;
    private nint _query;
    private nint _counter;
    private byte[] _buffer = new byte[64 * 1024];

    private GpuEngineCounters(nint query, nint counter, int processId, IEngineLog log)
    {
        _query = query;
        _counter = counter;
        _log = log;
        _pidPrefix = $"pid_{processId}_";
    }

    /// <summary>
    /// Opens the counters, or returns null when they are unavailable — an old
    /// build, a disabled perflib, or a driver that does not publish them. A
    /// missing counter is a reason to say "could not verify", never a reason to
    /// fail a recording.
    /// </summary>
    internal static GpuEngineCounters? TryCreate(IEngineLog log)
    {
        var status = PdhOpenQueryW(null, 0, out var query);
        if (status != 0)
        {
            log.Warn($"PdhOpenQuery failed (0x{status:X8}); GPU engine counters unavailable.");
            return null;
        }

        // The "English" variant so the counter path works on a localised Windows.
        status = PdhAddEnglishCounterW(query, CounterPath, 0, out var counter);
        if (status != 0)
        {
            log.Warn($"GPU Engine counters are not present on this system (0x{status:X8}).");
            PdhCloseQuery(query);
            return null;
        }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var instance = new GpuEngineCounters(query, counter, process.Id, log);

        // Rate counters need a first collection to establish a baseline; the
        // first Sample() after this is the first meaningful one.
        PdhCollectQueryData(query);
        return instance;
    }

    /// <summary>
    /// Per-engine utilisation for this process since the previous sample.
    /// Engines used by more than one context are summed.
    /// </summary>
    internal List<GpuEngineUsage> Sample()
    {
        var results = new List<GpuEngineUsage>(4);

        var status = PdhCollectQueryData(_query);
        if (status != 0)
        {
            return results;
        }

        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var bufferSize = (uint)_buffer.Length;
        uint itemCount;

        unsafe
        {
            fixed (byte* pinned = _buffer)
            {
                status = PdhGetFormattedCounterArrayW(
                    _counter, PdhFmtDouble, ref bufferSize, out itemCount, (nint)pinned);

                if (status == PdhMoreData)
                {
                    _buffer = new byte[bufferSize];
                    fixed (byte* grown = _buffer)
                    {
                        status = PdhGetFormattedCounterArrayW(
                            _counter, PdhFmtDouble, ref bufferSize, out itemCount, (nint)grown);
                    }
                }

                if (status != 0)
                {
                    _log.Debug($"PdhGetFormattedCounterArray failed (0x{status:X8}).");
                    return results;
                }

                fixed (byte* items = _buffer)
                {
                    for (var i = 0; i < itemCount; i++)
                    {
                        var item = *(PdhFormattedCounterItem*)(items + (i * sizeof(PdhFormattedCounterItem)));
                        var name = Marshal.PtrToStringUni(item.Name);

                        if (name is null || !name.StartsWith(_pidPrefix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (item.Value.CStatus != 0)
                        {
                            continue;
                        }

                        var engineType = EngineTypeOf(name);
                        totals[engineType] = totals.GetValueOrDefault(engineType) + item.Value.DoubleValue;
                    }
                }
            }
        }

        foreach (var (engine, utilization) in totals)
        {
            results.Add(new GpuEngineUsage(engine, utilization));
        }

        results.Sort((a, b) => b.UtilizationPercent.CompareTo(a.UtilizationPercent));
        return results;
    }

    /// <summary>Pulls <c>VideoEncode</c> out of an instance name's <c>engtype_</c> suffix.</summary>
    internal static string EngineTypeOf(string instanceName)
    {
        const string marker = "engtype_";
        var index = instanceName.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? "Unknown" : instanceName[(index + marker.Length)..];
    }

    public void Dispose()
    {
        if (_counter != 0)
        {
            _counter = 0;
        }

        if (_query != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValue
    {
        public uint CStatus;
        private readonly uint _padding;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterItem
    {
        public nint Name;
        public PdhCounterValue Value;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhGetFormattedCounterArrayW(
        nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
