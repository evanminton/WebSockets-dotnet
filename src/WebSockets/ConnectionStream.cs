using System.Security.Cryptography;

namespace WebSockets;

/// <summary>
/// Wraps the upgraded connection to cover the one thing <see cref="System.Net.WebSockets.WebSocket"/> gets wrong for the
/// standard: Close frames without a status code. It cannot send one, and reports a received one as 1000.
/// Writes are serialized (each frame is written in one call) so <see cref="SendEmptyCloseFrameAsync"/> can slip in a frame,
/// and incoming frame headers are tracked so <see cref="ReceivedEmptyCloseFrame"/> can tell 1005 from 1000.
/// </summary>
internal sealed class ConnectionStream(Stream inner) : Stream
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[14];
    private int _headerLength;
    private long _payloadRemaining;

    /// <summary>Whether a Close frame with an empty body has been read from the connection.</summary>
    public bool ReceivedEmptyCloseFrame { get; private set; }

    public override bool CanRead => inner.CanRead;

    public override bool CanWrite => inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Writes a masked Close frame with an empty body. <see cref="System.Net.WebSockets.WebSocket.CloseOutputAsync"/>
    /// cannot do this: given <c>WebSocketCloseStatus.Empty</c> it puts 1005 on the wire, which RFC 6455 forbids.
    /// </summary>
    public async Task SendEmptyCloseFrameAsync(CancellationToken cancellationToken)
    {
        byte[] frame = new byte[6];
        frame[0] = 0x88; // FIN + Close
        frame[1] = 0x80; // masked, payload length 0
        RandomNumberGenerator.Fill(frame.AsSpan(2));
        await WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Track(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Track(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Follows RFC 6455 frame boundaries through the byte stream the frame reader consumes.
    private void Track(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_payloadRemaining > 0)
            {
                int skip = (int)Math.Min(_payloadRemaining, data.Length);
                _payloadRemaining -= skip;
                data = data[skip..];
                continue;
            }

            _header[_headerLength++] = data[0];
            data = data[1..];
            if (_headerLength < 2)
            {
                continue;
            }

            int lengthCode = _header[1] & 0x7F;
            int extendedLength = lengthCode switch { 126 => 2, 127 => 8, _ => 0 };
            int maskLength = (_header[1] & 0x80) != 0 ? 4 : 0;
            if (_headerLength < 2 + extendedLength + maskLength)
            {
                continue;
            }

            long payloadLength = extendedLength switch
            {
                2 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(2)),
                8 => (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(2)),
                _ => lengthCode,
            };
            if ((_header[0] & 0x0F) == 0x8 && payloadLength == 0)
            {
                ReceivedEmptyCloseFrame = true;
            }

            _payloadRemaining = payloadLength;
            _headerLength = 0;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _writeLock.Wait();
        try
        {
            inner.Write(buffer, offset, count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
