using Frost.Engine.Capture;
using Frost.Engine.Pipeline;

namespace Frost.Engine.Tests;

/// <summary>Captures what the pipeline handed downstream, and can refuse frames.</summary>
internal sealed class RecordingFrameSink : IFrameSink
{
    internal RecordingFrameSink(TexturePool? poolToReturnTo = null) => Pool = poolToReturnTo;

    /// <summary>Pool that owns the slots currently arriving; swapped on a router rebind.</summary>
    internal TexturePool? Pool { get; set; }

    internal List<CapturedFrame> Accepted { get; } = [];

    /// <summary>When set, the sink refuses everything — the "encoder is wedged" case.</summary>
    internal bool RefuseEverything { get; set; }

    /// <summary>When true, slots are returned to the pool as soon as they arrive.</summary>
    internal bool ReturnImmediately { get; set; } = true;

    public bool TryAccept(in CapturedFrame frame)
    {
        if (RefuseEverything)
        {
            return false;
        }

        Accepted.Add(frame);

        if (ReturnImmediately)
        {
            Pool?.Return(frame.SlotIndex);
        }

        return true;
    }

    internal void ReturnAll()
    {
        foreach (var frame in Accepted)
        {
            Pool?.Return(frame.SlotIndex);
        }

        Accepted.Clear();
    }
}

/// <summary>Records copies instead of performing them.</summary>
internal sealed class RecordingFrameCopier : IFrameCopier
{
    internal List<(nint Source, nint Destination)> SourceCopies { get; } = [];

    internal List<(nint Source, nint Destination)> PooledCopies { get; } = [];

    public void CopyFromSource(nint sourceTexture, nint destinationTexture, int width, int height) =>
        SourceCopies.Add((sourceTexture, destinationTexture));

    public void CopyPooled(nint sourceTexture, nint destinationTexture, int width, int height) =>
        PooledCopies.Add((sourceTexture, destinationTexture));
}
