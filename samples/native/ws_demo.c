/*
 * ws_demo.c: exercises the whole C API of WebSocketsNative and checks the results.
 *
 *   ws_demo [ws://127.0.0.1:8765]
 *
 * The blob, options and error checks run offline. The connection checks need the loopback server from
 * samples/EchoServer (dotnet run --project samples/EchoServer). Exit code 0 when every check passed.
 */
#define _CRT_SECURE_NO_WARNINGS /* strncpy is fine here: every buffer is zeroed first and copied with size - 1 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "websockets.h"

#ifdef _WIN32
#  include <windows.h>
#  define sleep_ms(ms) Sleep(ms)
#else
#  include <time.h>
static void sleep_ms(int ms) { struct timespec t = { ms / 1000, (ms % 1000) * 1000000L }; nanosleep(&t, NULL); }
#endif

static int passed, failed;

#define CHECK(cond, what) do { \
    if (cond) { passed++; printf("  ok    %s\n", what); } \
    else { failed++; printf("  FAIL  %s  (line %d; last error: %s %s)\n", what, __LINE__, ws_last_error_name(), ws_last_error()); } \
} while (0)

static int str_is(char* s, const char* expected) {
    int same = s != NULL && strcmp(s, expected) == 0;
    ws_free(s);
    return same;
}

/* ───────────── offline ───────────── */

static void test_blobs(void) {
    puts("blob");
    ws_blob* b = ws_blob_create("Hello, world", 12, "Text/Plain");
    CHECK(b != NULL, "ws_blob_create");
    CHECK(ws_blob_size(b) == 12, "size");
    CHECK(str_is(ws_blob_type(b), "text/plain"), "type is lowercased");
    CHECK(str_is(ws_blob_text(b), "Hello, world"), "text()");

    char buf[32];
    CHECK(ws_blob_bytes(b, NULL, 0) == 12, "bytes() reports the size without a buffer");
    CHECK(ws_blob_bytes(b, buf, sizeof buf) == 12 && memcmp(buf, "Hello, world", 12) == 0, "bytes() copies");
    CHECK(ws_blob_read(b, 7, buf, 3) == 3 && memcmp(buf, "wor", 3) == 0, "read() at an offset");
    CHECK(ws_blob_read(b, 12, buf, 3) == 0, "read() at the end");

    ws_blob* s = ws_blob_slice(b, -5, WS_BLOB_DEFAULT, "x/y");
    CHECK(str_is(ws_blob_text(s), "world") && str_is(ws_blob_type(s), "x/y"), "slice(-5) with a content type");
    ws_blob* s2 = ws_blob_slice(b, WS_BLOB_DEFAULT, 5, NULL);
    CHECK(str_is(ws_blob_text(s2), "Hello") && str_is(ws_blob_type(s2), ""), "slice(undefined, 5)");
    ws_blob* s3 = ws_blob_slice(b, 8, 2, NULL);
    CHECK(ws_blob_size(s3) == 0, "slice with end before start is empty");

    ws_blob_part parts[3];
    memset(parts, 0, sizeof parts);
    parts[0].kind = WS_PART_TEXT;  parts[0].data = "ab";  parts[0].length = 2;
    parts[1].kind = WS_PART_BYTES; parts[1].data = "\x01\x02"; parts[1].length = 2;
    parts[2].kind = WS_PART_BLOB;  parts[2].blob = s2;
    ws_blob* p = ws_blob_create_from_parts(parts, 3, "application/octet-stream");
    CHECK(p != NULL && ws_blob_size(p) == 9, "create_from_parts (text + bytes + blob)");
    CHECK(ws_blob_bytes(p, buf, sizeof buf) == 9 && memcmp(buf, "ab\x01\x02Hello", 9) == 0, "parts are concatenated in order");

    ws_blob* bom = ws_blob_create("\xEF\xBB\xBFhi\xFF", 6, NULL);
    CHECK(str_is(ws_blob_text(bom), "hi\xEF\xBF\xBD"), "text() strips a BOM and replaces invalid UTF-8");

    ws_blob* c = ws_blob_clone(b);
    ws_blob_destroy(b);
    CHECK(str_is(ws_blob_text(c), "Hello, world"), "a clone outlives the original handle");

    ws_blob* empty = ws_blob_create(NULL, 0, NULL);
    CHECK(empty != NULL && ws_blob_size(empty) == 0, "empty blob");

    parts[0].kind = 7;
    CHECK(ws_blob_create_from_parts(parts, 1, NULL) == NULL && strcmp(ws_last_error_name(), "ArgumentOutOfRangeException") == 0,
          "unknown part kind fails");

    ws_blob_destroy(c); ws_blob_destroy(s); ws_blob_destroy(s2); ws_blob_destroy(s3);
    ws_blob_destroy(p); ws_blob_destroy(bom); ws_blob_destroy(empty);
}

