using System.Buffers.Binary;
using System.Text.Json;

namespace Frost.Shared.Ipc;

/// <summary>Raised when the other end sends something that is not a valid frame.</summary>
public sealed class IpcProtocolException(string message) : Exception(message);

/// <summary>
/// Length-prefixed JSON frames over a stream.
/// </summary>
/// <remarks>
/// <para>A pipe in byte mode is a stream, not a datagram source: one
/// <c>ReadAsync</c> can return half a message, or two messages, or one and a
/// half. Every read here therefore loops until it has the bytes it asked for,
/// which is the single most common way hand-rolled IPC goes wrong — it works
/// perfectly until a message crosses a buffer boundary under load.</para>
///
/// <para>The length prefix is validated before anything is allocated for it. An
/// unchecked prefix is how a corrupt byte turns into a 2GB allocation in the
/// process that is supposed to be recording someone's game.</para>
/// </remarks>
public static class IpcFraming
{
    private const int PrefixBytes = sizeof(int);

    /// <summary>Serialises and writes one frame.</summary>
    public static async Task WriteAsync(
        Stream stream, IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, FrostIpcJsonContext.Default.IpcMessage);

        if (payload.Length > FrostIpc.MaxFrameBytes)
        {
            throw new IpcProtocolException(
                $"Message of {payload.Length} bytes exceeds the {FrostIpc.MaxFrameBytes}-byte frame limit.");
        }

        var frame = new byte[PrefixBytes + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(PrefixBytes));

        // One write for prefix and payload together: two writes can be observed
        // as a torn frame by a reader that is cancelled between them.
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame, or returns null when the stream ends cleanly.
    /// </summary>
    /// <remarks>
    /// A clean end of stream is the normal way a Shell goes away — the user
    /// closed the window — so it is a null, not an exception. A stream that ends
    /// <i>mid-frame</i> is a real protocol error and does throw.
    /// </remarks>
    public static async Task<IpcMessage?> ReadAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[PrefixBytes];
        var prefixRead = await ReadExactlyOrEndAsync(stream, prefix, cancellationToken).ConfigureAwait(false);

        if (prefixRead == 0)
        {
            return null;
        }

        if (prefixRead < PrefixBytes)
        {
            throw new IpcProtocolException(
                $"Stream ended after {prefixRead} of {PrefixBytes} length-prefix bytes.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);

        // Validate before allocating. This is the whole reason the cap exists.
        if (length <= 0)
        {
            throw new IpcProtocolException($"Frame length {length} is not positive.");
        }

        if (length > FrostIpc.MaxFrameBytes)
        {
            throw new IpcProtocolException(
                $"Frame length {length} exceeds the {FrostIpc.MaxFrameBytes}-byte limit.");
        }

        var payload = new byte[length];
        var payloadRead = await ReadExactlyOrEndAsync(stream, payload, cancellationToken).ConfigureAwait(false);

        if (payloadRead < length)
        {
            throw new IpcProtocolException(
                $"Stream ended after {payloadRead} of {length} payload bytes.");
        }

        IpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(payload, FrostIpcJsonContext.Default.IpcMessage);
        }
        catch (JsonException ex)
        {
            throw new IpcProtocolException($"Frame payload was not valid JSON: {ex.Message}");
        }

        if (message is null)
        {
            throw new IpcProtocolException("Frame payload deserialised to null.");
        }

        return message;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/>, returning how many bytes were read before
    /// the stream ended.
    /// </summary>
    /// <remarks>
    /// <c>Stream.ReadExactlyAsync</c> exists in .NET 7+, but it throws on a clean
    /// end of stream, and a clean end of stream is the expected case here. So the
    /// loop is written out, and the caller distinguishes "nothing at all" from
    /// "ended mid-frame".
    /// </remarks>
    private static async Task<int> ReadExactlyOrEndAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }
}
