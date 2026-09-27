namespace WebSockets;

/// <summary>The values of <see cref="WebSocket.ReadyState"/>.</summary>
public enum WebSocketReadyState : ushort
{
    /// <summary>The connection has not yet been established.</summary>
    Connecting = 0,

    /// <summary>The connection is established and communication is possible.</summary>
    Open = 1,

    /// <summary>The connection is going through the closing handshake, or <see cref="WebSocket.Close"/> was called.</summary>
    Closing = 2,

    /// <summary>The connection has been closed or could not be opened.</summary>
    Closed = 3,
}

/// <summary>How binary messages are exposed in <see cref="MessageEventArgs.Data"/>.</summary>
public enum BinaryType
{
    /// <summary>Binary messages are delivered as <see cref="WebSockets.Blob"/> objects. This is the default.</summary>
    Blob,

    /// <summary>Binary messages are delivered as <see cref="byte"/> arrays.</summary>
    ArrayBuffer,
}
