namespace Frost.Engine.Diagnostics;

/// <summary>One line of the performance budget, and whether a measurement met it.</summary>
/// <param name="Name">What is being measured.</param>
/// <param name="Limit">The budget, in <paramref name="Unit"/>.</param>
/// <param name="Unit">Unit for the limit and the measurement.</param>
/// <param name="Measured">
/// What was measured, or null when it has not been measured on this machine.
/// </param>
public readonly record struct BudgetLine(string Name, double Limit, string Unit, double? Measured = null)
{
    /// <summary>True when a measurement exists and is within the limit.</summary>
    public bool? Passes => Measured is null ? null : Measured <= Limit;

    /// <summary>How the line reads in a report.</summary>
    public string Verdict => Passes switch
    {
        null => "not measured",
        true => "pass",
        false => "OVER BUDGET",
    };

    public string Describe() => Measured is null
        ? $"{Name}: budget {Limit:G} {Unit} — not measured"
        : $"{Name}: {Measured:G} {Unit} against a budget of {Limit:G} {Unit} — {Verdict}";
}

/// <summary>
/// The performance budget from the spec, as data.
/// </summary>
/// <remarks>
/// <para>Written down so a measurement run can be checked against it mechanically
/// rather than by reading a table and squinting. Every limit here is quoted from
/// the spec; none is a target invented after the fact.</para>
///
/// <para>What this type deliberately does <b>not</b> do is supply numbers. A
/// measurement that has not happened reads "not measured", and a report full of
/// those is the honest output of a machine that cannot run the Engine. Filling
/// them in requires the hardware.</para>
/// </remarks>
public static class PerformanceBudget
{
    /// <summary>Engine armed but not recording.</summary>
    public static BudgetLine IdleCpuPercent => new("Engine idle CPU", 1.0, "% of machine");

    public static BudgetLine IdleMemoryMegabytes => new("Engine idle memory", 50.0, "MB working set");

    /// <summary>Ring-buffering 1080p60.</summary>
    public static BudgetLine ActiveCpuPercent => new("Engine recording CPU", 3.0, "% of machine");

    /// <summary>
    /// The one that actually matters to a player: capture must not cost frames.
    /// </summary>
    public static BudgetLine GameFrameTimeIncreaseMilliseconds =>
        new("Game frame-time increase", 0.0, "ms (must be unmeasurable)");

    /// <summary>Hotkey press to toast on screen.</summary>
    public static BudgetLine ToastLatencyMilliseconds => new("Hotkey to toast", 150.0, "ms");

    /// <summary>Every line, in the order a report should list them.</summary>
    public static IReadOnlyList<BudgetLine> AllLines =>
    [
        IdleCpuPercent,
        IdleMemoryMegabytes,
        ActiveCpuPercent,
        GameFrameTimeIncreaseMilliseconds,
        ToastLatencyMilliseconds,
    ];

    /// <summary>
    /// Renders a report, marking anything unmeasured as such.
    /// </summary>
    /// <param name="measurements">
    /// Measured values by line name. Lines absent from this are reported as not
    /// measured rather than as passing.
    /// </param>
    public static string Report(IReadOnlyDictionary<string, double>? measurements = null)
    {
        var lines = AllLines
            .Select(line => measurements is not null && measurements.TryGetValue(line.Name, out var value)
                ? line with { Measured = value }
                : line)
            .ToList();

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Performance budget");
        builder.AppendLine("==================");

        foreach (var line in lines)
        {
            builder.AppendLine("  " + line.Describe());
        }

        var measured = lines.Count(l => l.Measured is not null);
        var over = lines.Count(l => l.Passes == false);

        builder.AppendLine();
        builder.AppendLine(
            $"  {measured} of {lines.Count} lines measured, {over} over budget.");

        if (measured < lines.Count)
        {
            builder.AppendLine(
                "  Unmeasured lines need a Windows machine with a hardware encoder; " +
                "see README for the procedure.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether every line has been measured and every one passes.
    /// </summary>
    /// <remarks>
    /// Explicitly false when anything is unmeasured: "we did not check" must never
    /// read as "it passed".
    /// </remarks>
    public static bool IsFullyMet(IReadOnlyDictionary<string, double> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        return AllLines.All(line =>
            measurements.TryGetValue(line.Name, out var value) && value <= line.Limit);
    }
}
