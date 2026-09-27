using System.Net.WebSockets;
using System.Text;

namespace WebSockets.Tests;

public class ConnectionTests(TestServer server) : IClassFixture<TestServer>
{
    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Opens_and_exposes_the_negotiated_protocol_and_extensions()
    {
        var socket = new WebSocket(server.Ws("/echo?id=" + NewId()), ["chat.v2", "chat.v1"]);
        var log = new EventLog(socket);

        await log.Opened;

        Assert.Equal(WebSocketReadyState.Open, socket.ReadyState);
        Assert.Equal("chat.v2", socket.Protocol);
        Assert.StartsWith("permessage-deflate", socket.Extensions);
        Assert.Equal(["open:Open"], log.Events);
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task Extensions_are_empty_when_permessage_deflate_is_not_offered()
    {
        var socket = new WebSocket(server.Ws("/echo?id=" + NewId()), new WebSocketOptions { PerMessageDeflate = false });
        var log = new EventLog(socket);

        await log.Opened;

        Assert.Equal("", socket.Extensions);
        Assert.Equal("", socket.Protocol);
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task Http_urls_connect_as_ws()
    {
        var socket = new WebSocket(server.BaseUrl + "/echo?id=" + NewId());
        var log = new EventLog(socket);

        await log.Opened;

        Assert.StartsWith("ws://", socket.Url);
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task Echoes_text_and_binary_messages_with_the_origin()
    {
        var socket = new WebSocket(server.Ws("/echo?id=" + NewId()));
        var log = new EventLog(socket);
        await log.Opened;

        socket.Send("héllo \U0001F600");
        MessageEventArgs text = await log.NextMessageAsync();
        Assert.Equal("héllo \U0001F600", Assert.IsType<string>(text.Data));
        Assert.Equal(new Uri(server.BaseUrl).GetLeftPart(UriPartial.Authority).Replace("http", "ws"), text.Origin);
        Assert.Equal("", text.LastEventId);

        socket.Send(new byte[] { 1, 2, 3 });
        var blob = Assert.IsType<Blob>((await log.NextMessageAsync()).Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, await blob.ArrayBufferAsync());

        socket.BinaryType = BinaryType.ArrayBuffer;
        socket.Send(new Blob([new byte[] { 4, 5 }]));
        Assert.Equal(new byte[] { 4, 5 }, Assert.IsType<byte[]>((await log.NextMessageAsync()).Data));

        await socket.DisposeAsync();
    }

    [Fact]
    public async Task Lone_surrogates_are_sent_as_replacement_characters()
    {
        string id = NewId();
        var socket = new WebSocket(server.Ws("/echo?id=" + id));
        var log = new EventLog(socket);
        await log.Opened;

        socket.Send("a\uD800b");

        Assert.Equal("a�b", (await log.NextMessageAsync()).Data);
        Assert.True(server.Record(id).Messages.TryPeek(out var received));
        Assert.Equal("a�b", Encoding.UTF8.GetString(received.Data));
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task Large_messages_are_reassembled_and_buffered_amount_drains()
    {
        var socket = new WebSocket(server.Ws("/echo?id=" + NewId()));
        var log = new EventLog(socket);
        socket.BinaryType = BinaryType.ArrayBuffer;
        await log.Opened;

        byte[] payload = new byte[1_000_000];
        Random.Shared.NextBytes(payload);
        socket.Send(payload);
        Assert.True(socket.BufferedAmount <= (ulong)payload.Length);

        Assert.Equal(payload, (await log.NextMessageAsync()).Data);
        Assert.Equal(0UL, socket.BufferedAmount);
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task Messages_arrive_in_order()
    {
        var socket = new WebSocket(server.Ws("/echo?id=" + NewId()));
        var log = new EventLog(socket);
        await log.Opened;

        for (int i = 0; i < 200; i++)
        {
            socket.Send(i.ToString());
        }

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(i.ToString(), (await log.NextMessageAsync()).Data);
        }

        await socket.DisposeAsync();
    }

    [Fact]
    public async Task Client_close_completes_the_handshake_with_code_and_reason()
    {
        string id = NewId();
        var socket = new WebSocket(server.Ws("/echo?id=" + id));
        var log = new EventLog(socket);
        await log.Opened;

        socket.Send("last");
        socket.Close(4000, "bye é");
        Assert.Equal(WebSocketReadyState.Closing, socket.ReadyState);

        CloseEventArgs close = await log.Closed;
        Assert.True(close.WasClean);
        Assert.Equal(4000, close.Code);
        Assert.Equal("bye é", close.Reason);
        Assert.Equal(WebSocketReadyState.Closed, socket.ReadyState);

        var (status, description) = await server.Record(id).Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((WebSocketCloseStatus)4000, status);
        Assert.Equal("bye é", description);
        Assert.Single(server.Record(id).Messages);

        // The echo of "last" arrived after close() was called, so no message event fires.
        Assert.Equal(["open:Open", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Server_close_delivers_pending_messages_then_a_clean_close()
    {
        var socket = new WebSocket(server.Ws("/server-close?messages=3&code=4001&reason=done"));
        var log = new EventLog(socket);

        CloseEventArgs close = await log.Closed;

        Assert.True(close.WasClean);
        Assert.Equal(4001, close.Code);
        Assert.Equal("done", close.Reason);
        Assert.Equal(["open:Open", "message", "message", "message", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Send_after_close_is_not_transmitted_and_buffered_amount_keeps_growing()
    {
        string id = NewId();
        var socket = new WebSocket(server.Ws("/echo?id=" + id));
        var log = new EventLog(socket);
        await log.Opened;

        socket.Close(1000);
        socket.Send("abc");
        Assert.Equal(3UL, socket.BufferedAmount);
        await log.Closed;
        socket.Send(new byte[4]);

        Assert.Equal(7UL, socket.BufferedAmount);
        Assert.Empty(server.Record(id).Messages);
    }

    [Fact]
    public async Task Refused_handshake_fires_error_then_close()
    {
        var socket = new WebSocket(server.Ws("/forbidden"));
        var log = new EventLog(socket);

        CloseEventArgs close = await log.Closed;

        Assert.False(close.WasClean);
        Assert.Equal(1006, close.Code);
        Assert.Equal(["error:Closed", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Unreachable_server_fires_error_then_close()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var socket = new WebSocket($"ws://127.0.0.1:{port}/");
        var log = new EventLog(socket);

        Assert.Equal(1006, (await log.Closed).Code);
        Assert.Equal(["error:Closed", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Missing_subprotocol_in_the_response_fails_the_connection()
    {
        var socket = new WebSocket(server.Ws("/echo?protocol=none&id=" + NewId()), "chat");
        var log = new EventLog(socket);

        CloseEventArgs close = await log.Closed;

        Assert.False(close.WasClean);
        Assert.Equal(1006, close.Code);
        Assert.Equal(["error:Closed", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Dropped_connection_fires_error_then_close_1006()
    {
        var socket = new WebSocket(server.Ws("/drop"));
        var log = new EventLog(socket);

        CloseEventArgs close = await log.Closed;

        Assert.False(close.WasClean);
        Assert.Equal(1006, close.Code);
        Assert.Equal(["open:Open", "error:Closed", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Server_that_never_answers_close_is_dropped_after_the_timeout()
    {
        var socket = new WebSocket(server.Ws("/echo?silent=1&id=" + NewId()), new WebSocketOptions { CloseTimeout = TimeSpan.FromMilliseconds(300) });
        var log = new EventLog(socket);
        await log.Opened;

        socket.Close(1000);
        CloseEventArgs close = await log.Closed;

        Assert.False(close.WasClean);
        Assert.Equal(1006, close.Code);
    }

    [Fact]
    public async Task Exceeding_max_buffered_amount_flags_full_and_closes()
    {
        var socket = new WebSocket(server.Ws("/echo?id=" + NewId()), new WebSocketOptions { MaxBufferedAmount = 10 });
        var log = new EventLog(socket);
        await log.Opened;

        socket.Send(new byte[11]);
        CloseEventArgs close = await log.Closed;

        Assert.False(close.WasClean);
        Assert.Equal(1006, close.Code);
        Assert.Equal(["open:Open", "error:Closed", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Origin_option_is_sent_and_protocols_are_requested_in_order()
    {
        string id = NewId();
        var socket = new WebSocket(server.Ws("/echo?id=" + id), ["b", "a"], new WebSocketOptions { Origin = "https://app.example" });
        var log = new EventLog(socket);
        await log.Opened;

        Assert.Equal("https://app.example", server.Record(id).Origin);
        Assert.Equal("b, a", server.Record(id).RequestedProtocols);
        await socket.DisposeAsync();
    }

    [Fact]
    public async Task Events_are_posted_to_the_synchronization_context()
    {
        var context = new RecordingContext();
        var socket = new WebSocket(server.Ws("/server-close?messages=1"), new WebSocketOptions { SynchronizationContext = context });
        var log = new EventLog(socket);

        await log.Closed;

        // open, message, the CLOSING state change when the server's Close frame arrives, close.
        Assert.Equal(4, context.Posts);
        Assert.Equal(["open:Open", "message", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Handler_exceptions_are_reported_and_do_not_stop_later_events()
    {
        var errors = new List<Exception>();
        var socket = new WebSocket(server.Ws("/server-close?messages=2"), new WebSocketOptions { EventHandlerException = errors.Add });
        socket.OnMessage += (_, _) => throw new InvalidOperationException("boom");
        var log = new EventLog(socket);

        await log.Closed;

        Assert.Equal(2, errors.Count);
        Assert.Equal("close:Closed", log.Events.Last());
    }

    [Fact]
    public async Task Dispose_async_performs_the_closing_handshake()
    {
        string id = NewId();
        var socket = new WebSocket(server.Ws("/echo?id=" + id));
        var log = new EventLog(socket);
        await log.Opened;

        await socket.DisposeAsync();

        Assert.True((await log.Closed).WasClean);
        Assert.True(server.Record(id).Closed.Task.IsCompleted);
    }

    [Fact]
    public async Task Dispose_drops_the_connection()
    {
        var socket = new WebSocket(server.Ws("/echo?id=" + NewId()));
        var log = new EventLog(socket);
        await log.Opened;

        socket.Dispose();
        CloseEventArgs close = await log.Closed;

        Assert.False(close.WasClean);
        Assert.Equal(1006, close.Code);
        Assert.Equal(["open:Open", "close:Closed"], log.Events);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => _posts;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            base.Post(d, state);
        }
    }
}
