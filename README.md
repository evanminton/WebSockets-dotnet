# WebSockets

A .NET 10 implementation of the [WHATWG WebSockets Standard](https://websockets.spec.whatwg.org/): the browser `WebSocket` API, with the same states, events, errors and edge cases, for any .NET app.

```csharp
using WebSockets;

var socket = new WebSocket("wss://example.com/chat", ["chat.v2", "chat.v1"]);
socket.OnOpen += (_, _) => socket.Send("hello");
socket.OnMessage += (_, e) => Console.WriteLine(e.Data);
socket.OnError += (_, _) => Console.WriteLine("connection failed");
socket.OnClose += (_, e) => Console.WriteLine($"closed {e.Code} {e.Reason} clean={e.WasClean}");

// later
socket.Close(1000, "done");
```

## Mapping from the standard

| Standard (Web IDL) | .NET |
| --- | --- |
| `new WebSocket(url, protocols)` | `new WebSocket(url)`, `(url, protocol)`, `(url, protocols, options)` |
| `url`, `protocol`, `extensions` | `Url`, `Protocol`, `Extensions` |
| `readyState`, `CONNECTING`…`CLOSED` | `ReadyState` (`WebSocketReadyState.Connecting`…`Closed`) |
| `bufferedAmount` | `BufferedAmount` (`ulong`) |
| `binaryType` (`"blob"` / `"arraybuffer"`) | `BinaryType` (`BinaryType.Blob` / `BinaryType.ArrayBuffer`) |
| `send(USVString / Blob / BufferSource)` | `Send(string)`, `Send(Blob)`, `Send(byte[])`, `Send(ReadOnlySpan<byte>)`, `Send(ReadOnlyMemory<byte>)` |
| `close(code, reason)` | `Close(ushort? code, string? reason)` |
| `onopen`, `onmessage`, `onerror`, `onclose` | `OnOpen`, `OnMessage`, `OnError`, `OnClose` events |
| `MessageEvent` (`data`, `origin`, `lastEventId`) | `MessageEventArgs` (`Data`, `Origin`, `LastEventId`) |
| `CloseEvent` (`wasClean`, `code`, `reason`) | `CloseEventArgs` (`WasClean`, `Code`, `Reason`) |
| `DOMException` | `DomException` with `Name` = `SyntaxError`, `InvalidStateError`, `InvalidAccessError` |
| `Blob` | `Blob` (`Size`, `Type`, `Slice`, `ArrayBufferAsync`, `TextAsync`, `BytesAsync`, `Stream`) |

`MessageEventArgs.Data` is a `string` for text messages, and for binary messages a `Blob` or a `byte[]` depending on `BinaryType` at the time the event is dispatched.

## Behavior that follows the standard

- **Constructor**: parses the URL (against `WebSocketOptions.BaseUrl` when set), maps `http`/`https` to `ws`/`wss`, and throws `SyntaxError` for other schemes, any fragment (even an empty `#`), and subprotocols that are repeated or not RFC 7230 tokens. It never blocks; connecting happens in the background.
- **Opening handshake**: `GET` over HTTP/1.1 with `Upgrade`, `Connection`, a random 16-byte `Sec-WebSocket-Key`, `Sec-WebSocket-Version: 13`, the requested subprotocols and a `permessage-deflate; client_max_window_bits` offer. Redirects fail the connection. The response must be a 101 with a correct `Sec-WebSocket-Accept`, a subprotocol when any were requested (and only one that was), and no extension that was not offered.
- **`Send`** throws `InvalidStateError` while connecting. `BufferedAmount` counts UTF-8/binary bytes not yet written to the network; once closing or closed, `Send` transmits nothing but still adds to it, and it never goes back down.
- **`Close`** throws `InvalidAccessError` for codes other than 1000 and 3000–4999, and `SyntaxError` for reasons over 123 UTF-8 bytes. While connecting it fails the connection (`error`, then `close` with 1006). With no code it sends a Close frame with an empty body; a reason is only sent together with a code.
- **Events** are dispatched one at a time, in order: `open`, then `message`s, then `error` (only when the connection failed or the buffer was full) and `close`. Messages that arrive after `Close()` was called are dropped, since the state is no longer `OPEN`.
- **Closing handshake**: the server's code and reason are reported on `close`; a Close frame without a code is reported as 1005 and answered with an empty Close frame. Abnormal closure reports 1006 with `WasClean == false`.

## Options

`WebSocketOptions` covers what a browser takes from its environment and what the standard leaves to the user agent:

| Option | Default | Purpose |
| --- | --- | --- |
| `BaseUrl` | none | Resolves relative URLs (the "API base URL"). |
| `Origin` | none | Value of the `Origin` request header. |
| `RequestHeaders` | empty | Extra handshake headers, such as `Authorization` or `Cookie`. |
| `PerMessageDeflate` | `true` | Offer `permessage-deflate`. |
| `KeepAliveInterval` | 30 s | Ping interval; `TimeSpan.Zero` disables pings. |
| `MaxBufferedAmount` | `long.MaxValue` | Over this, the socket is "flagged as full" and closed (`error`, then `close` 1006). |
| `CloseTimeout` | 30 s | How long to wait for the server's Close frame before dropping the connection; also bounds `DisposeAsync`. |
| `SynchronizationContext` | the current one, if any | Where events run (for example a UI thread). Without one they run on a dedicated queue. |
| `EventHandlerException` | trace | Receives exceptions thrown by event handlers, which never stop later events. |
| `ConfigureHandler` | none | Configure the `SocketsHttpHandler`: proxy, TLS, client certificates, credentials, cookies. |
| `HttpMessageInvoker` | none | Supply your own invoker for the handshake instead. |

## Disposal

`DisposeAsync()` starts the closing handshake with no status code (what a browser does when a `WebSocket` is garbage collected), waits for the `close` event up to `CloseTimeout`, then releases the connection. `Dispose()` drops the connection immediately; a `close` event with code 1006 still fires.

## Differences from a browser

- There is no document, so there is no "document went away" handling, and `Origin` is only sent when you set it.
- Cookies and credentials are not attached automatically; use `RequestHeaders` or `ConfigureHandler`.
- The handshake is HTTP/1.1 only (no WebSockets over HTTP/2).
- Attach event handlers right after construction. As in a browser, events are queued, but without a `SynchronizationContext` a very fast failure could in principle be dispatched before a handler is attached on another line.
- `WebSocketStream` is not part of the standard yet, so it is not included.

## Implementation notes

Framing, masking, fragmentation, UTF-8 validation, ping/pong and permessage-deflate come from `System.Net.WebSockets.WebSocket.CreateFromStream`. The library does the opening handshake itself, and wraps the connection stream to send and recognize Close frames without a status code, which `System.Net.WebSockets` cannot do (it writes 1005 on the wire, which RFC 6455 forbids, and reports an empty Close frame as 1000).

## Native C library

`src/WebSockets.Native` compiles the library with NativeAOT into a shared (`.dll`/`.so`/`.dylib`) or static
(`.lib`/`.a`) C library that needs no .NET runtime, with the whole API in
[`include/websockets.h`](src/WebSockets.Native/include/websockets.h). On Windows, `build-native.cmd` builds both kinds
and checks them from C with `samples/native/ws_demo.c`. See [NATIVE.md](src/WebSockets.Native/NATIVE.md).

## Building and testing

```sh
dotnet test
```

The tests run a Kestrel server and a hand-rolled raw WebSocket server on loopback, so they need nothing external.
