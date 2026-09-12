using System.Runtime.InteropServices;
using WinRT;

namespace Frost.Engine.Windows.Interop;

/// <summary>
/// Bridges CsWinRT projection objects to the raw ABI pointers the per-frame path
/// uses.
/// </summary>
/// <remarks>
/// The projection is fine for setup — creating a device, a frame pool, a session
/// — because that happens once. It is not fine per frame: every projected call
/// that returns a runtime class allocates a managed wrapper, which at 60fps is a
/// steady stream of Gen0 garbage on the capture thread. So setup goes through the
/// projection and then we take the interface pointer and call vtables directly.
/// </remarks>
internal static class WinRtAbi
{
    /// <summary>
    /// Takes a reference to <paramref name="projected"/>'s default interface. The
    /// caller owns the returned pointer and must release it.
    /// </summary>
    internal static nint AddRefDefaultInterface<T>(T projected)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(projected);

        if (projected is not IWinRTObject winrt)
        {
            throw new ArgumentException(
                $"{typeof(T).Name} is not a CsWinRT projected object.", nameof(projected));
        }

        var thisPtr = winrt.NativeObject.ThisPtr;
        if (thisPtr == 0)
        {
            throw new InvalidOperationException($"{typeof(T).Name} has no native object.");
        }

        // Prefer an explicit QI onto the type's own IID so we are certain the
        // vtable we are about to index is the one we think it is. The IID comes
        // from the projection's metadata rather than a literal in our source,
        // so it cannot drift. If that lookup is not available for this type,
        // the default interface pointer is already the right vtable.
        try
        {
            var iid = GuidGenerator.GetIID(typeof(T));
            var specific = ComHelpers.TryQueryInterface(thisPtr, iid);
            if (specific != 0)
            {
                return specific;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fall through to the default interface pointer below.
        }

        Marshal.AddRef(thisPtr);
        return thisPtr;
    }
}
