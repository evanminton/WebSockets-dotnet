/*
 * websockets.h: C API of WebSockets-dotnet, a WHATWG WebSockets Standard client (the browser WebSocket API),
 * built with NativeAOT. No .NET runtime is needed on the target machine.
 *
 *   Shared: link WebSocketsNative.lib (Windows import library) and ship WebSocketsNative.dll;
 *           or WebSocketsNative.so / WebSocketsNative.dylib on Linux / macOS.
 *   Static: #define WS_STATIC, link WebSocketsNative.lib / .a plus the NativeAOT runtime libraries listed in
 *           runtime/link.rsp, and build with the static CRT (/MT) on Windows.
 *
 * Mapping from the standard
 *   new WebSocket(url, protocols)       ws_socket_create(url, protocols, count, options, callbacks)
 *   url, protocol, extensions           ws_socket_url, ws_socket_protocol, ws_socket_extensions
 *   readyState                          ws_socket_ready_state (WS_CONNECTING ... WS_CLOSED)
 *   bufferedAmount                      ws_socket_buffered_amount
 *   binaryType                          ws_socket_binary_type / ws_socket_set_binary_type
 *   send(USVString / Blob / Buffer)     ws_socket_send_text / ws_socket_send_blob / ws_socket_send_binary
 *   close(code, reason)                 ws_socket_close (WS_CLOSE_NO_CODE for no code)
 *   onopen/onmessage/onerror/onclose    ws_callbacks
 *   MessageEvent, CloseEvent            ws_message, ws_close_event
 *   DOMException                        -1 result, ws_last_error_name() = "SyntaxError", ...
 *   Blob                                ws_blob_* functions
 *
 * Conventions
 *   - Text is UTF-8. char* results are allocated by the library: free them with ws_free (never free()).
 *   - int results: >= 0 success, -1 failure. char* and handle results: NULL on failure.
 *     On failure ws_last_error() says why, ws_last_error_name() gives the DOMException name ("SyntaxError",
 *     "InvalidStateError", "InvalidAccessError") or the .NET exception type for other errors, and
 *     ws_last_error_code() the legacy DOMException code (12, 11, 15) or 0. Per thread; valid until the next
 *     failure on that thread; never NULL.
 *   - Events are dispatched one at a time, in order (open, messages, then error and close), on a library thread,
 *     or on the thread that calls ws_dispatcher_run when the options name a ws_dispatcher.
 *     Pointers passed to a callback are valid only during that call.
 *   - Handles (ws_socket, ws_options, ws_blob, ws_dispatcher) are freed with their own destroy function.
 */
#ifndef WEBSOCKETS_H
#define WEBSOCKETS_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && !defined(WS_STATIC)
#  define WS_API __declspec(dllimport)
#else
#  define WS_API
#endif

#if defined(_WIN32)
#  define WS_CALL __cdecl
#else
#  define WS_CALL
#endif

/* ───────────── values ───────────── */

/* readyState */
enum ws_ready_state { WS_CONNECTING = 0, WS_OPEN = 1, WS_CLOSING = 2, WS_CLOSED = 3 };

/* binaryType: how binary messages are delivered to on_message. WS_BINARY_BLOB is the default. */
enum ws_binary_type { WS_BINARY_BLOB = 0, WS_BINARY_ARRAYBUFFER = 1 };

/* ws_message.type */
enum ws_message_type {
    WS_MESSAGE_TEXT = 0,        /* data is UTF-8, NUL-terminated (length excludes the NUL) */
    WS_MESSAGE_ARRAYBUFFER = 1, /* binary, binaryType was WS_BINARY_ARRAYBUFFER */
    WS_MESSAGE_BLOB = 2         /* binary, binaryType was WS_BINARY_BLOB; blob is set as well */
};

/* ws_socket_close: pass as code to close without a status code (reason must then be NULL or is not sent). */
#define WS_CLOSE_NO_CODE (-1)

/* ws_blob_slice: pass for start or end to use the default (0 and the size). */
#define WS_BLOB_DEFAULT INT64_MIN

