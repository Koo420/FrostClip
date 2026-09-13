using Frost.Engine.Diagnostics;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class PerformanceBudgetTests
{
    [Fact]
    public void TheBudgetMatchesTheSpec()
    {
        Assert.Equal(1.0, PerformanceBudget.IdleCpuPercent.Limit);
        Assert.Equal(50.0, PerformanceBudget.IdleMemoryMegabytes.Limit);
        Assert.Equal(3.0, PerformanceBudget.ActiveCpuPercent.Limit);
        Assert.Equal(150.0, PerformanceBudget.ToastLatencyMilliseconds.Limit);
        Assert.Equal(0.0, PerformanceBudget.GameFrameTimeIncreaseMilliseconds.Limit);
    }

    [Fact]
    public void AnUnmeasuredLineNeverReadsAsPassing()
    {
        // "We did not check" must not be mistaken for "it passed".
        var line = PerformanceBudget.IdleCpuPercent;

        Assert.Null(line.Passes);
        Assert.Equal("not measured", line.Verdict);
        Assert.Contains("not measured", line.Describe());
    }

    [Fact]
    public void AMeasurementInsideTheBudgetPasses()
    {
        var line = PerformanceBudget.IdleCpuPercent with { Measured = 0.4 };

        Assert.True(line.Passes);
        Assert.Equal("pass", line.Verdict);
        Assert.Contains("0.4", line.Describe());
    }

    [Fact]
    public void AMeasurementOverTheBudgetFailsLoudly()
    {
        var line = PerformanceBudget.IdleMemoryMegabytes with { Measured = 120 };

        Assert.False(line.Passes);
        Assert.Equal("OVER BUDGET", line.Verdict);
    }

    [Fact]
    public void ALineExactlyAtTheLimitPasses() =>
        Assert.True((PerformanceBudget.ActiveCpuPercent with { Measured = 3.0 }).Passes);

    [Fact]
    public void AnEmptyReportSaysEverythingIsUnmeasured()
    {
        var report = PerformanceBudget.Report();

        Assert.Contains("0 of 5 lines measured", report);
        Assert.Contains("need a Windows machine", report);
        Assert.DoesNotContain("pass", report);
    }

    [Fact]
    public void APartialReportSaysWhichLinesAreStillMissing()
    {
        var report = PerformanceBudget.Report(new Dictionary<string, double>
        {
            [PerformanceBudget.IdleCpuPercent.Name] = 0.3,
            [PerformanceBudget.IdleMemoryMegabytes.Name] = 38,
        });

        Assert.Contains("2 of 5 lines measured", report);
        Assert.Contains("Unmeasured lines", report);
    }

    [Fact]
    public void AFullReportCountsWhatIsOverBudget()
    {
        var report = PerformanceBudget.Report(new Dictionary<string, double>
        {
            [PerformanceBudget.IdleCpuPercent.Name] = 0.3,
            [PerformanceBudget.IdleMemoryMegabytes.Name] = 38,
            [PerformanceBudget.ActiveCpuPercent.Name] = 7.5,
            [PerformanceBudget.GameFrameTimeIncreaseMilliseconds.Name] = 0,
            [PerformanceBudget.ToastLatencyMilliseconds.Name] = 90,
        });

        Assert.Contains("5 of 5 lines measured", report);
        Assert.Contains("1 over budget", report);
        Assert.Contains("OVER BUDGET", report);
        Assert.DoesNotContain("needs a Windows machine", report);
    }

    [Fact]
    public void PartialMeasurementsDoNotCountAsMeetingTheBudget()
    {
        Assert.False(PerformanceBudget.IsFullyMet(new Dictionary<string, double>
        {
            [PerformanceBudget.IdleCpuPercent.Name] = 0.1,
        }));
    }

    [Fact]
    public void EveryLineWithinBudgetCountsAsMet()
    {
        Assert.True(PerformanceBudget.IsFullyMet(
            PerformanceBudget.AllLines.ToDictionary(l => l.Name, l => l.Limit)));
    }

    [Fact]
    public void EveryLineIsNamedAndHasAUnit() =>
        Assert.All(PerformanceBudget.AllLines, line =>
        {
            Assert.False(string.IsNullOrWhiteSpace(line.Name));
            Assert.False(string.IsNullOrWhiteSpace(line.Unit));
        });

    [Fact]
    public void LineNamesAreUniqueSoAReportCannotCollide() =>
        Assert.Equal(
            PerformanceBudget.AllLines.Count,
            PerformanceBudget.AllLines.Select(l => l.Name).Distinct().Count());

    [Fact]
    public void NullMeasurementsAreRejected() =>
        Assert.Throws<ArgumentNullException>(() => PerformanceBudget.IsFullyMet(null!));
}