static void test_options(void) {
    puts("options");
    ws_options* o = ws_options_create();
    CHECK(o != NULL, "ws_options_create");
    CHECK(ws_options_get_per_message_deflate(o) == 1, "permessage-deflate defaults to on");
    CHECK(ws_options_get_close_timeout(o) == 30000, "close timeout defaults to 30 s");
    CHECK(ws_options_get_keep_alive_interval(o) == 30000, "keep-alive defaults to 30 s");
    CHECK(ws_options_get_max_buffered_amount(o) == INT64_MAX, "max buffered amount defaults to INT64_MAX");
    CHECK(str_is(ws_options_get_origin(o), ""), "no origin by default");

    CHECK(ws_options_set_base_url(o, "https://example.com/app/") == 0 &&
          str_is(ws_options_get_base_url(o), "https://example.com/app/"), "base URL");
    CHECK(ws_options_set_base_url(o, "relative/only") == -1 && ws_last_error_code() == WS_SYNTAX_ERR, "relative base URL is a SyntaxError");
    CHECK(ws_options_set_origin(o, "https://example.com") == 0 && str_is(ws_options_get_origin(o), "https://example.com"), "origin");
    CHECK(ws_options_set_per_message_deflate(o, 0) == 0 && ws_options_get_per_message_deflate(o) == 0, "permessage-deflate off");
    CHECK(ws_options_set_close_timeout(o, 1500) == 0 && ws_options_get_close_timeout(o) == 1500, "close timeout");
    CHECK(ws_options_set_close_timeout(o, -2) == -1, "negative close timeout fails");
    CHECK(ws_options_set_keep_alive_interval(o, 0) == 0 && ws_options_get_keep_alive_interval(o) == 0, "keep-alive off");
    CHECK(ws_options_set_max_buffered_amount(o, 1024) == 0 && ws_options_get_max_buffered_amount(o) == 1024, "max buffered amount");
    CHECK(ws_options_set_request_header(o, "X-Token", "abc") == 0 && str_is(ws_options_get_request_header(o, "x-token"), "abc"),
          "request headers are case-insensitive");
    CHECK(ws_options_set_request_header(o, "X-Token", NULL) == 0 && ws_options_get_request_header(o, "X-Token") == NULL, "header removed");
    CHECK(ws_options_clear_request_headers(o) == 0, "clear headers");
    CHECK(ws_options_set_proxy(o, "") == 0 && ws_options_set_proxy(o, NULL) == 0, "proxy direct / system");
    CHECK(ws_options_set_proxy(o, "not a url") == -1, "bad proxy URL fails");
    CHECK(ws_options_set_connect_timeout(o, 5000) == 0 && ws_options_set_accept_any_certificate(o, 0) == 0, "connect timeout, certificates");
    CHECK(ws_options_add_client_certificate(o, "missing.pfx", NULL) == -1, "missing client certificate fails");
    ws_options_destroy(o);
}

