using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace WebSockets;

/// <summary>
/// A WebSocket client with the API and behavior of the WHATWG WebSockets Standard
/// (<see href="https://websockets.spec.whatwg.org/"/>). Frames are handled by <see cref="System.Net.WebSockets.WebSocket"/>;
/// the opening handshake follows the standard's "establish a WebSocket connection" algorithm.
/// </summary>
/// <remarks>
/// As in a browser, the constructor starts connecting in the background and never blocks. Progress is reported
/// through the <see cref="OnOpen"/>, <see cref="OnMessage"/>, <see cref="OnError"/> and <see cref="OnClose"/> events,
/// which are dispatched one at a time, in order, on the event loop described in <see cref="WebSocketOptions.SynchronizationContext"/>.
/// Attach handlers right after construction.
/// </remarks>
public sealed class WebSocket : IDisposable, IAsyncDisposable
{
    private const int MaxReasonBytes = 123;

    // Encoding a .NET string replaces lone surrogates with U+FFFD, which is the USVString conversion.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly Lock _gate = new();
    private readonly WebSocketOptions _options;
    private readonly Uri _uri;
    private readonly string[] _protocols;
    private readonly string _origin;
    private readonly EventLoop _loop;
    private System.Net.WebSockets.WebSocket? _socket;
    private ConnectionStream? _stream;
    private readonly HttpMessageInvoker? _ownedInvoker;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Outgoing> _outgoing = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _closeFrameSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closedEventDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile int _readyState = (int)WebSocketReadyState.Connecting;
    private long _bufferedAmount;
    private volatile BinaryType _binaryType = BinaryType.Blob;
    private string _extensions = "";
    private string _protocol = "";
    private WebSocketMessageFlags _sendFlags = WebSocketMessageFlags.EndOfMessage;

    // Connection state, guarded by _gate.
    private bool _established;
    private bool _closingHandshakeStarted;
    private bool _failRequested;
    private bool _full;
    private int _connectionClosed;
    private int _disposed;

    /// <summary>Creates a WebSocket and starts connecting to <paramref name="url"/>.</summary>
    /// <exception cref="DomException"><c>SyntaxError</c> when the URL is invalid, not ws/wss/http/https, or has a fragment.</exception>
    public WebSocket(string url)
        : this(url, [], null)
    {
    }

    /// <summary>Creates a WebSocket that requests a single subprotocol.</summary>
    /// <exception cref="DomException"><c>SyntaxError</c> when the URL or the subprotocol is invalid.</exception>
    public WebSocket(string url, string protocol)
        : this(url, [protocol], null)
    {
    }

    /// <summary>Creates a WebSocket with options and no subprotocols.</summary>
    /// <exception cref="DomException"><c>SyntaxError</c> when the URL is invalid.</exception>
    public WebSocket(string url, WebSocketOptions options)
        : this(url, [], options)
    {
    }

    /// <summary>Creates a WebSocket that requests the given subprotocols, in order of preference.</summary>
    /// <exception cref="DomException"><c>SyntaxError</c> when the URL is invalid, or a subprotocol is repeated or is not a valid token.</exception>
    public WebSocket(string url, IEnumerable<string> protocols, WebSocketOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(protocols);
        _options = options ?? new WebSocketOptions();

        _uri = ParseUrl(url, _options.BaseUrl);
        _protocols = ValidateProtocols(protocols);
        Url = _uri.AbsoluteUri;
        _origin = _uri.GetLeftPart(UriPartial.Authority);
        _ownedInvoker = _options.HttpMessageInvoker is null ? CreateInvoker() : null;
        _loop = new EventLoop(_options.SynchronizationContext ?? SynchronizationContext.Current, _options.EventHandlerException);

        _ = Task.Run(EstablishAsync);
    }

    /// <summary>Fired when the connection is established.</summary>
    public event EventHandler? OnOpen;

    /// <summary>Fired for each message received while the WebSocket is open.</summary>
    public event EventHandler<MessageEventArgs>? OnMessage;

    /// <summary>Fired before <see cref="OnClose"/> when the connection failed or was closed because the send buffer was full.</summary>
    public event EventHandler? OnError;

    /// <summary>Fired once, when the connection is closed.</summary>
    public event EventHandler<CloseEventArgs>? OnClose;

    /// <summary>The URL, serialized, after resolving it and mapping http/https to ws/wss.</summary>
    public string Url { get; }

    /// <summary>The state of the connection.</summary>
    public WebSocketReadyState ReadyState => (WebSocketReadyState)_readyState;

    /// <summary>
    /// The number of bytes of application data passed to <c>Send</c> that have not yet been transmitted.
    /// Once the connection is closing or closed, <c>Send</c> still adds to it and it never goes down again.
    /// </summary>
    public ulong BufferedAmount => (ulong)Interlocked.Read(ref _bufferedAmount);