/* ws_blob_part.kind */
enum ws_blob_part_kind { WS_PART_BYTES = 0, WS_PART_TEXT = 1, WS_PART_BLOB = 2 };

/* Legacy DOMException codes returned by ws_last_error_code. */
enum ws_dom_exception_code {
    WS_INVALID_STATE_ERR = 11, WS_SYNTAX_ERR = 12, WS_INVALID_ACCESS_ERR = 15
};

/* ───────────── types ───────────── */

typedef struct ws_socket ws_socket;
typedef struct ws_options ws_options;
typedef struct ws_blob ws_blob;
typedef struct ws_dispatcher ws_dispatcher;

/* MessageEvent */
typedef struct ws_message {
    int32_t type;               /* enum ws_message_type */
    const uint8_t* data;        /* the message bytes (UTF-8 for text) */
    size_t length;
    ws_blob* blob;              /* WS_MESSAGE_BLOB only, else NULL; borrowed: ws_blob_clone to keep it */
    const char* origin;         /* the URL's origin, such as "wss://example.com" */
    const char* last_event_id;  /* always "" */
} ws_message;

/* CloseEvent */
typedef struct ws_close_event {
    int32_t was_clean;          /* 1 when the closing handshake completed */
    uint16_t code;              /* such as 1000, 1005 (no code received) or 1006 (abnormal closure) */
    const char* reason;
} ws_close_event;

typedef void (WS_CALL *ws_open_fn)(void* user, ws_socket* socket);
typedef void (WS_CALL *ws_message_fn)(void* user, ws_socket* socket, const ws_message* message);
typedef void (WS_CALL *ws_error_fn)(void* user, ws_socket* socket);
typedef void (WS_CALL *ws_close_fn)(void* user, ws_socket* socket, const ws_close_event* event);

/* onopen, onmessage, onerror, onclose. Any of them may be NULL. */
typedef struct ws_callbacks {
    ws_open_fn on_open;
    ws_message_fn on_message;
    ws_error_fn on_error;
    ws_close_fn on_close;
    void* user;                 /* passed to every callback */
} ws_callbacks;

/* One part of ws_blob_create_from_parts: bytes, UTF-8 text, or another blob. */
typedef struct ws_blob_part {
    int32_t kind;               /* enum ws_blob_part_kind */
    const void* data;           /* WS_PART_BYTES / WS_PART_TEXT */
    size_t length;              /* bytes of data */
    const ws_blob* blob;        /* WS_PART_BLOB */
} ws_blob_part;

/* ───────────── library ───────────── */

WS_API void WS_CALL ws_free(void* p);                 /* frees a char* returned by the library; NULL is fine */
WS_API const char* WS_CALL ws_last_error(void);       /* message of this thread's last failure, "" if none */
WS_API const char* WS_CALL ws_last_error_name(void);  /* "SyntaxError", "InvalidStateError", ... or "" */
WS_API int WS_CALL ws_last_error_code(void);          /* legacy DOMException code, or 0 */
WS_API const char* WS_CALL ws_version(void);          /* library version; do not free */

/* ───────────── options (WebSocketOptions) ─────────────
 * Settings a browser takes from its environment. ws_socket_create copies them, so the options can be changed or
 * destroyed afterwards. Times are milliseconds. */

WS_API ws_options* WS_CALL ws_options_create(void);
WS_API void WS_CALL ws_options_destroy(ws_options* options);