static void test_errors(void) {
    puts("constructor errors (DOMException)");
    const char* two[2] = { "chat", "chat" };
    const char* bad[1] = { "has space" };
    CHECK(ws_socket_create("ftp://example.com/", NULL, 0, NULL, NULL) == NULL &&
          strcmp(ws_last_error_name(), "SyntaxError") == 0 && ws_last_error_code() == WS_SYNTAX_ERR, "ftp: scheme is a SyntaxError");
    CHECK(ws_socket_create("ws://example.com/#", NULL, 0, NULL, NULL) == NULL && ws_last_error_code() == WS_SYNTAX_ERR, "empty fragment is a SyntaxError");
    CHECK(ws_socket_create("ws://example.com/", two, 2, NULL, NULL) == NULL && ws_last_error_code() == WS_SYNTAX_ERR, "repeated subprotocol is a SyntaxError");
    CHECK(ws_socket_create("ws://example.com/", bad, 1, NULL, NULL) == NULL && ws_last_error_code() == WS_SYNTAX_ERR, "invalid subprotocol token is a SyntaxError");
    CHECK(ws_socket_create(NULL, NULL, 0, NULL, NULL) == NULL && strcmp(ws_last_error_name(), "ArgumentNullException") == 0, "NULL URL");
    CHECK(ws_socket_ready_state(NULL) == -1, "NULL socket handle fails");

    ws_options* o = ws_options_create();
    ws_options_set_base_url(o, "https://example.com/app/");
    ws_socket* s = ws_socket_create("chat?x=1", NULL, 0, o, NULL);
    ws_options_destroy(o);
    CHECK(s != NULL && str_is(ws_socket_url(s), "wss://example.com/app/chat?x=1"), "relative URL resolved, https mapped to wss");
    CHECK(ws_socket_ready_state(s) == WS_CONNECTING, "readyState is CONNECTING right after create");
    CHECK(ws_socket_send_text(s, "too early") == -1 && strcmp(ws_last_error_name(), "InvalidStateError") == 0 &&
          ws_last_error_code() == WS_INVALID_STATE_ERR, "send while connecting is an InvalidStateError");
    ws_socket_destroy(s);
}

/* ───────────── online ───────────── */

typedef struct {
    ws_dispatcher* dispatcher;
    int opened, errors, closed, texts, blobs, buffers;
    int close_code, was_clean;
    char protocol[64], last_text[256], close_reason[128], origin[128];
    unsigned char last_binary[16];
    size_t last_binary_length;
    int thread_mismatch;
#ifdef _WIN32
    DWORD thread;
#endif
} state;

static void note_thread(state* st) {
#ifdef _WIN32
    if (st->dispatcher != NULL && GetCurrentThreadId() != st->thread) st->thread_mismatch = 1;
#else
    (void)st;
#endif
}

static void WS_CALL on_open(void* user, ws_socket* s) {
    state* st = (state*)user;
    char* p = ws_socket_protocol(s);
    note_thread(st);
    strncpy(st->protocol, p ? p : "", sizeof st->protocol - 1);
    ws_free(p);
    st->opened++;
}

static void WS_CALL on_message(void* user, ws_socket* s, const ws_message* m) {
    state* st = (state*)user;
    (void)s;
    note_thread(st);
    strncpy(st->origin, m->origin, sizeof st->origin - 1);
    if (m->type == WS_MESSAGE_TEXT) {
        st->texts++;
        strncpy(st->last_text, (const char*)m->data, sizeof st->last_text - 1);
    } else {
        if (m->type == WS_MESSAGE_BLOB && m->blob != NULL && ws_blob_size(m->blob) == (int64_t)m->length) st->blobs++;
        if (m->type == WS_MESSAGE_ARRAYBUFFER && m->blob == NULL) st->buffers++;
        st->last_binary_length = m->length < sizeof st->last_binary ? m->length : sizeof st->last_binary;
        memcpy(st->last_binary, m->data, st->last_binary_length);
    }
}

static void WS_CALL on_error(void* user, ws_socket* s) { (void)s; note_thread((state*)user); ((state*)user)->errors++; }

static void WS_CALL on_close(void* user, ws_socket* s, const ws_close_event* e) {
    state* st = (state*)user;
    (void)s;
    note_thread(st);
    st->closed++;
    st->close_code = e->code;
    st->was_clean = e->was_clean;
    strncpy(st->close_reason, e->reason, sizeof st->close_reason - 1);
}

