using Frost.Engine.Capture;

namespace Frost.Engine.Tests;

/// <summary>Stands in for D3D11 so the pool's bookkeeping is testable without a GPU.</summary>
internal sealed class FakeTextureAllocator : IGpuTextureAllocator
{
    private nint _next = 0x1000;

    internal List<nint> Created { get; } = [];

    internal List<nint> Destroyed { get; } = [];

    internal int FailAfter { get; set; } = int.MaxValue;

    public nint Create(int width, int height)
    {
        if (Created.Count >= FailAfter)
        {
            return 0;
        }

        var handle = _next;
        _next += 0x1000;
        Created.Add(handle);
        return handle;
    }

    public void Destroy(nint handle) => Destroyed.Add(handle);
}
