using System.Runtime.CompilerServices;

namespace Frost.Engine.Capture;

/// <summary>A texture rented from a <see cref="TexturePool"/>.</summary>
public readonly struct PooledTexture
{
    internal PooledTexture(nint handle, int slotIndex)
    {
        Handle = handle;
        SlotIndex = slotIndex;
    }

    public nint Handle { get; }

    public int SlotIndex { get; }

    public bool IsValid => SlotIndex >= 0;
}

/// <summary>
/// Fixed-size pool of GPU textures for the capture→encode handoff. Every texture
/// is allocated once at construction; renting and returning touch nothing but an
/// int array, so the hot path neither allocates nor takes a lock.
/// </summary>
/// <remarks>
/// Thread model: exactly one renter (the capture thread), any number of returners
/// (whatever thread finished with the frame). Slot ownership is transferred with
/// a single interlocked state flip per slot, so there is no shared mutable
/// structure to tear.
/// </remarks>
public sealed class TexturePool : IDisposable
{
    private const int Free = 0;
    private const int Rented = 1;

    private readonly IGpuTextureAllocator _allocator;
    private readonly nint[] _handles;
    private readonly int[] _state;
    private int _disposed;

    public TexturePool(IGpuTextureAllocator allocator, int width, int height, int capacity)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be >= 1.");
        }

        if (width < 1 || height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid texture size {width}x{height}.");
        }

        _allocator = allocator;
        Width = width;
        Height = height;
        _handles = new nint[capacity];
        _state = new int[capacity];

        for (var i = 0; i < capacity; i++)
        {
            var handle = allocator.Create(width, height);
            if (handle == 0)
            {
                // Unwind what we already took so a failed pool does not leak VRAM.
                for (var j = 0; j < i; j++)
                {
                    allocator.Destroy(_handles[j]);
                }

                throw new InvalidOperationException(
                    $"GPU texture allocation failed for slot {i} of {capacity} at {width}x{height}.");
            }

            _handles[i] = handle;
        }
    }

    public int Width { get; }

    public int Height { get; }

    public int Capacity => _handles.Length;

    /// <summary>Free slots right now. Diagnostic only; racy by nature.</summary>
    public int Available
    {
        get
        {
            var free = 0;
            for (var i = 0; i < _state.Length; i++)
            {
                if (Volatile.Read(ref _state[i]) == Free)
                {
                    free++;
                }
            }

            return free;
        }
    }

    /// <summary>
    /// Rents any free texture. Allocation-free.
    /// </summary>
    public bool TryRent(out PooledTexture texture) => TryRentPreferring(-1, out texture);

    /// <summary>
    /// Rents a texture, trying <paramref name="preferredSlot"/> first.
    /// </summary>
    /// <remarks>
    /// Capture uses this to ask for the slot it most recently emitted. If that
    /// slot is free again, its pixels are still the previous frame's — nothing
    /// else writes to this pool — so a filler frame can reuse it with no GPU copy
    /// at all. It is only ever an optimisation: any free slot is correct.
    /// </remarks>
    public bool TryRentPreferring(int preferredSlot, out PooledTexture texture)
    {
        if ((uint)preferredSlot < (uint)_state.Length &&
            Interlocked.CompareExchange(ref _state[preferredSlot], Rented, Free) == Free)
        {
            texture = new PooledTexture(_handles[preferredSlot], preferredSlot);
            return true;
        }

        for (var i = 0; i < _state.Length; i++)
        {
            if (i != preferredSlot &&
                Interlocked.CompareExchange(ref _state[i], Rented, Free) == Free)
            {
                texture = new PooledTexture(_handles[i], i);
                return true;
            }
        }

        texture = default;
        return false;
    }

    /// <summary>Returns a rented slot. Allocation-free. Idempotent-safe to the extent that a double return is caught.</summary>
    public void Return(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)_state.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Slot is not from this pool.");
        }

        if (Interlocked.CompareExchange(ref _state[slotIndex], Free, Rented) != Rented)
        {
            throw new InvalidOperationException(
                $"Texture pool slot {slotIndex} was returned while not rented. " +
                "That means two owners believe they hold the same frame.");
        }
    }

    /// <summary>Handle for a slot, for the capture implementation's copy calls.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public nint HandleAt(int slotIndex) => _handles[slotIndex];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (var i = 0; i < _handles.Length; i++)
        {
            if (_handles[i] != 0)
            {
                _allocator.Destroy(_handles[i]);
                _handles[i] = 0;
            }
        }
    }
}
