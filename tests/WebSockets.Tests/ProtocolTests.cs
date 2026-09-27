using System.Threading.Channels;

namespace WebSockets.Tests;

/// <summary>Wire-level behavior checked against a hand-rolled server.</summary>
public class ProtocolTests
{
    [Fact]
    public async Task Handshake_request_has_the_headers_the_standard_requires()
    {
        string request = "";
        using var server = new RawServer(r => RawServer.Accept(request = r, "Sec-WebSocket-Protocol: b"), async c => await c.ReadFrameAsync());
        var socket = new WebSocket(server.Url, ["a", "b"], new WebSocketOptions { Origin = "https://app.example" });
        var log = new EventLog(socket);
        await log.Opened;

        Assert.StartsWith("GET / HTTP/1.1\r\n", request);
        Assert.Contains("Upgrade: websocket\r\n", request);
        Assert.Contains("Connection: Upgrade\r\n", request);
        Assert.Contains("Sec-WebSocket-Version: 13\r\n", request);
        Assert.Contains("Sec-WebSocket-Protocol: a, b\r\n", request);
        Assert.Contains("Sec-WebSocket-Extensions: permessage-deflate; client_max_window_bits\r\n", request);
        Assert.Contains("Origin: https://app.example\r\n", request);
        string key = request.Split("\r\n").Single(l => l.StartsWith("Sec-WebSocket-Key: "))["Sec-WebSocket-Key: ".Length..];
        Assert.Equal(16, Convert.FromBase64String(key).Length);
        Assert.Equal("b", socket.Protocol);
    }

    [Fact]
    public async Task Close_without_arguments_sends_a_close_frame_with_no_body()
    {
        (int Opcode, byte[] Payload) received = default;
        using var server = new RawServer(r => RawServer.Accept(r), async c =>
        {
            received = await c.ReadFrameAsync();
            await c.SendFrameAsync(0x8, []);
        });
        var socket = new WebSocket(server.Url);
        var log = new EventLog(socket);
        await log.Opened;

        socket.Close();
        CloseEventArgs close = await log.Closed;

        Assert.Equal(0x8, received.Opcode);
        Assert.Empty(received.Payload);
        Assert.True(close.WasClean);
        Assert.Equal(1005, close.Code);
        Assert.Equal("", close.Reason);
    }

    [Fact]
    public async Task Close_with_code_and_reason_puts_both_in_the_frame()
    {
        (int Opcode, byte[] Payload) received = default;
        using var server = new RawServer(r => RawServer.Accept(r), async c =>
        {
            received = await c.ReadFrameAsync();
            await c.SendFrameAsync(0x8, received.Payload);
        });
        var socket = new WebSocket(server.Url);
        var log = new EventLog(socket);
        await log.Opened;

        socket.Close(3001, "gone");
        await log.Closed;

        Assert.Equal(RawConnection.ClosePayload(3001, "gone"), received.Payload);
    }

    [Fact]
    public async Task Close_with_only_a_reason_sends_no_body()
    {
        (int Opcode, byte[] Payload) received = default;
        using var server = new RawServer(r => RawServer.Accept(r), async c =>
        {
            received = await c.ReadFrameAsync();
            await c.SendFrameAsync(0x8, []);
        });
        var socket = new WebSocket(server.Url);
        var log = new EventLog(socket);
        await log.Opened;

        socket.Close(reason: "no code");
        await log.Closed;

        Assert.Empty(received.Payload);
    }

    [Fact]
    public async Task Server_close_without_a_code_is_answered_with_an_empty_close_frame()
    {
        (int Opcode, byte[] Payload) reply = default;
        using var server = new RawServer(r => RawServer.Accept(r), async c =>
        {
            await c.SendFrameAsync(0x8, []);
            reply = await c.ReadFrameAsync();
        });
        var socket = new WebSocket(server.Url);
        var log = new EventLog(socket);

        CloseEventArgs close = await log.Closed;
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(close.WasClean);
        Assert.Equal(1005, close.Code);
        Assert.Equal(0x8, reply.Opcode);
        Assert.Empty(reply.Payload);
    }

