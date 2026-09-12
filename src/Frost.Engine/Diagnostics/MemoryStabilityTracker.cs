namespace Frost.Engine.Diagnostics;

/// <summary>One sample of the numbers a leak would move.</summary>
public readonly record struct MemorySample(
    TimeSpan Elapsed,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen2Collections);

/// <summary>
/// Verdict for a soak run: is memory flat, and is the pipeline still allocating
/// once it is warm?
/// </summary>
public sealed record MemoryStabilityReport(
    TimeSpan Duration,
    int SampleCount,
    long BaselineWorkingSetBytes,
    long PeakWorkingSetBytes,
    long FinalWorkingSetBytes,
    long SteadyStateAllocatedBytes,
    double SteadyStateBytesPerSecond,
    int Gen2Collections)
{
    /// <summary>Working-set growth from the post-warm-up baseline to the end of the run.</summary>
    public long WorkingSetDriftBytes => FinalWorkingSetBytes - BaselineWorkingSetBytes;

    /// <summary>
    /// True when working set did not drift meaningfully and the steady-state
    /// allocation rate is low enough that the hot path is not the source.
    /// </summary>
    public bool IsStable =>
        WorkingSetDriftBytes < DriftToleranceBytes &&
        SteadyStateBytesPerSecond < AllocationRateToleranceBytesPerSecond;

    /// <summary>8MB of drift over a long run is jitter, not a leak.</summary>
    public const long DriftToleranceBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Capture and encode threads are meant to allocate nothing. Anything above a
    /// trickle (logging, stats, the odd timer) means something in the loop is
    /// allocating per frame.
    /// </summary>
    public const double AllocationRateToleranceBytesPerSecond = 4096;

    public override string ToString() =>
        $"""
        duration           {Duration:hh\:mm\:ss}
        samples            {SampleCount}
        working set        baseline {Mb(BaselineWorkingSetBytes)} -> final {Mb(FinalWorkingSetBytes)} (peak {Mb(PeakWorkingSetBytes)})
        drift              {Mb(WorkingSetDriftBytes)} (tolerance {Mb(DriftToleranceBytes)})
        steady-state alloc {Mb(SteadyStateAllocatedBytes)} total, {SteadyStateBytesPerSecond:F0} B/s (tolerance {AllocationRateToleranceBytesPerSecond:F0} B/s)
        gen2 collections   {Gen2Collections}
        verdict            {(IsStable ? "STABLE" : "UNSTABLE")}
        """;

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F2}MB";
}

/// <summary>
/// Samples memory over a long run and decides whether it is flat.
/// </summary>
/// <remarks>
/// Platform-agnostic so the same judgement is applied by the Windows capture
/// soak and by the pipeline soak test in CI. The first samples are treated as
/// warm-up — JIT, first-touch pages and the initial GC heap growth are not a
/// leak — and the baseline is taken after them.
/// </remarks>
public sealed class MemoryStabilityTracker
{
    private readonly List<MemorySample> _samples = [];
    private readonly int _warmUpSamples;
    private readonly Func<long> _workingSet;

    public MemoryStabilityTracker(int warmUpSamples = 3, Func<long>? workingSetProvider = null)
    {
        if (warmUpSamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warmUpSamples), warmUpSamples, "Must be >= 0.");
        }

        _warmUpSamples = warmUpSamples;
        _workingSet = workingSetProvider ?? CurrentWorkingSet;
    }

    public IReadOnlyList<MemorySample> Samples => _samples;

    public MemorySample Sample(TimeSpan elapsed)
    {
        var sample = new MemorySample(
            elapsed,
            _workingSet(),
            GC.GetTotalMemory(forceFullCollection: false),
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(2));

        _samples.Add(sample);
        return sample;
    }

    public MemoryStabilityReport Report()
    {
        if (_samples.Count == 0)
        {
            throw new InvalidOperationException("No samples were taken.");
        }

        var baselineIndex = Math.Min(_warmUpSamples, _samples.Count - 1);
        var baseline = _samples[baselineIndex];
        var last = _samples[^1];

        var window = last.Elapsed - baseline.Elapsed;
        var allocated = last.TotalAllocatedBytes - baseline.TotalAllocatedBytes;
        var perSecond = window > TimeSpan.Zero ? allocated / window.TotalSeconds : 0;

        return new MemoryStabilityReport(
            Duration: last.Elapsed,
            SampleCount: _samples.Count,
            BaselineWorkingSetBytes: baseline.WorkingSetBytes,
            PeakWorkingSetBytes: _samples.Max(s => s.WorkingSetBytes),
            FinalWorkingSetBytes: last.WorkingSetBytes,
            SteadyStateAllocatedBytes: allocated,
            SteadyStateBytesPerSecond: perSecond,
            Gen2Collections: last.Gen2Collections - baseline.Gen2Collections);
    }

    private static long CurrentWorkingSet()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.WorkingSet64;
    }
}
