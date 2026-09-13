using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// Asserts that a hot-path loop does not allocate per iteration.
/// </summary>
/// <remarks>
/// <para>Frost's capture, encode and hotkey threads are not allowed to allocate,
/// and that is worth measuring rather than asserting in a comment. But an exact
/// "zero bytes" assertion is flaky: tiered compilation can promote a long-running
/// loop mid-measurement (on-stack replacement), and that bookkeeping lands on the
/// measuring thread. The failure looks like a product regression and is not one.
/// </para>
///
/// <para>So the bound is <i>less than one byte per iteration</i>. The smallest
/// object .NET can allocate is 24 bytes, so any code path that allocated even
/// once per iteration would exceed this by more than an order of magnitude. It
/// proves the thing that matters — nothing is allocated per sample — while being
/// immune to a one-off cost that does not scale with the loop.</para>
/// </remarks>
internal static class AllocationAssert
{
    /// <summary>
    /// Runs <paramref name="body"/> <paramref name="warmUpIterations"/> times to
    /// settle the JIT, then <paramref name="iterations"/> times while measuring.
    /// </summary>
    internal static void NoPerIterationAllocation(
        Action<int> body,
        int iterations = 200_000,
        int warmUpIterations = 5_000)
    {
        ArgumentNullException.ThrowIfNull(body);

        for (var i = 0; i < warmUpIterations; i++)
        {
            body(i);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = warmUpIterations; i < warmUpIterations + iterations; i++)
        {
            body(i);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated < iterations,
            $"allocated {allocated} bytes over {iterations} iterations " +
            $"({(double)allocated / iterations:F3} bytes each); the hot path must allocate nothing per " +
            "iteration, and the smallest possible object is 24 bytes");
    }
}
