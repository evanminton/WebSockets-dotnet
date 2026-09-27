using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WebSockets.Tests;

/// <summary>
/// A hand-rolled WebSocket server for one connection, so tests can see and produce exact frames and handshake
/// responses that System.Net.WebSockets would refuse to send.
/// </summary>
public sealed class RawServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public RawServer(Func<string, string> response, Func<RawConnection, Task> script)
    {
        _listener.Start();
        Completion = RunAsync(response, script);
    }

    public string Url => $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

    public Task Completion { get; }

    /// <summary>A 101 response for the request, plus any extra header lines.</summary>
    public static string Accept(string request, params string[] extraHeaders)
    {
        string key = request.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = new StringBuilder("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n");
        response.Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n");
        foreach (string header in extraHeaders)
        {
            response.Append(header).Append("\r\n");
        }

        return response.Append("\r\n").ToString();
    }

    private async Task RunAsync(Func<string, string> response, Func<RawConnection, Task> script)
    {
        using Socket socket = await _listener.AcceptSocketAsync();
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = new StringBuilder();
        var one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n"))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                return;
            }

            request.Append((char)one[0]);
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response(request.ToString())));
        await script(new RawConnection(stream, request.ToString()));
    }

    public void Dispose() => _listener.Stop();
}

public sealed class RawConnection(Stream stream, string request)
{
    public string Request { get; } = request;

    public async Task<(int Opcode, byte[] Payload)> ReadFrameAsync()
    {
        byte[] header = await ReadExactlyAsync(2);
        int opcode = header[0] & 0x0F;
        long length = header[1] & 0x7F;
        if (length == 126)
        {
            byte[] ext = await ReadExactlyAsync(2);
            length = (ext[0] << 8) | ext[1];
        }
        else if (length == 127)
        {
            byte[] ext = await ReadExactlyAsync(8);
            length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ext);
        }

        byte[] mask = (header[1] & 0x80) != 0 ? await ReadExactlyAsync(4) : [0, 0, 0, 0];
        byte[] payload = await ReadExactlyAsync((int)length);
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] ^= mask[i % 4];
        }

        return (opcode, payload);
    }

    public async Task SendFrameAsync(int opcode, byte[] payload)
    {
        if (payload.Length > 125)
        {
            throw new NotSupportedException("Only short frames are needed in tests.");
        }

        await stream.WriteAsync(new byte[] { (byte)(0x80 | opcode), (byte)payload.Length }.Concat(payload).ToArray());
    }

    public static byte[] ClosePayload(ushort code, string reason = "") =>
        new byte[] { (byte)(code >> 8), (byte)code }.Concat(Encoding.UTF8.GetBytes(reason)).ToArray();

    private async Task<byte[]> ReadExactlyAsync(int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }
}
