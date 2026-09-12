using Frost.Engine.Diagnostics;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class EncodeAttributionTests
{
    private static Dictionary<string, double> Engines(
        double videoEncode = 0, double threeD = 0, double compute = 0, double copy = 0) =>
        new()
        {
            ["VideoEncode"] = videoEncode,
            ["3D"] = threeD,
            ["Compute"] = compute,
            ["Copy"] = copy,
        };

    [Fact]
    public void BusyVideoEncodeEngineAndLowCpuPasses()
    {
        var tracker = new EncodeAttributionTracker();
        for (var i = 0; i < 10; i++)
        {
            tracker.Add(Engines(videoEncode: 8.5, threeD: 2.0, copy: 1.0), processCpuPercent: 1.2);
        }

        var report = tracker.Report();

        Assert.True(report.Passes);
        Assert.Equal("PASS", report.Verdict);
        Assert.Equal(8.5, report.VideoEncodePercent, 3);
        Assert.Equal(1.2, report.ProcessCpuPercent, 3);
    }

    [Fact]
    public void AnIdleVideoEncodeEngineFailsEvenWhenTheGpuIsOtherwiseBusy()
    {
        // The failure this exists to catch: encoding silently landing on the 3D
        // engine or a software encoder while the GPU still looks busy.
        var tracker = new EncodeAttributionTracker();
        tracker.Add(Engines(videoEncode: 0.0, threeD: 45.0), processCpuPercent: 1.0);

        var report = tracker.Report();

        Assert.False(report.Passes);
        Assert.False(report.VideoEncodeEngineIsWorking);
        Assert.Contains("video encode engine idle", report.Verdict);
    }

    [Fact]
    public void CpuOverBudgetFailsEvenWithTheVideoEngineBusy()
    {
        var tracker = new EncodeAttributionTracker(cpuBudgetPercent: 3.0);
        tracker.Add(Engines(videoEncode: 10.0), processCpuPercent: 14.0);

        var report = tracker.Report();

        Assert.False(report.Passes);
        Assert.True(report.VideoEncodeEngineIsWorking);
        Assert.False(report.CpuWithinBudget);
        Assert.Contains("CPU over budget", report.Verdict);
    }

    [Fact]
    public void SomeThreeDAndCopyUseIsExpectedAndDoesNotFail()
    {
        // The capture copy and the NV12 conversion legitimately use those
        // engines; the requirement is about where the *encode* happens.
        var tracker = new EncodeAttributionTracker();
        tracker.Add(Engines(videoEncode: 6.0, threeD: 9.0, copy: 4.0), processCpuPercent: 2.0);

        Assert.True(tracker.Report().Passes);
    }

    [Fact]
    public void NoSamplesReportsUnverifiedRatherThanPassing()
    {
        var report = new EncodeAttributionTracker().Report();

        Assert.False(report.CountersAvailable);
        Assert.False(report.Passes);
        Assert.Contains("UNVERIFIED", report.Verdict);
    }

    [Fact]
    public void MultipleContextsOnOneEngineAreSummedThenAveraged()
    {
        var tracker = new EncodeAttributionTracker();
        tracker.Add([new("VideoEncode", 4.0), new("VideoEncode", 3.0)], 1.0);
        tracker.Add([new("VideoEncode", 5.0)], 1.0);

        // 7 + 5 over two samples.
        Assert.Equal(6.0, tracker.Report().VideoEncodePercent, 3);
    }

    [Fact]
    public void ThresholdIsAtTheBoundaryNotAboveIt()
    {
        var tracker = new EncodeAttributionTracker();
        tracker.Add(Engines(videoEncode: EncodeAttributionReport.MinimumVideoEncodePercent), 0.5);
        Assert.True(tracker.Report().VideoEncodeEngineIsWorking);

        var justUnder = new EncodeAttributionTracker();
        justUnder.Add(Engines(videoEncode: EncodeAttributionReport.MinimumVideoEncodePercent - 0.01), 0.5);
        Assert.False(justUnder.Report().VideoEncodeEngineIsWorking);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    public void NonsenseBudgetsAreRejected(double budget) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncodeAttributionTracker(budget));

    [Fact]
    public void ReportRendersEveryNumberItJudgedOn()
    {
        var tracker = new EncodeAttributionTracker();
        tracker.Add(Engines(videoEncode: 7.4, threeD: 3.5, compute: 0.25, copy: 1.75), 2.5);

        var text = tracker.Report().ToString();

        Assert.Contains("VideoEncode    7.4%", text);
        Assert.Contains("GPU 3D             3.5%", text);
        Assert.Contains("process CPU        2.50%", text);
        Assert.Contains("PASS", text);
    }
}
