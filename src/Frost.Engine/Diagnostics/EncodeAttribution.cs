namespace Frost.Engine.Diagnostics;

/// <summary>
/// Verdict on where the encoding work actually went.
/// </summary>
/// <remarks>
/// The point of the exercise is Phase 2's last requirement: prove the GPU's
/// dedicated video-encode engine is doing the encoding, not the CPU and not the
/// 3D engine. Some 3D/copy engine use is expected and fine — the capture copy
/// and the NV12 colour conversion run there — so the test is that the video
/// encode engine is genuinely busy and the process's CPU use stays inside
/// budget, not that other engines are idle.
/// </remarks>
public sealed record EncodeAttributionReport(
    int Samples,
    double VideoEncodePercent,
    double ThreeDPercent,
    double ComputePercent,
    double CopyPercent,
    double ProcessCpuPercent,
    double CpuBudgetPercent)
{
    /// <summary>
    /// Below this, the video-encode engine is not meaningfully doing anything and
    /// something else must be encoding.
    /// </summary>
    public const double MinimumVideoEncodePercent = 1.0;

    /// <summary>Whether the GPU engine counters were readable at all.</summary>
    public bool CountersAvailable => Samples > 0;

    public bool VideoEncodeEngineIsWorking => VideoEncodePercent >= MinimumVideoEncodePercent;

    public bool CpuWithinBudget => ProcessCpuPercent <= CpuBudgetPercent;

    /// <summary>
    /// True only when the counters were readable, the video encode engine was
    /// busy, and CPU stayed inside budget. An unreadable counter is reported as
    /// "could not verify" rather than quietly passing.
    /// </summary>
    public bool Passes => CountersAvailable && VideoEncodeEngineIsWorking && CpuWithinBudget;

    public string Verdict => !CountersAvailable
        ? "UNVERIFIED (GPU engine counters unavailable)"
        : Passes
            ? "PASS"
            : !VideoEncodeEngineIsWorking
                ? "FAIL (video encode engine idle — something else is encoding)"
                : "FAIL (CPU over budget)";

    public override string ToString() =>
        $"""
        samples            {Samples}
        GPU VideoEncode    {VideoEncodePercent:F1}% (needs >= {MinimumVideoEncodePercent:F1}%)
        GPU 3D             {ThreeDPercent:F1}%
        GPU Compute        {ComputePercent:F1}%
        GPU Copy           {CopyPercent:F1}%
        process CPU        {ProcessCpuPercent:F2}% (budget {CpuBudgetPercent:F2}%)
        verdict            {Verdict}
        """;
}

/// <summary>
/// Accumulates GPU-engine and CPU samples and produces an
/// <see cref="EncodeAttributionReport"/>.
/// </summary>
/// <remarks>
/// Platform-agnostic so the judgement is testable; the Windows side only
/// supplies the numbers.
/// </remarks>
public sealed class EncodeAttributionTracker
{
    private readonly Dictionary<string, double> _engineTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly double _cpuBudgetPercent;
    private double _cpuTotal;
    private int _samples;

    /// <param name="cpuBudgetPercent">
    /// Share of total machine CPU Frost is allowed while ring-buffering. Defaults
    /// to the 3% in the performance budget.
    /// </param>
    public EncodeAttributionTracker(double cpuBudgetPercent = 3.0)
    {
        if (cpuBudgetPercent is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cpuBudgetPercent), cpuBudgetPercent, "Must be a percentage above zero.");
        }

        _cpuBudgetPercent = cpuBudgetPercent;
    }

    public int Samples => _samples;

    /// <summary>
    /// Adds one observation. <paramref name="engineUtilization"/> maps engine
    /// type (<c>VideoEncode</c>, <c>3D</c>, …) to a percentage.
    /// </summary>
    public void Add(IEnumerable<KeyValuePair<string, double>> engineUtilization, double processCpuPercent)
    {
        ArgumentNullException.ThrowIfNull(engineUtilization);

        foreach (var (engine, utilization) in engineUtilization)
        {
            _engineTotals[engine] = _engineTotals.GetValueOrDefault(engine) + utilization;
        }

        _cpuTotal += processCpuPercent;
        _samples++;
    }

    public EncodeAttributionReport Report()
    {
        if (_samples == 0)
        {
            return new EncodeAttributionReport(0, 0, 0, 0, 0, 0, _cpuBudgetPercent);
        }

        return new EncodeAttributionReport(
            Samples: _samples,
            VideoEncodePercent: Average("VideoEncode"),
            ThreeDPercent: Average("3D"),
            ComputePercent: Average("Compute"),
            CopyPercent: Average("Copy"),
            ProcessCpuPercent: _cpuTotal / _samples,
            CpuBudgetPercent: _cpuBudgetPercent);
    }

    private double Average(string engine) => _engineTotals.GetValueOrDefault(engine) / _samples;
}