static ws_callbacks callbacks_for(state* st) {
    ws_callbacks cb;
    cb.on_open = on_open; cb.on_message = on_message; cb.on_error = on_error; cb.on_close = on_close; cb.user = st;
    return cb;
}

/* Runs the dispatcher (or just waits, without one) until cond holds or ~5 s pass. */
#define WAIT_FOR(st, cond) do { \
    int waited_ = 0; \
    while (!(cond) && waited_ < 5000) { \
        if ((st)->dispatcher) ws_dispatcher_run((st)->dispatcher, 50); else sleep_ms(10); \
        waited_ += (st)->dispatcher ? 50 : 10; \
    } \
} while (0)

static void test_echo(const char* base) {
    char url[256];
    const char* protocols[2] = { "chat.v2", "chat.v1" };
    state st;
    ws_options* o;
    ws_callbacks cb;
    ws_socket* s;
    ws_blob* blob;
    char long_reason[130];

    puts("echo (events on a dispatcher)");
    memset(&st, 0, sizeof st);
    st.dispatcher = ws_dispatcher_create();
#ifdef _WIN32
    st.thread = GetCurrentThreadId();
#endif
    o = ws_options_create();
    ws_options_set_dispatcher(o, st.dispatcher);
    ws_options_set_origin(o, "https://demo.example");
    cb = callbacks_for(&st);
    snprintf(url, sizeof url, "%s/echo", base);
    s = ws_socket_create(url, protocols, 2, o, &cb);
    ws_options_destroy(o);
    CHECK(s != NULL, "ws_socket_create");

    WAIT_FOR(&st, st.opened);
    CHECK(st.opened == 1 && ws_socket_ready_state(s) == WS_OPEN, "open event, readyState OPEN");
    CHECK(strcmp(st.protocol, "chat.v2") == 0, "server selected the first subprotocol");
    {
        char* ext = ws_socket_extensions(s);
        CHECK(ext != NULL && strncmp(ext, "permessage-deflate", 18) == 0, "permessage-deflate negotiated");
        if (ext) printf("        extensions: %s\n", ext);
        ws_free(ext);
    }

    CHECK(ws_socket_send_text(s, "hello \xE2\x9C\x93") == 0, "send text");
    WAIT_FOR(&st, st.texts == 1);
    CHECK(strcmp(st.last_text, "hello \xE2\x9C\x93") == 0, "text echoed (UTF-8 kept)");
    CHECK(strcmp(st.origin, base) == 0, "message origin is the URL's origin");

    CHECK(ws_socket_binary_type(s) == WS_BINARY_BLOB, "binaryType defaults to blob");
    CHECK(ws_socket_send_binary(s, "\x00\x01\x02\xFF", 4) == 0, "send binary");
    WAIT_FOR(&st, st.blobs == 1);
    CHECK(st.blobs == 1 && st.last_binary_length == 4 && memcmp(st.last_binary, "\x00\x01\x02\xFF", 4) == 0, "binary echoed as a blob");

    CHECK(ws_socket_set_binary_type(s, WS_BINARY_ARRAYBUFFER) == 0 && ws_socket_binary_type(s) == WS_BINARY_ARRAYBUFFER, "binaryType = arraybuffer");
    CHECK(ws_socket_set_binary_type(s, 9) == -1, "invalid binaryType fails");
    blob = ws_blob_create("blob!", 5, NULL);
    CHECK(ws_socket_send_blob(s, blob) == 0, "send blob");
    ws_blob_destroy(blob);
    WAIT_FOR(&st, st.buffers == 1);
    CHECK(st.buffers == 1 && st.last_binary_length == 5 && memcmp(st.last_binary, "blob!", 5) == 0, "blob echoed as an arraybuffer");

    CHECK(ws_socket_send_text_n(s, "abc\xFF", 4) == 0, "send_text_n with invalid UTF-8");
    WAIT_FOR(&st, st.texts == 2);
    CHECK(strcmp(st.last_text, "abc\xEF\xBF\xBD") == 0, "invalid UTF-8 sent as U+FFFD");

    CHECK(ws_socket_close(s, 1001, NULL) == -1 && ws_last_error_code() == WS_INVALID_ACCESS_ERR, "close(1001) is an InvalidAccessError");
    CHECK(ws_socket_close(s, 70000, NULL) == -1 && ws_last_error_code() == WS_INVALID_ACCESS_ERR, "close(70000) is an InvalidAccessError");
    memset(long_reason, 'x', 124); long_reason[124] = 0;
    CHECK(ws_socket_close(s, 1000, long_reason) == -1 && ws_last_error_code() == WS_SYNTAX_ERR, "124-byte reason is a SyntaxError");

    CHECK(ws_socket_close(s, 4001, "done") == 0, "close(4001, \"done\")");
    CHECK(ws_socket_ready_state(s) == WS_CLOSING, "readyState CLOSING");
    CHECK(ws_socket_send_text(s, "late") == 0 && ws_socket_buffered_amount(s) == 4, "send after close only grows bufferedAmount");
    WAIT_FOR(&st, st.closed);
    CHECK(st.closed == 1 && st.close_code == 4001 && st.was_clean && strcmp(st.close_reason, "done") == 0, "close event 4001 \"done\", clean");
    CHECK(st.errors == 0, "no error event");
    CHECK(ws_socket_ready_state(s) == WS_CLOSED, "readyState CLOSED");
    CHECK(!st.thread_mismatch, "every event ran on the dispatcher's thread");
    ws_socket_destroy(s);
    ws_dispatcher_destroy(st.dispatcher);
}