/* Base URL relative URLs are resolved against (the "API base URL"). NULL clears it. SyntaxError if not absolute. */
WS_API int WS_CALL ws_options_set_base_url(ws_options* options, const char* url);
WS_API char* WS_CALL ws_options_get_base_url(const ws_options* options);          /* "" when none */
/* Value of the Origin request header. NULL: none is sent (the default). */
WS_API int WS_CALL ws_options_set_origin(ws_options* options, const char* origin);
WS_API char* WS_CALL ws_options_get_origin(const ws_options* options);            /* "" when none */
/* Offer permessage-deflate. Default 1. */
WS_API int WS_CALL ws_options_set_per_message_deflate(ws_options* options, int enabled);
WS_API int WS_CALL ws_options_get_per_message_deflate(const ws_options* options);
/* Largest bufferedAmount before the socket is flagged as full and closed. Default INT64_MAX. */
WS_API int WS_CALL ws_options_set_max_buffered_amount(ws_options* options, int64_t bytes);
WS_API int64_t WS_CALL ws_options_get_max_buffered_amount(const ws_options* options);
/* How long to wait for the server's Close frame. Default 30000; -1 waits forever. Also bounds ws_socket_shutdown. */
WS_API int WS_CALL ws_options_set_close_timeout(ws_options* options, int64_t milliseconds);
WS_API int64_t WS_CALL ws_options_get_close_timeout(const ws_options* options);
/* Ping interval. Default 30000; 0 disables pings. */
WS_API int WS_CALL ws_options_set_keep_alive_interval(ws_options* options, int64_t milliseconds);
WS_API int64_t WS_CALL ws_options_get_keep_alive_interval(const ws_options* options);
/* Extra handshake headers, such as Authorization or Cookie. Names are case-insensitive. value NULL removes. */
WS_API int WS_CALL ws_options_set_request_header(ws_options* options, const char* name, const char* value);
WS_API char* WS_CALL ws_options_get_request_header(const ws_options* options, const char* name); /* NULL, no error, when absent */
WS_API int WS_CALL ws_options_clear_request_headers(ws_options* options);
/* Where events run: NULL (the default) runs them on a library thread; a dispatcher runs them in ws_dispatcher_run. */
WS_API int WS_CALL ws_options_set_dispatcher(ws_options* options, ws_dispatcher* dispatcher);

/* The connection's HTTP handler (ConfigureHandler in .NET). */
/* Proxy: NULL uses the system proxy (the default), "" connects directly, otherwise a proxy URL such as "http://proxy:8080". */
WS_API int WS_CALL ws_options_set_proxy(ws_options* options, const char* proxy_url);
/* TCP/TLS connect timeout. Default -1 (none); otherwise 1 to INT32_MAX. */
WS_API int WS_CALL ws_options_set_connect_timeout(ws_options* options, int64_t milliseconds);
/* Accept any server certificate. For development only. Default 0. */
WS_API int WS_CALL ws_options_set_accept_any_certificate(ws_options* options, int enabled);
/* Adds a TLS client certificate from a PKCS#12 (.pfx/.p12) file; password may be NULL. */
WS_API int WS_CALL ws_options_add_client_certificate(ws_options* options, const char* pfx_path, const char* password);

/* ───────────── dispatcher (SynchronizationContext) ─────────────
 * Runs a socket's events on a thread you choose, for example a UI or game loop: set it in the options, then call
 * ws_dispatcher_run from that thread. One dispatcher can serve many sockets. Destroy it after its sockets. */

WS_API ws_dispatcher* WS_CALL ws_dispatcher_create(void);
WS_API void WS_CALL ws_dispatcher_destroy(ws_dispatcher* dispatcher);
/* Waits up to timeout_ms (0: don't wait, -1: forever) for an event, then runs the events that are ready.
 * Returns the number run (0 on timeout), or -1. */
WS_API int WS_CALL ws_dispatcher_run(ws_dispatcher* dispatcher, int timeout_ms);

/* ───────────── socket (WebSocket) ───────────── */

/* Creates a socket and starts connecting in the background; it never blocks. protocols is an array of count
 * subprotocols in order of preference (NULL when count is 0). options and callbacks may be NULL; both are copied.
 * Fails with SyntaxError for a bad URL (not ws/wss/http/https, or with a fragment) or bad/repeated subprotocols. */
WS_API ws_socket* WS_CALL ws_socket_create(const char* url, const char* const* protocols, int count,
                                           const ws_options* options, const ws_callbacks* callbacks);
/* Replaces the callbacks. Events already dispatched are not repeated. */
WS_API int WS_CALL ws_socket_set_callbacks(ws_socket* socket, const ws_callbacks* callbacks);

