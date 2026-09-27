namespace WebSockets;

/// <summary>
/// Settings that a browser would take from its environment (base URL, origin, cookies, proxy)
/// plus knobs for behavior the standard leaves to the user agent.
/// </summary>
public sealed class WebSocketOptions
{
    /// <summary>The base URL relative URLs are resolved against (the "API base URL"). Without it, the URL must be absolute.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>The value of the <c>Origin</c> request header, if any. Browsers always send one; other clients usually do not.</summary>
    public string? Origin { get; set; }

    /// <summary>Whether to offer the <c>permessage-deflate</c> extension. Defaults to true, as browsers do.</summary>
    public bool PerMessageDeflate { get; set; } = true;

    /// <summary>
    /// The largest <see cref="WebSocket.BufferedAmount"/> allowed. When <see cref="WebSocket.Send(string)"/> would exceed it,
    /// the WebSocket is flagged as full and the connection is closed, which fires <c>error</c> and <c>close</c>.
    /// Defaults to <see cref="long.MaxValue"/>.
    /// </summary>
    public long MaxBufferedAmount { get; set; } = long.MaxValue;

    /// <summary>How long to wait for the server's Close frame after sending ours before dropping the connection. Defaults to 30 seconds.</summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where events are dispatched. When null (the default), the <see cref="SynchronizationContext"/> current when the
    /// <see cref="WebSocket"/> is constructed is used, if any; otherwise events run in order on a dedicated queue.
    /// Events are never dispatched concurrently.
    /// </summary>
    public SynchronizationContext? SynchronizationContext { get; set; }

    /// <summary>Receives exceptions thrown by event handlers. When null, they are written to <see cref="System.Diagnostics.Trace"/>.</summary>
    public Action<Exception>? EventHandlerException { get; set; }

    /// <summary>How often to send a Ping frame to keep the connection alive. Defaults to <see cref="System.Net.WebSockets.WebSocket.DefaultKeepAliveInterval"/>; <see cref="TimeSpan.Zero"/> disables it.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = System.Net.WebSockets.WebSocket.DefaultKeepAliveInterval;

    /// <summary>Extra headers for the opening handshake request, such as <c>Authorization</c> or <c>Cookie</c>.</summary>
    public IDictionary<string, string> RequestHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Configures the <see cref="SocketsHttpHandler"/> used for the opening handshake: proxy, TLS certificates and validation,
    /// credentials, cookies, connect timeout. Automatic redirects are off, since the standard fails the connection on a redirect.
    /// Ignored when <see cref="HttpMessageInvoker"/> is set.
    /// </summary>
    public Action<SocketsHttpHandler>? ConfigureHandler { get; set; }

    /// <summary>
    /// An <see cref="System.Net.Http.HttpMessageInvoker"/> for the opening handshake, instead of a handler created per connection.
    /// It must support HTTP/1.1 upgrades, as <see cref="SocketsHttpHandler"/> does, and is not disposed by the <see cref="WebSocket"/>.
    /// </summary>
    public HttpMessageInvoker? HttpMessageInvoker { get; set; }
}
