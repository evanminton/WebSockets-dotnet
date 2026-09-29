# WebSockets native library (C API)

The WebSockets library compiled ahead of time with NativeAOT, so you can use the browser `WebSocket` API from C, C++,
Rust, Python (ctypes), Delphi, game engines and so on. The target machine needs no .NET runtime. The build is not
minified or obfuscated: exception messages, stack trace data and symbols (`.pdb`) are kept.

| Folder | What |
|---|---|
| `include/websockets.h` | The whole API, documented inline |
| `Release/shared`, `Debug/shared` | `WebSocketsNative.dll` + `WebSocketsNative.lib` (import library) + `WebSocketsNative.pdb` |
| `Release/static`, `Debug/static` | `WebSocketsNative.lib` (static) + `runtime/` (NativeAOT runtime libraries and `link.rsp`) |
| `demo` | `ws_demo.c`; `shared/ws_demo.exe` (with the DLL) and `ws_demo_static.exe` (no DLL) |

`ws_demo --offline` runs the checks that need no server. The full run connects to `samples/EchoServer` from the
repository (`dotnet run --project samples/EchoServer`).

## Linking

**Shared (DLL):**

```bat
cl /MD /I include app.c /link Release\shared\WebSocketsNative.lib
rem ship WebSocketsNative.dll next to app.exe
```

**Static (one exe, no DLL).** Define `WS_STATIC`, use the static CRT (`/MT`) and link the runtime libraries too:

```bat
cl /MT /DWS_STATIC /I include app.c /link Release\static\WebSocketsNative.lib /LIBPATH:Release\static\runtime @Release\static\runtime\link.rsp
```

In CMake or Visual Studio, add `WebSocketsNative.lib`, every entry of `runtime\link.rsp`, and `runtime` as a library
directory. Only one copy of the NativeAOT runtime can be linked into a process, so two NativeAOT static libraries
can't share one exe; two NativeAOT DLLs can share one process.

## Using it

```c
#include <stdio.h>
#include "websockets.h"

static void WS_CALL on_open(void* user, ws_socket* s)    { ws_socket_send_text(s, "hello"); }
static void WS_CALL on_message(void* user, ws_socket* s, const ws_message* m) {
    if (m->type == WS_MESSAGE_TEXT) printf("%s\n", (const char*)m->data);
}
static void WS_CALL on_close(void* user, ws_socket* s, const ws_close_event* e) {
    printf("closed %d %s clean=%d\n", e->code, e->reason, e->was_clean);
}

int main(void) {
    const char* protocols[] = { "chat.v2", "chat.v1" };
    ws_callbacks cb = { on_open, on_message, NULL, on_close, NULL };
    ws_socket* s = ws_socket_create("wss://example.com/chat", protocols, 2, NULL, &cb);
    if (!s) { printf("%s: %s\n", ws_last_error_name(), ws_last_error()); return 1; }  /* SyntaxError: ... */
    /* ... later */
    ws_socket_close(s, 1000, "done");
    ws_socket_shutdown(s);   /* waits for the close event */
    ws_socket_destroy(s);
    return 0;
}
```

Events run one at a time, in order, on a library thread. To run them on your own thread (a UI or game loop), create
a `ws_dispatcher`, set it with `ws_options_set_dispatcher`, and call `ws_dispatcher_run(dispatcher, timeout_ms)` from
that thread.

Everything in the .NET API is there: the constructor's URL and subprotocol checks, `url`, `readyState`,
`bufferedAmount`, `extensions`, `protocol`, `binaryType`, every `send` overload, `close`, the four events with
`MessageEvent` and `CloseEvent` data, `Dispose` (`ws_socket_abort`) and `DisposeAsync` (`ws_socket_shutdown`),
`Blob` with `slice`, `bytes`/`arrayBuffer`, `text` and `stream` (`ws_blob_read`), and `WebSocketOptions`. The handler
hook (`ConfigureHandler`) is offered as its common settings: proxy, connect timeout, client certificates and
certificate validation. `HttpMessageInvoker` and `EventHandlerException` have no C counterpart.

Strings the library returns are freed with `ws_free`. Calls return -1 or NULL on failure; `ws_last_error()` says why
and `ws_last_error_name()` gives the `DOMException` name (`SyntaxError`, `InvalidStateError`, `InvalidAccessError`).

## Building it

Windows: `build-native.cmd [win-x64|win-arm64]` at the repository root builds all four libraries, checks the exports
against the header, and compiles and runs `ws_demo.c` against each. Or directly:

```bat
dotnet publish src\WebSockets.Native -c Release -r win-x64 -p:NativeLib=Shared -o out\shared
dotnet publish src\WebSockets.Native -c Release -r win-x64 -p:NativeLib=Static -o out\static
```

NativeAOT only compiles for the OS it runs on. Build `linux-x64`/`linux-arm64` on Linux (with clang) and
`osx-arm64`/`osx-x64` on macOS (with Xcode) with the same `dotnet publish` commands. That gives
`WebSocketsNative.so` / `.a` and `WebSocketsNative.dylib` / `.a` from the same source and header.
