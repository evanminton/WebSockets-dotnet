namespace WebSockets;

/// <summary>The data of a <c>message</c> event, the counterpart of <c>MessageEvent</c>.</summary>
public sealed class MessageEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public MessageEventArgs(object data, string origin, string lastEventId = "")
    {
        Data = data;
        Origin = origin;
        LastEventId = lastEventId;
    }

    /// <summary>
    /// The message: a <see cref="string"/> for text messages, and for binary messages a
    /// <see cref="WebSockets.Blob"/> or a <see cref="byte"/> array depending on <see cref="WebSocket.BinaryType"/>.
    /// </summary>
    public object Data { get; }

    /// <summary>The serialization of the WebSocket URL's origin, such as <c>wss://example.com</c>.</summary>
    public string Origin { get; }

    /// <summary>Always the empty string for WebSocket messages.</summary>
    public string LastEventId { get; }
}

/// <summary>The data of a <c>close</c> event, the counterpart of <c>CloseEvent</c>.</summary>
public sealed class CloseEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public CloseEventArgs(bool wasClean = false, ushort code = 0, string reason = "")
    {
        WasClean = wasClean;
        Code = code;
        Reason = reason;
    }

    /// <summary>Whether the connection closed cleanly (the closing handshake completed).</summary>
    public bool WasClean { get; }

    /// <summary>The WebSocket connection close code, such as 1000, 1005 (no code received) or 1006 (abnormal closure).</summary>
    public ushort Code { get; }

    /// <summary>The WebSocket connection close reason.</summary>
    public string Reason { get; }
}
