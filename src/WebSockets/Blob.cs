using System.Text;

namespace WebSockets;

/// <summary>
/// An immutable chunk of bytes with a MIME type, following the File API <c>Blob</c> interface.
/// Binary messages are delivered as <see cref="Blob"/> objects when <see cref="WebSocket.BinaryType"/>
/// is <see cref="WebSockets.BinaryType.Blob"/>, and a <see cref="Blob"/> can be passed to <see cref="WebSocket.Send(Blob)"/>.
/// </summary>
public sealed class Blob
{
    private readonly ReadOnlyMemory<byte> _data;

    /// <summary>Creates an empty blob.</summary>
    public Blob()
        : this(ReadOnlyMemory<byte>.Empty, "")
    {
    }

    /// <summary>Creates a blob holding a copy of <paramref name="data"/>.</summary>
    public Blob(ReadOnlySpan<byte> data, string type = "")
        : this(data.ToArray(), type, copy: false)
    {
    }

    /// <summary>
    /// Creates a blob from a sequence of parts. Each part is a <see cref="string"/> (encoded as UTF-8),
    /// a <see cref="byte"/> array, an <see cref="ArraySegment{T}"/> or <see cref="ReadOnlyMemory{T}"/> of bytes, or another <see cref="Blob"/>.
    /// </summary>
    public Blob(IEnumerable<object> blobParts, string type = "")
        : this(Concat(blobParts), type, copy: false)
    {
    }

    private Blob(ReadOnlyMemory<byte> data, string type, bool copy = false)
    {
        _data = copy ? data.ToArray() : data;
        Type = NormalizeType(type);
    }

    internal static Blob Wrap(byte[] data) => new(data, "", copy: false);

    /// <summary>The size of the blob in bytes.</summary>
    public long Size => _data.Length;

    /// <summary>The ASCII-lowercased MIME type, or the empty string when unknown.</summary>
    public string Type { get; }

    /// <summary>A read-only view of the blob's bytes, without copying.</summary>
    public ReadOnlyMemory<byte> Memory => _data;

    /// <summary>Returns a new blob holding the bytes in the range [<paramref name="start"/>, <paramref name="end"/>). Negative values count from the end.</summary>
    public Blob Slice(long? start = null, long? end = null, string? contentType = null)
    {
        long relativeStart = Relative(start ?? 0);
        long relativeEnd = Relative(end ?? Size);
        long span = Math.Max(relativeEnd - relativeStart, 0);
        return new Blob(_data.Slice((int)relativeStart, (int)span), contentType ?? "");
    }

    /// <summary>Returns a copy of the blob's bytes (the counterpart of <c>arrayBuffer()</c>).</summary>
    public Task<byte[]> ArrayBufferAsync() => Task.FromResult(_data.ToArray());

    /// <summary>Returns a copy of the blob's bytes (the counterpart of <c>bytes()</c>).</summary>
    public Task<byte[]> BytesAsync() => Task.FromResult(_data.ToArray());

    /// <summary>Decodes the blob as UTF-8, replacing invalid sequences (the counterpart of <c>text()</c>).</summary>
    public Task<string> TextAsync() => Task.FromResult(Encoding.UTF8.GetString(StripBom(_data.Span)));

    /// <summary>Returns a read-only stream over the blob's bytes (the counterpart of <c>stream()</c>).</summary>
    public Stream Stream() => new MemoryStream(_data.ToArray(), writable: false);

    private long Relative(long value) =>
        value < 0 ? Math.Max(Size + value, 0) : Math.Min(value, Size);

    private static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("﻿"u8) ? bytes[3..] : bytes;

    private static string NormalizeType(string type)
    {
        foreach (char c in type)
        {
            if (c < ' ' || c > '~')
            {
                return "";
            }
        }

        return type.ToLowerInvariant();
    }

    private static byte[] Concat(IEnumerable<object> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        using var buffer = new MemoryStream();
        foreach (object part in parts)
        {
            switch (part)
            {
                case string s:
                    buffer.Write(Encoding.UTF8.GetBytes(s));
                    break;
                case byte[] bytes:
                    buffer.Write(bytes);
                    break;
                case ArraySegment<byte> segment:
                    buffer.Write(segment);
                    break;
                case ReadOnlyMemory<byte> memory:
                    buffer.Write(memory.Span);
                    break;
                case Memory<byte> memory:
                    buffer.Write(memory.Span);
                    break;
                case Blob blob:
                    buffer.Write(blob._data.Span);
                    break;
                default:
                    throw new ArgumentException($"Unsupported blob part type {part?.GetType().Name ?? "null"}.", nameof(parts));
            }
        }

        return buffer.ToArray();
    }
}