static void test_library_thread(const char* base) {
    char url[256];
    state st;
    ws_callbacks cb;
    ws_socket* s;

    puts("server close (events on a library thread)");
    memset(&st, 0, sizeof st);
    cb = callbacks_for(&st);
    snprintf(url, sizeof url, "%s/server-close?messages=3&code=4321&reason=bye", base);
    s = ws_socket_create(url, NULL, 0, NULL, &cb);
    WAIT_FOR(&st, st.closed);
    CHECK(st.opened == 1 && st.texts == 3 && strcmp(st.last_text, "2") == 0, "open, then 3 messages in order");
    CHECK(st.closed == 1 && st.close_code == 4321 && st.was_clean && strcmp(st.close_reason, "bye") == 0, "server's code and reason reported");
    ws_socket_destroy(s);

    puts("graceful shutdown");
    memset(&st, 0, sizeof st);
    snprintf(url, sizeof url, "%s/echo", base);
    s = ws_socket_create(url, NULL, 0, NULL, &cb);
    WAIT_FOR(&st, st.opened);
    CHECK(ws_socket_shutdown(s) == 0, "ws_socket_shutdown returns");
    CHECK(st.closed == 1 && st.close_code == 1000 && st.was_clean, "closed cleanly before shutdown returned");
    ws_socket_destroy(s);

    puts("abort");
    memset(&st, 0, sizeof st);
    s = ws_socket_create(url, NULL, 0, NULL, &cb);
    WAIT_FOR(&st, st.opened);
    CHECK(ws_socket_abort(s) == 0, "ws_socket_abort");
    WAIT_FOR(&st, st.closed);
    CHECK(st.closed == 1 && st.close_code == 1006 && !st.was_clean, "close event 1006, not clean");
    ws_socket_destroy(s);

    puts("connection failure");
    memset(&st, 0, sizeof st);
    s = ws_socket_create("ws://127.0.0.1:1/", NULL, 0, NULL, &cb);
    WAIT_FOR(&st, st.closed);
    CHECK(st.opened == 0 && st.errors == 1 && st.closed == 1 && st.close_code == 1006, "error, then close 1006");
    ws_socket_destroy(s);
}

int main(int argc, char** argv) {
    const char* base = argc > 1 ? argv[1] : "ws://127.0.0.1:8765";
    printf("WebSocketsNative %s\n", ws_version());
    test_blobs();
    test_options();
    test_errors();
    if (argc > 1 && strcmp(argv[1], "--offline") == 0) {
        puts("(connection checks skipped)");
    } else {
        test_echo(base);
        test_library_thread(base);
    }
    printf("\n%d passed, %d failed\n", passed, failed);
    return failed == 0 ? 0 : 1;
}
