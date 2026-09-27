namespace WebSockets.Tests;

public class MethodValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(1001)]
    [InlineData(1005)]
    [InlineData(2999)]
    [InlineData(5000)]
    [InlineData(65535)]
    public void Close_rejects_codes_other_than_1000_and_3000_to_4999(int code)
    {
        using var hole = new BlackHole();
        using var socket = new WebSocket(hole.Url);

        var ex = Assert.Throws<DomException>(() => socket.Close((ushort)code));
        Assert.Equal(DomException.InvalidAccessError, ex.Name);
        Assert.Equal(WebSocketReadyState.Connecting, socket.ReadyState);
    }

    [Fact]
    public void Close_rejects_reasons_longer_than_123_utf8_bytes()
    {
        using var hole = new BlackHole();
        using var socket = new WebSocket(hole.Url);

        // 62 two-byte characters = 124 bytes, although the string is only 62 chars long.
        var ex = Assert.Throws<DomException>(() => socket.Close(1000, new string('é', 62)));
        Assert.Equal(DomException.SyntaxError, ex.Name);
        Assert.Equal(WebSocketReadyState.Connecting, socket.ReadyState);
    }

    [Fact]
    public void Close_validates_arguments_even_when_already_closed()
    {
        using var hole = new BlackHole();
        using var socket = new WebSocket(hole.Url);
        socket.Close();

        Assert.Throws<DomException>(() => socket.Close(2000));
    }

    [Fact]
    public void Send_while_connecting_throws_InvalidStateError()
    {
        using var hole = new BlackHole();
        using var socket = new WebSocket(hole.Url);

        Assert.Equal(DomException.InvalidStateError, Assert.Throws<DomException>(() => socket.Send("hi")).Name);
        Assert.Equal(DomException.InvalidStateError, Assert.Throws<DomException>(() => socket.Send(new byte[3])).Name);
        Assert.Equal(DomException.InvalidStateError, Assert.Throws<DomException>(() => socket.Send(new Blob())).Name);
        Assert.Equal(0UL, socket.BufferedAmount);
    }

    [Fact]
    public async Task Close_while_connecting_fails_the_connection()
    {
        using var hole = new BlackHole();
        var socket = new WebSocket(hole.Url);
        var log = new EventLog(socket);

        socket.Close(1000, "never mind");
        Assert.Equal(WebSocketReadyState.Closing, socket.ReadyState);

        CloseEventArgs close = await log.Closed;
        Assert.False(close.WasClean);
        Assert.Equal(1006, close.Code);
        Assert.Equal("", close.Reason);
        Assert.Equal(["error:Closed", "close:Closed"], log.Events);
    }

    [Fact]
    public async Task Send_after_failure_only_grows_buffered_amount()
    {
        using var hole = new BlackHole();
        var socket = new WebSocket(hole.Url);
        var log = new EventLog(socket);
        socket.Close();
        await log.Closed;

        socket.Send("héllo");
        socket.Send(new byte[10]);
        socket.Send(new Blob([new byte[5]]));
        Assert.Equal(6UL + 10 + 5, socket.BufferedAmount);
    }
}
