namespace WebSockets.Native;

/// <summary>
/// The state behind a ws_socket handle: the <see cref="WebSocket"/> and the C callbacks its events are forwarded to.
/// A callback runs under <see cref="_callbackGate"/>, so <see cref="Detach"/> can wait for one in progress.
/// </summary>
internal sealed unsafe class NativeSocket
{
    private readonly Lock _callbackGate = new();
    private NativeCallbacks _callbacks;
    private volatile bool _detached;

    public NativeSocket(WebSocket socket, NativeCallbacks callbacks)
    {
        Socket = socket;
        _callbacks = callbacks;
        socket.OnOpen += (_, _) => Invoke(static (in NativeCallbacks c, void* handle, object? _) =>
        {
            if (c.OnOpen != null) c.OnOpen(c.User, handle);
        }, null);
        socket.OnError += (_, _) => Invoke(static (in NativeCallbacks c, void* handle, object? _) =>
        {
            if (c.OnError != null) c.OnError(c.User, handle);
        }, null);
        socket.OnMessage += (_, e) => Invoke(static (in NativeCallbacks c, void* handle, object? e) =>
        {
            if (c.OnMessage != null) DispatchMessage(c, handle, (MessageEventArgs)e!);
        }, e);
        socket.OnClose += (_, e) => Invoke(static (in NativeCallbacks c, void* handle, object? e) =>
        {
            if (c.OnClose == null) return;
            var args = (CloseEventArgs)e!;
            fixed (byte* reason = Interop.Utf8Z(args.Reason))
            {
                var ev = new NativeCloseEvent { WasClean = args.WasClean ? 1 : 0, Code = args.Code, Reason = reason };
                c.OnClose(c.User, handle, &ev);
            }
        }, e);
    }

    private delegate void Callback(in NativeCallbacks callbacks, void* handle, object? state);

    public WebSocket Socket { get; }

    /// <summary>The ws_socket* passed to callbacks.</summary>
    public nint Handle { get; set; }

    public void SetCallbacks(NativeCallbacks callbacks)
    {
        lock (_callbackGate)
        {
            _callbacks = callbacks;
        }
    }

    /// <summary>Stops all callbacks. Waits for one running on another thread; returns at once from inside a callback.</summary>
    public void Detach()
    {
        _detached = true;
        lock (_callbackGate)
        {
            _callbacks = default;
        }
    }

    private void Invoke(Callback callback, object? state)
    {
        if (_detached) return;
        lock (_callbackGate)
        {
            if (_detached) return;
            NativeCallbacks callbacks = _callbacks;
            callback(callbacks, (void*)Handle, state);
        }
    }

    private static void DispatchMessage(in NativeCallbacks c, void* handle, MessageEventArgs e)
    {
        fixed (byte* origin = Interop.Utf8Z(e.Origin))
        fixed (byte* lastEventId = Interop.Utf8Z(e.LastEventId))
        {
            var message = new NativeMessage { Origin = origin, LastEventId = lastEventId };
            switch (e.Data)
            {
                case string text:
                {
                    byte[] bytes = Interop.Utf8Z(text);
                    fixed (byte* data = bytes)
                    {
                        message.Type = Interop.MessageText;
                        message.Data = data;
                        message.Length = (nuint)(bytes.Length - 1);
                        c.OnMessage(c.User, handle, &message);
                    }

                    break;
                }

                case byte[] bytes:
                    fixed (byte* data = bytes)
                    {
                        message.Type = Interop.MessageArrayBuffer;
                        message.Data = data;
                        message.Length = (nuint)bytes.Length;
                        c.OnMessage(c.User, handle, &message);
                    }

                    break;

                case Blob blob:
                    nint blobHandle = Interop.NewHandle(blob);
                    try
                    {
                        fixed (byte* data = blob.Memory.Span)
                        {
                            message.Type = Interop.MessageBlob;
                            message.Data = data;
                            message.Length = (nuint)blob.Size;
                            message.Blob = (void*)blobHandle;
                            c.OnMessage(c.User, handle, &message);
                        }
                    }
                    finally
                    {
                        Interop.FreeHandle((void*)blobHandle);
                    }

                    break;
            }
        }
    }
}