    [Fact]
    public async Task Server_close_code_is_echoed()
    {
        (int Opcode, byte[] Payload) reply = default;
        using var server = new RawServer(r => RawServer.Accept(r), async c =>
        {
            await c.SendFrameAsync(0x8, RawConnection.ClosePayload(1001, "restarting"));
            reply = await c.ReadFrameAsync();
        });
        var socket = new WebSocket(server.Url);
        var log = new EventLog(socket);

        CloseEventArgs close = await log.Closed;
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1001, close.Code);
        Assert.Equal("restarting", close.Reason);
        Assert.Equal(RawConnection.ClosePayload(1001), reply.Payload);
    }

    [Theory]
    [InlineData("bad-accept")]
    [InlineData("unrequested-protocol")]
    [InlineData("unoffered-extension")]
    [InlineData("bad-deflate-parameter")]
    [InlineData("missing-upgrade")]
    [InlineData("client-8-bit-window")]
    public async Task Invalid_handshake_responses_fail_the_connection(string kind)
    {
        using var server = new RawServer(
            r => kind switch
            {
                "bad-accept" => "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: AAAA\r\n\r\n",
                "unrequested-protocol" => RawServer.Accept(r, "Sec-WebSocket-Protocol: other"),
                "unoffered-extension" => RawServer.Accept(r, "Sec-WebSocket-Extensions: x-custom"),
                "bad-deflate-parameter" => RawServer.Accept(r, "Sec-WebSocket-Extensions: permessage-deflate; server_max_window_bits=20"),
                "client-8-bit-window" => RawServer.Accept(r, "Sec-WebSocket-Extensions: permessage-deflate; client_max_window_bits=8"),
                _ => RawServer.Accept(r).Replace("Upgrade: websocket\r\n", ""),
            },
            c => Task.Delay(1000));
        var socket = new WebSocket(server.Url, kind == "unrequested-protocol" ? ["chat"] : []);
        var log = new EventLog(socket);

        CloseEventArgs close = await log.Closed;

        Assert.False(close.WasClean);
        Assert.Equal(1006, close.Code);
        Assert.Equal(["error:Closed", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Extensions_reflect_the_servers_response()
    {
        using var server = new RawServer(
            r => RawServer.Accept(r, "Sec-WebSocket-Extensions: permessage-deflate; server_no_context_takeover; client_max_window_bits=10"),
            async c => await c.ReadFrameAsync());
        var socket = new WebSocket(server.Url);
        var log = new EventLog(socket);

        await log.Opened;

        Assert.Equal("permessage-deflate; server_no_context_takeover; client_max_window_bits=10", socket.Extensions);
        socket.Dispose();
    }

    [Fact]
    public async Task Server_8_bit_window_is_accepted()
    {
        using var server = new RawServer(
            r => RawServer.Accept(r, "Sec-WebSocket-Extensions: permessage-deflate; server_max_window_bits=8"),
            async c => await c.ReadFrameAsync());
        var socket = new WebSocket(server.Url);
        var log = new EventLog(socket);

        await log.Opened;

        Assert.Equal("permessage-deflate; server_max_window_bits=8", socket.Extensions);
        socket.Dispose();
    }

    [Fact]
    public async Task Close_after_the_handshake_but_before_open_is_dispatched_fails_the_connection()
    {
        using var server = new RawServer(r => RawServer.Accept(r), c => Task.Delay(1000));
        var context = new ManualContext();
        var socket = new WebSocket(server.Url, new WebSocketOptions { SynchronizationContext = context });
        var log = new EventLog(socket);
        Task<CloseEventArgs> closed = log.Closed;

        // The open task is posted but has not run, so the WebSocket is still CONNECTING.
        Action open = await context.NextPostAsync();
        Assert.Equal(WebSocketReadyState.Connecting, socket.ReadyState);
        socket.Close(1000);
        Assert.Equal(WebSocketReadyState.Closing, socket.ReadyState);
        open();
        while (!closed.IsCompleted)
        {
            (await context.NextPostAsync())();
        }

        CloseEventArgs close = await closed;
        Assert.False(close.WasClean);
        Assert.Equal(1006, close.Code);
        Assert.Equal(["error:Closed", "close:Closed"], log.Events);
        Assert.Equal("", socket.Extensions);
    }

    [Fact]
    public async Task A_throwing_exception_callback_does_not_stop_later_events()
    {
        using var server = new RawServer(r => RawServer.Accept(r), async c =>
        {
            await c.SendFrameAsync(0x8, []);
            await c.ReadFrameAsync();
        });

        // Constructed off the test's synchronization context, so events run on the WebSocket's own queue.
        (WebSocket socket, EventLog log) = await Task.Run(() =>
        {
            var s = new WebSocket(server.Url, new WebSocketOptions { EventHandlerException = _ => throw new InvalidOperationException("callback") });
            var l = new EventLog(s);
            s.OnOpen += (_, _) => throw new InvalidOperationException("handler");
            return (s, l);
        });

        CloseEventArgs close = await log.Closed;

        Assert.True(close.WasClean);
        Assert.Equal(["open:Open", "close:Closed"], log.Events);
        socket.Dispose();
    }

    [Fact]
    public async Task Redirects_fail_the_connection()
    {
        using var server = new RawServer(_ => "HTTP/1.1 302 Found\r\nLocation: ws://127.0.0.1:1/\r\nContent-Length: 0\r\n\r\n", _ => Task.CompletedTask);
        var socket = new WebSocket(server.Url);
        var log = new EventLog(socket);

        Assert.Equal(1006, (await log.Closed).Code);
        Assert.Equal(["error:Closed", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Message_received_after_close_is_called_is_dropped()
    {
        using var server = new RawServer(r => RawServer.Accept(r), async c =>
        {
            await c.SendFrameAsync(0x1, "early"u8.ToArray());
            var close = await c.ReadFrameAsync();
            await c.SendFrameAsync(0x1, "late"u8.ToArray());
            await c.SendFrameAsync(0x8, close.Payload);
        });
        var socket = new WebSocket(server.Url);
        var log = new EventLog(socket);
        socket.OnMessage += (_, _) => socket.Close(1000);

        await log.Closed;

        Assert.Equal(["open:Open", "message", "close:Closed"], log.Events);
    }

    /// <summary>Holds posted callbacks until the test runs them.</summary>
    private sealed class ManualContext : SynchronizationContext
    {
        private readonly Channel<Action> _posts = Channel.CreateUnbounded<Action>();

        public override void Post(SendOrPostCallback d, object? state) => _posts.Writer.TryWrite(() => d(state));

        public async Task<Action> NextPostAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _posts.Reader.ReadAsync(cts.Token);
        }
    }
}