WS_API char* WS_CALL ws_socket_url(const ws_socket* socket);         /* serialized, after mapping http(s) to ws(s) */
WS_API int WS_CALL ws_socket_ready_state(const ws_socket* socket);   /* enum ws_ready_state, or -1 */
WS_API uint64_t WS_CALL ws_socket_buffered_amount(const ws_socket* socket);
WS_API char* WS_CALL ws_socket_extensions(const ws_socket* socket);  /* "" until open */
WS_API char* WS_CALL ws_socket_protocol(const ws_socket* socket);    /* "" until open or when none was selected */
WS_API int WS_CALL ws_socket_binary_type(const ws_socket* socket);   /* enum ws_binary_type, or -1 */
WS_API int WS_CALL ws_socket_set_binary_type(ws_socket* socket, int binary_type);

/* send(). InvalidStateError while connecting. Once closing or closed, nothing is sent but bufferedAmount grows. */
WS_API int WS_CALL ws_socket_send_text(ws_socket* socket, const char* text);                  /* NUL-terminated UTF-8 */
WS_API int WS_CALL ws_socket_send_text_n(ws_socket* socket, const char* text, size_t length);  /* invalid UTF-8 becomes U+FFFD */
WS_API int WS_CALL ws_socket_send_binary(ws_socket* socket, const void* data, size_t length);  /* copied */
WS_API int WS_CALL ws_socket_send_blob(ws_socket* socket, const ws_blob* blob);

/* close(code, reason). code is WS_CLOSE_NO_CODE or 1000 or 3000-4999 (else InvalidAccessError); reason may be
 * NULL and is at most 123 bytes of UTF-8 (else SyntaxError). While connecting, fails the connection. */
WS_API int WS_CALL ws_socket_close(ws_socket* socket, int code, const char* reason);

/* Drops the connection at once (Dispose). A close event with 1006 follows unless already closed. */
WS_API int WS_CALL ws_socket_abort(ws_socket* socket);
/* Starts the closing handshake with no code, waits up to the close timeout for the close event, then drops the
 * connection (DisposeAsync). Blocks; don't call it from a callback or from the dispatcher's own thread. */
WS_API int WS_CALL ws_socket_shutdown(ws_socket* socket);
/* Drops the connection like ws_socket_abort, stops all callbacks (waiting for one in progress on another thread),
 * and frees the handle. Safe to call from inside a callback. */
WS_API void WS_CALL ws_socket_destroy(ws_socket* socket);

/* ───────────── blob (Blob) ─────────────
 * Immutable bytes with a MIME type. Every ws_blob, including clones and slices, is freed with ws_blob_destroy. */

WS_API ws_blob* WS_CALL ws_blob_create(const void* data, size_t length, const char* type);  /* copies; type may be NULL */
WS_API ws_blob* WS_CALL ws_blob_create_from_parts(const ws_blob_part* parts, int count, const char* type);
WS_API ws_blob* WS_CALL ws_blob_clone(const ws_blob* blob);  /* a new handle to the same blob (no copy) */
WS_API void WS_CALL ws_blob_destroy(ws_blob* blob);
WS_API int64_t WS_CALL ws_blob_size(const ws_blob* blob);    /* or -1 */
WS_API char* WS_CALL ws_blob_type(const ws_blob* blob);      /* lowercased MIME type, "" when unknown */
/* slice(start, end, contentType): bytes [start, end); negative values count from the end. */
WS_API ws_blob* WS_CALL ws_blob_slice(const ws_blob* blob, int64_t start, int64_t end, const char* content_type);
/* bytes() / arrayBuffer(): copies the bytes when capacity >= size. Returns the size either way, or -1. */
WS_API int64_t WS_CALL ws_blob_bytes(const ws_blob* blob, void* buffer, size_t capacity);
/* stream(): copies up to capacity bytes starting at offset. Returns the number copied (0 at the end), or -1. */
WS_API int64_t WS_CALL ws_blob_read(const ws_blob* blob, int64_t offset, void* buffer, size_t capacity);
/* text(): decodes as UTF-8 (BOM stripped, invalid sequences replaced). */
WS_API char* WS_CALL ws_blob_text(const ws_blob* blob);

#ifdef __cplusplus
}
#endif

#endif /* WEBSOCKETS_H */
