namespace WebSockets.Tests;

public class ConstructorTests
{
    [Theory]
    [InlineData("ws://example.com", "ws://example.com/")]
    [InlineData("wss://example.com:8443/chat?room=1", "wss://example.com:8443/chat?room=1")]
    [InlineData("http://example.com/a", "ws://example.com/a")]
    [InlineData("https://example.com/a", "wss://example.com/a")]
    [InlineData("http://example.com:8080/", "ws://example.com:8080/")]
    [InlineData("HTTPS://EXAMPLE.com", "wss://example.com/")]
    public void Url_is_parsed_and_http_schemes_are_mapped(string input, string expected)
    {
        Assert.Equal(expected, WebSocket.ParseUrl(input, null).AbsoluteUri);
    }

    [Theory]
    [InlineData("/chat", "https://example.com/app/", "wss://example.com/chat")]
    [InlineData("chat", "http://example.com/app/", "ws://example.com/app/chat")]
    [InlineData("ws://other.test/x", "https://example.com/", "ws://other.test/x")]
    public void Relative_urls_resolve_against_the_base_url(string input, string baseUrl, string expected)
    {
        Assert.Equal(expected, WebSocket.ParseUrl(input, new Uri(baseUrl)).AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/chat")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///tmp/x")]
    [InlineData("ws://example.com/#")]
    [InlineData("ws://example.com/#frag")]
    public void Invalid_urls_throw_SyntaxError(string url)
    {
        var ex = Assert.Throws<DomException>(() => new WebSocket(url));
        Assert.Equal(DomException.SyntaxError, ex.Name);
        Assert.Equal(12, ex.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a,b")]
    [InlineData("café")]
    [InlineData("x\"y")]
    public void Invalid_subprotocols_throw_SyntaxError(string protocol)
    {
        var ex = Assert.Throws<DomException>(() => new WebSocket("ws://127.0.0.1:1/", protocol));
        Assert.Equal(DomException.SyntaxError, ex.Name);
    }

    [Fact]
    public void Repeated_subprotocols_throw_SyntaxError()
    {
        var ex = Assert.Throws<DomException>(() => new WebSocket("ws://127.0.0.1:1/", ["chat", "json", "chat"]));
        Assert.Equal(DomException.SyntaxError, ex.Name);
    }

    [Fact]
    public void Initial_attributes_match_the_standard()
    {
        using var hole = new BlackHole();
        using var socket = new WebSocket(hole.Url, ["chat.v1", "json"]);

        Assert.Equal(hole.Url, socket.Url);
        Assert.Equal(WebSocketReadyState.Connecting, socket.ReadyState);
        Assert.Equal(0UL, socket.BufferedAmount);
        Assert.Equal("", socket.Protocol);
        Assert.Equal("", socket.Extensions);
        Assert.Equal(BinaryType.Blob, socket.BinaryType);

        socket.BinaryType = BinaryType.ArrayBuffer;
        Assert.Equal(BinaryType.ArrayBuffer, socket.BinaryType);
    }

    [Fact]
    public void Ready_state_values_match_the_idl_constants()
    {
        Assert.Equal(0, (int)WebSocketReadyState.Connecting);
        Assert.Equal(1, (int)WebSocketReadyState.Open);
        Assert.Equal(2, (int)WebSocketReadyState.Closing);
        Assert.Equal(3, (int)WebSocketReadyState.Closed);
    }
}
