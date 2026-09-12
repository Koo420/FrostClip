using Frost.Engine.Capture;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class TexturePoolTests
{
    [Fact]
    public void AllocatesEveryTextureUpFront()
    {
        var allocator = new FakeTextureAllocator();
        using var pool = new TexturePool(allocator, 1920, 1080, 4);

        Assert.Equal(4, allocator.Created.Count);
        Assert.Equal(4, pool.Capacity);
        Assert.Equal(4, pool.Available);
    }

    [Fact]
    public void RentHandsOutDistinctSlotsUntilExhausted()
    {
        var allocator = new FakeTextureAllocator();
        using var pool = new TexturePool(allocator, 64, 64, 3);

        var slots = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(pool.TryRent(out var texture));
            Assert.True(texture.IsValid);
            Assert.NotEqual(0, texture.Handle);
            slots.Add(texture.SlotIndex);
        }

        Assert.Equal(3, slots.Distinct().Count());
        Assert.Equal(0, pool.Available);
        Assert.False(pool.TryRent(out _));
    }

    [Fact]
    public void ReturnedSlotBecomesRentableAgain()
    {
        var allocator = new FakeTextureAllocator();
        using var pool = new TexturePool(allocator, 64, 64, 2);

        Assert.True(pool.TryRent(out var a));
        Assert.True(pool.TryRent(out _));
        Assert.False(pool.TryRent(out _));

        pool.Return(a.SlotIndex);
        Assert.True(pool.TryRent(out var reRented));
        Assert.Equal(a.SlotIndex, reRented.SlotIndex);
    }

    [Fact]
    public void PreferredSlotIsHandedBackWhenFree()
    {
        // This is what makes a filler frame free: the capture stage asks for the
        // slot it just emitted, and if it is free its pixels are still the
        // previous frame's, so no GPU copy is needed.
        var allocator = new FakeTextureAllocator();
        using var pool = new TexturePool(allocator, 64, 64, 4);

        Assert.True(pool.TryRent(out var first));
        pool.Return(first.SlotIndex);

        Assert.True(pool.TryRentPreferring(first.SlotIndex, out var again));
        Assert.Equal(first.SlotIndex, again.SlotIndex);
    }

    [Fact]
    public void PreferredSlotFallsBackToAnyFreeSlotWhenBusy()
    {
        var allocator = new FakeTextureAllocator();
        using var pool = new TexturePool(allocator, 64, 64, 2);

        Assert.True(pool.TryRent(out var held));

        Assert.True(pool.TryRentPreferring(held.SlotIndex, out var other));
        Assert.NotEqual(held.SlotIndex, other.SlotIndex);
    }

    [Fact]
    public void ReturningAnUnrentedSlotIsAnError()
    {
        // A double return means two owners think they hold the same frame, which
        // would corrupt a recording. Fail loudly rather than silently aliasing.
        var allocator = new FakeTextureAllocator();
        using var pool = new TexturePool(allocator, 64, 64, 2);

        Assert.True(pool.TryRent(out var texture));
        pool.Return(texture.SlotIndex);

        Assert.Throws<InvalidOperationException>(() => pool.Return(texture.SlotIndex));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Return(99));
    }

    [Fact]
    public void FailedAllocationUnwindsEarlierTextures()
    {
        var allocator = new FakeTextureAllocator { FailAfter = 2 };

        Assert.Throws<InvalidOperationException>(
            () => new TexturePool(allocator, 1920, 1080, 4));

        Assert.Equal(2, allocator.Created.Count);
        Assert.Equal(allocator.Created, allocator.Destroyed);
    }

    [Fact]
    public void DisposeReleasesEveryTextureExactlyOnce()
    {
        var allocator = new FakeTextureAllocator();
        var pool = new TexturePool(allocator, 64, 64, 3);

        pool.Dispose();
        pool.Dispose();

        Assert.Equal(allocator.Created, allocator.Destroyed);
    }

    [Fact]
    public void RentAndReturnDoNotAllocate()
    {
        var allocator = new FakeTextureAllocator();
        using var pool = new TexturePool(allocator, 64, 64, 3);

        // Warm up so first-call JIT does not get counted as a steady-state cost.
        for (var i = 0; i < 100; i++)
        {
            pool.TryRentPreferring(0, out var warm);
            pool.Return(warm.SlotIndex);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            pool.TryRentPreferring(0, out var texture);
            pool.Return(texture.SlotIndex);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