    /// <summary>The extensions selected by the server, such as <c>permessage-deflate</c>; empty until open.</summary>
    public string Extensions => Volatile.Read(ref _extensions);

    /// <summary>The subprotocol selected by the server; empty until open or when none was selected.</summary>
    public string Protocol => Volatile.Read(ref _protocol);

    /// <summary>How binary messages are delivered to <see cref="OnMessage"/>. Defaults to <see cref="WebSockets.BinaryType.Blob"/>.</summary>
    public BinaryType BinaryType
    {
        get => _binaryType;
        set => _binaryType = value;
    }

    /// <summary>
    /// Closes the connection. With neither argument the Close frame has no body; a <paramref name="reason"/>
    /// is only sent together with a <paramref name="code"/>.
    /// </summary>
    /// <exception cref="DomException">
    /// <c>InvalidAccessError</c> when <paramref name="code"/> is neither 1000 nor in 3000 to 4999;
    /// <c>SyntaxError</c> when <paramref name="reason"/> is longer than 123 bytes as UTF-8.
    /// </exception>
    public void Close(ushort? code = null, string? reason = null)
    {
        if (code is { } c && c != 1000 && (c < 3000 || c > 4999))
        {
            throw new DomException(DomException.InvalidAccessError, $"The close code must be 1000 or between 3000 and 4999, but was {c}.");
        }

        string? reasonText = null;
        if (reason is not null)
        {
            byte[] reasonBytes = Utf8.GetBytes(reason);
            if (reasonBytes.Length > MaxReasonBytes)
            {
                throw new DomException(DomException.SyntaxError, $"The close reason must be at most {MaxReasonBytes} bytes of UTF-8, but was {reasonBytes.Length}.");
            }

            reasonText = Utf8.GetString(reasonBytes);
        }

        bool fail = false;
        lock (_gate)
        {
            if (_readyState is (int)WebSocketReadyState.Closing or (int)WebSocketReadyState.Closed)
            {
                return;
            }

            // Still CONNECTING until the open task runs, even once the handshake has finished, so fail rather than close.
            if (_readyState == (int)WebSocketReadyState.Connecting)
            {
                _failRequested = true;
                fail = true;
            }
            else if (!_closingHandshakeStarted)
            {
                _closingHandshakeStarted = true;
                _outgoing.Writer.TryWrite(code is { } status
                    ? Outgoing.Close((WebSocketCloseStatus)status, reasonText)
                    : Outgoing.Close(WebSocketCloseStatus.Empty, null));
            }

            _readyState = (int)WebSocketReadyState.Closing;
        }

        if (fail)
        {
            _cts.Cancel();
        }
    }

    /// <summary>Sends a text message.</summary>
    /// <exception cref="DomException"><c>InvalidStateError</c> while <see cref="ReadyState"/> is <see cref="WebSocketReadyState.Connecting"/>.</exception>
    public void Send(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ThrowIfConnecting();
        SendCore(Utf8.GetBytes(data), WebSocketMessageType.Text);
    }

    /// <summary>Sends a binary message holding the blob's bytes.</summary>
    /// <exception cref="DomException"><c>InvalidStateError</c> while <see cref="ReadyState"/> is <see cref="WebSocketReadyState.Connecting"/>.</exception>
    public void Send(Blob data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ThrowIfConnecting();
        SendCore(data.Memory, WebSocketMessageType.Binary);
    }

    /// <summary>Sends a binary message. The bytes are copied, so the array may be reused afterwards.</summary>
    /// <exception cref="DomException"><c>InvalidStateError</c> while <see cref="ReadyState"/> is <see cref="WebSocketReadyState.Connecting"/>.</exception>
    public void Send(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Send(data.AsSpan());
    }

    /// <summary>Sends a binary message. The bytes are copied, so the memory may be reused afterwards.</summary>
    /// <exception cref="DomException"><c>InvalidStateError</c> while <see cref="ReadyState"/> is <see cref="WebSocketReadyState.Connecting"/>.</exception>
    public void Send(ReadOnlyMemory<byte> data) => Send(data.Span);

    /// <summary>Sends a binary message. The bytes are copied, so the memory may be reused afterwards.</summary>
    /// <exception cref="DomException"><c>InvalidStateError</c> while <see cref="ReadyState"/> is <see cref="WebSocketReadyState.Connecting"/>.</exception>
    public void Send(ReadOnlySpan<byte> data)
    {
        ThrowIfConnecting();
        SendCore(data.ToArray(), WebSocketMessageType.Binary);
    }

