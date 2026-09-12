using Frost.Engine.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Frost.Engine.Windows;

/// <summary>
/// Backs <see cref="TexturePool"/> with real D3D11 textures.
/// </summary>
/// <remarks>
/// Only ever called at pool construction and disposal, so the managed wrapper
/// each texture carries costs nothing in steady state. The raw pointers handed
/// out to the pool are what the per-frame copy path uses.
/// </remarks>
internal sealed class D3D11TextureAllocator : IGpuTextureAllocator, IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly Dictionary<nint, ID3D11Texture2D> _textures = [];

    internal D3D11TextureAllocator(GraphicsDevice device) => _device = device;

    public nint Create(int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,

            // WGC hands us B8G8R8A8; staying in that format keeps the capture copy
            // a straight blit. Conversion to the encoder's NV12 happens once, in
            // the encode stage, on the GPU.
            Format = Format.B8G8R8A8_UNorm,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,

            // ShaderResource so the encode stage's colour conversion can sample
            // it; RenderTarget so a hardware video processor can write from it.
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        var texture = _device.Device.CreateTexture2D(description);
        var handle = texture.NativePointer;
        _textures[handle] = texture;
        return handle;
    }

    public void Destroy(nint handle)
    {
        if (_textures.Remove(handle, out var texture))
        {
            texture.Dispose();
        }
    }

    /// <summary>Managed wrapper for a handle this allocator created.</summary>
    internal ID3D11Texture2D Resolve(nint handle) => _textures[handle];

    public void Dispose()
    {
        foreach (var texture in _textures.Values)
        {
            texture.Dispose();
        }

        _textures.Clear();
    }
}
