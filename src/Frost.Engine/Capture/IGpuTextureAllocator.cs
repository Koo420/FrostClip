namespace Frost.Engine.Capture;

/// <summary>
/// The one thing <see cref="TexturePool"/> needs from the graphics API, factored
/// out so the pool's bookkeeping is testable without a GPU.
/// </summary>
public interface IGpuTextureAllocator
{
    /// <summary>Allocates a texture and returns an opaque handle. Called only at pool construction.</summary>
    nint Create(int width, int height);

    /// <summary>Releases a texture. Called only at pool disposal.</summary>
    void Destroy(nint handle);
}