    /// <summary>Drops the connection at once, without a closing handshake. A <c>close</c> event with code 1006 still follows unless already closed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        ConnectionClosed(wasClean: false, code: 1006, reason: "", failed: false);
    }

    /// <summary>
    /// Starts the closing handshake with no status code, as a browser does when a WebSocket is garbage collected,
    /// waits up to <see cref="WebSocketOptions.CloseTimeout"/> for the <c>close</c> event, then disposes.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        Close();
        try
        {
            await _closedEventDispatched.Task.WaitAsync(_options.CloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        Dispose();
    }

    private void ThrowIfConnecting()
    {
        if (_readyState == (int)WebSocketReadyState.Connecting)
        {
            throw new DomException(DomException.InvalidStateError, "The WebSocket is still connecting.");
        }
    }

    private void SendCore(ReadOnlyMemory<byte> data, WebSocketMessageType type)
    {
        bool full = false;
        lock (_gate)
        {
            long buffered = Interlocked.Add(ref _bufferedAmount, data.Length);
            if (_established && !_failRequested && !_closingHandshakeStarted && _connectionClosed == 0)
            {
                if (buffered > _options.MaxBufferedAmount)
                {
                    _full = true;
                    full = true;
                }
                else
                {
                    _outgoing.Writer.TryWrite(Outgoing.Message(data, type));
                }
            }
        }

        if (full)
        {
            ConnectionClosed(wasClean: false, code: 1006, reason: "", failed: false);
        }
    }

    private async Task EstablishAsync()
    {
        HandshakeResult handshake;
        try
        {
            HttpMessageInvoker invoker = _options.HttpMessageInvoker ?? _ownedInvoker!;
            handshake = await Handshake.ConnectAsync(_uri, _protocols, _options, invoker, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            FailConnection();
            return;
        }

        string extensions = handshake.Extensions;
        string protocol = handshake.Protocol;

        lock (_gate)
        {
            _socket = handshake.Socket;
            _stream = handshake.Stream;
            if (!handshake.CompressOutgoing)
            {
                _sendFlags |= WebSocketMessageFlags.DisableCompression;
            }

            if (_failRequested || _connectionClosed != 0)
            {
                _socket.Dispose();
                FailConnection();
                return;
            }

            _established = true;
        }

        _loop.Queue(() =>
        {
            // Under _gate so Close() sees either CONNECTING (and fails) or OPEN (and closes cleanly), never both.
            lock (_gate)
            {
                // Close() failed the connection after the handshake but before this task ran; its error and close events follow.
                if (_readyState != (int)WebSocketReadyState.Connecting)
                {
                    return;
                }

                _readyState = (int)WebSocketReadyState.Open;
            }

            Volatile.Write(ref _extensions, extensions);
            Volatile.Write(ref _protocol, protocol);
            OnOpen?.Invoke(this, EventArgs.Empty);
        });

        _ = Task.Run(SendLoopAsync);
        await ReceiveLoopAsync().ConfigureAwait(false);
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (Outgoing item in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (!item.IsClose)
                {
                    await _socket!.SendAsync(item.Data, item.Type, _sendFlags, _cts.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _bufferedAmount, -item.Data.Length);
                    continue;
                }

                if (item.Status == WebSocketCloseStatus.Empty || !TryValidateCloseStatus(item.Status))
                {
                    await _stream!.SendEmptyCloseFrameAsync(_cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await _socket!.CloseOutputAsync(item.Status, item.Reason, _cts.Token).ConfigureAwait(false);
                }

                _closeFrameSent.TrySetResult();
                _ = DropAfterCloseTimeoutAsync();
                return;
            }
        }
        catch (Exception)
        {
            FailConnection();
        }
    }

    private async Task DropAfterCloseTimeoutAsync()
    {
        try
        {
            await Task.Delay(_options.CloseTimeout, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        FailConnection();
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult result = await _socket!.ReceiveAsync(buffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CompleteClosingHandshakeAsync().ConfigureAwait(false);
                    return;
                }

                message.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    byte[] data = message.WrittenSpan.ToArray();
                    message.ResetWrittenCount();
                    bool text = result.MessageType == WebSocketMessageType.Text;
                    _loop.Queue(() => DispatchMessage(text, data));
                }
            }
        }
        catch (Exception)
        {
            FailConnection();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task CompleteClosingHandshakeAsync()
    {
        ushort code = _stream!.ReceivedEmptyCloseFrame ? (ushort)1005 : (ushort)(_socket!.CloseStatus ?? WebSocketCloseStatus.Empty);
        string reason = _socket!.CloseStatusDescription ?? "";

        bool startedByServer;
        lock (_gate)
        {
            startedByServer = !_closingHandshakeStarted;
            if (startedByServer)
            {
                _closingHandshakeStarted = true;
                _outgoing.Writer.TryWrite(Outgoing.Close((WebSocketCloseStatus)code, null));
            }
        }

        if (startedByServer)
        {
            _loop.Queue(() =>
                Interlocked.CompareExchange(ref _readyState, (int)WebSocketReadyState.Closing, (int)WebSocketReadyState.Open));
        }

        await _closeFrameSent.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
        ConnectionClosed(wasClean: true, code, reason, failed: false);
    }

    private void DispatchMessage(bool text, byte[] data)
    {
        if (_readyState != (int)WebSocketReadyState.Open)
        {
            return;
        }

        object dataForEvent = text
            ? Utf8.GetString(data)
            : _binaryType == BinaryType.Blob ? Blob.Wrap(data) : data;
        OnMessage?.Invoke(this, new MessageEventArgs(dataForEvent, _origin));
    }

    private void FailConnection() => ConnectionClosed(wasClean: false, code: 1006, reason: "", failed: true);

    private void ConnectionClosed(bool wasClean, ushort code, string reason, bool failed)
    {
        bool full;
        lock (_gate)
        {
            if (_connectionClosed != 0)
            {
                return;
            }

            _connectionClosed = 1;
            full = _full;
        }

        _outgoing.Writer.TryComplete();
        _cts.Cancel();
        lock (_gate)
        {
            _socket?.Dispose();
        }

        _ownedInvoker?.Dispose();
        _closeFrameSent.TrySetResult();

        _loop.Queue(() =>
        {
            _readyState = (int)WebSocketReadyState.Closed;
            if (failed || full)
            {
                OnError?.Invoke(this, EventArgs.Empty);
            }

            try
            {
                OnClose?.Invoke(this, new CloseEventArgs(wasClean, code, reason));
            }
            finally
            {
                _closedEventDispatched.TrySetResult();
            }
        });
        _loop.Complete();
    }

    // The codes System.Net.WebSockets accepts for CloseOutputAsync; any other code received from the server is
    // answered with an empty Close frame rather than echoed.
    private static bool TryValidateCloseStatus(WebSocketCloseStatus status) =>
        (int)status is (>= 1000 and <= 1003) or (>= 1007 and <= 1011) or (>= 3000 and <= 4999);

    private HttpMessageInvoker CreateInvoker()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        _options.ConfigureHandler?.Invoke(handler);
        return new HttpMessageInvoker(handler);
    }

    internal static Uri ParseUrl(string url, Uri? baseUrl)
    {
        bool parsed = baseUrl is not null
            ? Uri.TryCreate(baseUrl, url, out Uri? result)
            : Uri.TryCreate(url, UriKind.Absolute, out result);
        if (!parsed || result is null || !result.IsAbsoluteUri)
        {
            throw new DomException(DomException.SyntaxError, $"'{url}' is not a valid URL.");
        }

        string scheme = result.Scheme switch
        {
            "http" => "ws",
            "https" => "wss",
            var other => other,
        };
        if (scheme is not ("ws" or "wss"))
        {
            throw new DomException(DomException.SyntaxError, $"The URL scheme must be ws, wss, http or https, but was '{result.Scheme}'.");
        }

        // Any '#' in the input starts a fragment, and an empty fragment is still a fragment.
        if (url.Contains('#'))
        {
            throw new DomException(DomException.SyntaxError, "WebSocket URLs must not contain a fragment.");
        }

        if (scheme != result.Scheme)
        {
            result = new UriBuilder(result) { Scheme = scheme, Port = result.IsDefaultPort ? -1 : result.Port }.Uri;
        }

        return result;
    }

    internal static string[] ValidateProtocols(IEnumerable<string> protocols)
    {
        string[] list = protocols.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string protocol in list)
        {
            if (protocol is null || !IsToken(protocol))
            {
                throw new DomException(DomException.SyntaxError, $"'{protocol}' is not a valid subprotocol name.");
            }

            if (!seen.Add(protocol))
            {
                throw new DomException(DomException.SyntaxError, $"The subprotocol '{protocol}' is repeated.");
            }
        }

        return list;
    }

    // RFC 7230 token: 1*tchar.
    private static bool IsToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool tchar = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
            if (!tchar)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct Outgoing(ReadOnlyMemory<byte> Data, WebSocketMessageType Type, bool IsClose, WebSocketCloseStatus Status, string? Reason)
    {
        public static Outgoing Message(ReadOnlyMemory<byte> data, WebSocketMessageType type) => new(data, type, false, default, null);

        public static Outgoing Close(WebSocketCloseStatus status, string? reason) => new(default, default, true, status, reason);
    }
}
