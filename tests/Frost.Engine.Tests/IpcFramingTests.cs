using System.Buffers.Binary;
using Frost.Shared.Ipc;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class IpcFramingTests
{
    [Fact]
    public async Task RoundTripsAMessage()
    {
        using var stream = new MemoryStream();
        var sent = IpcMessage.ClipRequestFor(30, "30s", correlationId: 7);

        await IpcFraming.WriteAsync(stream, sent);
        stream.Position = 0;

        var received = await IpcFraming.ReadAsync(stream);

        Assert.NotNull(received);
        Assert.Equal(IpcMessageKind.RequestClip, received!.Kind);
        Assert.Equal(7, received.CorrelationId);
        Assert.Equal(30, received.ClipRequest!.Seconds);
        Assert.Equal("30s", received.ClipRequest.Label);
        Assert.Equal(FrostIpc.ProtocolVersion, received.Version);
    }

    [Fact]
    public async Task ReadsBackToBackMessagesInOrder()
    {
        using var stream = new MemoryStream();

        for (var i = 1; i <= 5; i++)
        {
            await IpcFraming.WriteAsync(stream, IpcMessage.Ack(i));
        }

        stream.Position = 0;

        for (var i = 1; i <= 5; i++)
        {
            var message = await IpcFraming.ReadAsync(stream);
            Assert.Equal(i, message!.CorrelationId);
        }

        Assert.Null(await IpcFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task ReassemblesAMessageDeliveredOneByteAtATime()
    {
        // The bug this exists to prevent: hand-rolled framing that reads once and
        // assumes it got a whole message. It works until a message crosses a
        // buffer boundary under load, then corrupts one frame in a thousand.
        using var buffer = new MemoryStream();
        var sent = IpcMessage.StatusReply(
            new EngineStatus { IsArmed = true, BufferedSeconds = 29.5, EncoderName = "Some Encoder MFT" },
            correlationId: 42);

        await IpcFraming.WriteAsync(buffer, sent);

        using var trickle = new TrickleStream(buffer.ToArray(), bytesPerRead: 1);
        var received = await IpcFraming.ReadAsync(trickle);

        Assert.NotNull(received);
        Assert.Equal(42, received!.CorrelationId);
        Assert.Equal(29.5, received.Status!.BufferedSeconds);
        Assert.Equal("Some Encoder MFT", received.Status.EncoderName);
        Assert.True(trickle.ReadCount > 10, "the test stream did not actually fragment the frame");
    }

    [Fact]
    public async Task ReassemblesWhenTheLengthPrefixItselfIsSplit()
    {
        using var buffer = new MemoryStream();
        await IpcFraming.WriteAsync(buffer, IpcMessage.Ack(9));

        using var trickle = new TrickleStream(buffer.ToArray(), bytesPerRead: 3);
        var received = await IpcFraming.ReadAsync(trickle);

        Assert.Equal(9, received!.CorrelationId);
    }

    [Fact]
    public async Task ACleanEndOfStreamIsNullNotAnError()
    {
        // Closing the Shell window is the normal case, not a fault.
        using var empty = new MemoryStream();
        Assert.Null(await IpcFraming.ReadAsync(empty));
    }

    [Fact]
    public async Task AStreamThatEndsMidPrefixIsAProtocolError()
    {
        using var stream = new MemoryStream([1, 2]);
        var ex = await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync(stream));
        Assert.Contains("length-prefix", ex.Message);
    }

    [Fact]
    public async Task AStreamThatEndsMidPayloadIsAProtocolError()
    {
        var frame = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 100);

        using var stream = new MemoryStream(frame);
        var ex = await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync(stream));
        Assert.Contains("payload bytes", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task ANonPositiveLengthPrefixIsRejected(int length)
    {
        var frame = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);

        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task AnOversizedLengthPrefixIsRejectedBeforeAnythingIsAllocated()
    {
        // A corrupt byte must not turn into a 2GB allocation inside the process
        // that is recording someone's game.
        var frame = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, int.MaxValue);

        using var stream = new MemoryStream(frame);
        var ex = await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync(stream));
        Assert.Contains("exceeds", ex.Message);
    }

    [Fact]
    public async Task GarbageInsideAWellFormedFrameIsAProtocolError()
    {
        var payload = "this is not json"u8.ToArray();
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);

        using var stream = new MemoryStream(frame);
        var ex = await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync(stream));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public async Task JsonNullIsAProtocolErrorRatherThanANullMessage()
    {
        var payload = "null"u8.ToArray();
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);

        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task NullArgumentsAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => IpcFraming.WriteAsync(null!, IpcMessage.Ack(1)));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => IpcFraming.WriteAsync(new MemoryStream(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => IpcFraming.ReadAsync(null!));
    }

    /// <summary>Returns a fixed few bytes per read, to force reassembly.</summary>
    private sealed class TrickleStream(byte[] data, int bytesPerRead) : Stream
    {
        private int _position;

        internal int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ReadCount++;
            var available = Math.Min(Math.Min(bytesPerRead, buffer.Length), data.Length - _position);

            if (available <= 0)
            {
                return 0;
            }

            data.AsSpan(_position, available).CopyTo(buffer);
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
