using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static WebSockets.Native.Interop;

namespace WebSockets.Native;

/// <summary>
/// The C entry points declared in include/websockets.h. Conventions: strings in are UTF-8; strings out are UTF-8,
/// allocated here and freed with ws_free; int results are 0 (or a value) on success and -1 on failure, with the reason
/// in ws_last_error. No exception ever crosses into C.
/// </summary>
internal static unsafe class Exports
{
    private static byte* s_version;

    // ─────────────── library ───────────────

    [UnmanagedCallersOnly(EntryPoint = "ws_free")]
    public static void Free(void* p)
    {
        if (p != null) NativeMemory.Free(p);
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_last_error")]
    public static byte* GetLastError() => LastError;

    [UnmanagedCallersOnly(EntryPoint = "ws_last_error_name")]
    public static byte* GetLastErrorName() => LastErrorName;

    [UnmanagedCallersOnly(EntryPoint = "ws_last_error_code")]
    public static int GetLastErrorCode() => LastErrorCode;

    [UnmanagedCallersOnly(EntryPoint = "ws_version")]
    public static byte* Version()
    {
        if (s_version == null)
        {
            string version = typeof(WebSocket).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(WebSocket).Assembly.GetName().Version?.ToString() ?? "";
            s_version = Alloc(version);
        }

        return s_version;
    }

    // ─────────────── options ───────────────

    [UnmanagedCallersOnly(EntryPoint = "ws_options_create")]
    public static void* OptionsCreate()
    {
        try { return (void*)NewHandle(new NativeOptions()); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_destroy")]
    public static void OptionsDestroy(void* options)
    {
        try { FreeHandle(options); }
        catch (Exception ex) { Fail(ex); }
    }

    private static NativeOptions Options(void* handle) => Get<NativeOptions>(handle, "options");

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_base_url")]
    public static int OptionsSetBaseUrl(void* options, byte* url)
    {
        try
        {
            string? text = Str(url);
            Uri? parsed = null;
            if (text is not null && !Uri.TryCreate(text, UriKind.Absolute, out parsed))
            {
                throw new DomException(DomException.SyntaxError, $"The base URL '{text}' is not an absolute URL.");
            }

            Options(options).BaseUrl = parsed;
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_get_base_url")]
    public static byte* OptionsGetBaseUrl(void* options)
    {
        try { return Alloc(Options(options).BaseUrl?.AbsoluteUri ?? ""); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_origin")]
    public static int OptionsSetOrigin(void* options, byte* origin)
    {
        try { Options(options).Origin = Str(origin); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_get_origin")]
    public static byte* OptionsGetOrigin(void* options)
    {
        try { return Alloc(Options(options).Origin ?? ""); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_per_message_deflate")]
    public static int OptionsSetPerMessageDeflate(void* options, int enabled)
    {
        try { Options(options).PerMessageDeflate = enabled != 0; return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_get_per_message_deflate")]
    public static int OptionsGetPerMessageDeflate(void* options)
    {
        try { return Options(options).PerMessageDeflate ? 1 : 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_max_buffered_amount")]
    public static int OptionsSetMaxBufferedAmount(void* options, long bytes)
    {
        try { Options(options).MaxBufferedAmount = bytes; return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_get_max_buffered_amount")]
    public static long OptionsGetMaxBufferedAmount(void* options)
    {
        try { return Options(options).MaxBufferedAmount; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_close_timeout")]
    public static int OptionsSetCloseTimeout(void* options, long milliseconds)
    {
        try { Options(options).CloseTimeout = Milliseconds(milliseconds, allowInfinite: true); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_get_close_timeout")]
    public static long OptionsGetCloseTimeout(void* options)
    {
        try { return (long)Options(options).CloseTimeout.TotalMilliseconds; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_keep_alive_interval")]
    public static int OptionsSetKeepAliveInterval(void* options, long milliseconds)
    {
        try { Options(options).KeepAliveInterval = Milliseconds(milliseconds, allowInfinite: false); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_get_keep_alive_interval")]
    public static long OptionsGetKeepAliveInterval(void* options)
    {
        try { return (long)Options(options).KeepAliveInterval.TotalMilliseconds; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_request_header")]
    public static int OptionsSetRequestHeader(void* options, byte* name, byte* value)
    {
        try
        {
            var headers = Options(options).RequestHeaders;
            string key = Need(name, nameof(name));
            if (Str(value) is { } text) headers[key] = text;
            else headers.Remove(key);
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_get_request_header")]
    public static byte* OptionsGetRequestHeader(void* options, byte* name)
    {
        try { return Options(options).RequestHeaders.TryGetValue(Need(name, nameof(name)), out string? value) ? Alloc(value) : null; }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_clear_request_headers")]
    public static int OptionsClearRequestHeaders(void* options)
    {
        try { Options(options).RequestHeaders.Clear(); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_dispatcher")]
    public static int OptionsSetDispatcher(void* options, void* dispatcher)
    {
        try
        {
            Options(options).Dispatcher = dispatcher == null ? null : Get<NativeDispatcher>(dispatcher, nameof(dispatcher));
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_proxy")]
    public static int OptionsSetProxy(void* options, byte* proxyUrl)
    {
        try
        {
            string? proxy = Str(proxyUrl);
            if (!string.IsNullOrEmpty(proxy) && !Uri.TryCreate(proxy, UriKind.Absolute, out _))
            {
                throw new DomException(DomException.SyntaxError, $"The proxy URL '{proxy}' is not an absolute URL.");
            }

            Options(options).Proxy = proxy;
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_connect_timeout")]
    public static int OptionsSetConnectTimeout(void* options, long milliseconds)
    {
        try { Options(options).ConnectTimeout = Milliseconds(milliseconds, allowInfinite: true); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_set_accept_any_certificate")]
    public static int OptionsSetAcceptAnyCertificate(void* options, int enabled)
    {
        try { Options(options).AcceptAnyCertificate = enabled != 0; return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_options_add_client_certificate")]
    public static int OptionsAddClientCertificate(void* options, byte* pfxPath, byte* password)
    {
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(Need(pfxPath, "pfx_path"), Str(password));
            Options(options).ClientCertificates.Add(certificate);
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static TimeSpan Milliseconds(long milliseconds, bool allowInfinite)
    {
        if (allowInfinite && milliseconds == -1) return Timeout.InfiniteTimeSpan;
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    // ─────────────── dispatcher ───────────────

    [UnmanagedCallersOnly(EntryPoint = "ws_dispatcher_create")]
    public static void* DispatcherCreate()
    {
        try { return (void*)NewHandle(new NativeDispatcher()); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_dispatcher_destroy")]
    public static void DispatcherDestroy(void* dispatcher)
    {
        try { FreeHandle(dispatcher); }
        catch (Exception ex) { Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_dispatcher_run")]
    public static int DispatcherRun(void* dispatcher, int timeoutMs)
    {
        try
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMs, -1);
            return Get<NativeDispatcher>(dispatcher, nameof(dispatcher)).Run(timeoutMs);
        }
        catch (Exception ex) { return Fail(ex); }
    }

    // ─────────────── socket ───────────────

    private static NativeSocket Socket(void* handle) => Get<NativeSocket>(handle, "socket");

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_create")]
    public static void* SocketCreate(byte* url, byte** protocols, int count, void* options, NativeCallbacks* callbacks)
    {
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (protocols == null && count > 0) throw new ArgumentNullException(nameof(protocols));
            var list = new string[count];
            for (int i = 0; i < count; i++)
            {
                list[i] = Need(protocols[i], $"protocols[{i}]");
            }

            NativeOptions settings = options == null ? new NativeOptions() : Options(options);
            GatedContext? gate = settings.Dispatcher is null ? new GatedContext() : null;
            var socket = new WebSocket(Need(url, nameof(url)), list, settings.Build(gate));
            var native = new NativeSocket(socket, callbacks == null ? default : *callbacks);
            nint handle = NewHandle(native);
            native.Handle = handle;
            gate?.Release();
            return (void*)handle;
        }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_set_callbacks")]
    public static int SocketSetCallbacks(void* socket, NativeCallbacks* callbacks)
    {
        try { Socket(socket).SetCallbacks(callbacks == null ? default : *callbacks); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_url")]
    public static byte* SocketUrl(void* socket)
    {
        try { return Alloc(Socket(socket).Socket.Url); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_ready_state")]
    public static int SocketReadyState(void* socket)
    {
        try { return (int)Socket(socket).Socket.ReadyState; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_buffered_amount")]
    public static ulong SocketBufferedAmount(void* socket)
    {
        try { return Socket(socket).Socket.BufferedAmount; }
        catch (Exception ex) { Fail(ex); return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_extensions")]
    public static byte* SocketExtensions(void* socket)
    {
        try { return Alloc(Socket(socket).Socket.Extensions); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_protocol")]
    public static byte* SocketProtocol(void* socket)
    {
        try { return Alloc(Socket(socket).Socket.Protocol); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_binary_type")]
    public static int SocketBinaryType(void* socket)
    {
        try { return (int)Socket(socket).Socket.BinaryType; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_set_binary_type")]
    public static int SocketSetBinaryType(void* socket, int binaryType)
    {
        try
        {
            if (binaryType is not ((int)BinaryType.Blob or (int)BinaryType.ArrayBuffer))
            {
                throw new ArgumentOutOfRangeException(nameof(binaryType), binaryType, "Use WS_BINARY_BLOB or WS_BINARY_ARRAYBUFFER.");
            }

            Socket(socket).Socket.BinaryType = (BinaryType)binaryType;
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_send_text")]
    public static int SocketSendText(void* socket, byte* text)
    {
        try { Socket(socket).Socket.Send(Need(text, nameof(text))); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_send_text_n")]
    public static int SocketSendTextN(void* socket, byte* text, nuint length)
    {
        try { Socket(socket).Socket.Send(Encoding.UTF8.GetString(Bytes(text, length))); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_send_binary")]
    public static int SocketSendBinary(void* socket, void* data, nuint length)
    {
        try { Socket(socket).Socket.Send(Bytes(data, length)); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_send_blob")]
    public static int SocketSendBlob(void* socket, void* blob)
    {
        try { Socket(socket).Socket.Send(Get<Blob>(blob, nameof(blob))); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_close")]
    public static int SocketClose(void* socket, int code, byte* reason)
    {
        try
        {
            ushort? status = code switch
            {
                -1 => null,
                >= 0 and <= ushort.MaxValue => (ushort)code,
                // Out of the unsigned short range: the Web IDL conversion would wrap it; any such value is invalid here.
                _ => throw new DomException(DomException.InvalidAccessError, $"The close code must be 1000 or between 3000 and 4999, but was {code}."),
            };
            Socket(socket).Socket.Close(status, Str(reason));
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_abort")]
    public static int SocketAbort(void* socket)
    {
        try { Socket(socket).Socket.Dispose(); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_shutdown")]
    public static int SocketShutdown(void* socket)
    {
        try { Socket(socket).Socket.DisposeAsync().AsTask().GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_socket_destroy")]
    public static void SocketDestroy(void* socket)
    {
        try
        {
            if (socket == null) return;
            NativeSocket native = Socket(socket);
            native.Detach();
            native.Socket.Dispose();
            FreeHandle(socket);
        }
        catch (Exception ex) { Fail(ex); }
    }

    // ─────────────── blob ───────────────

    private static Blob BlobOf(void* handle) => Get<Blob>(handle, "blob");

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_create")]
    public static void* BlobCreate(void* data, nuint length, byte* type)
    {
        try { return (void*)NewHandle(new Blob(Bytes(data, length), Str(type) ?? "")); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_create_from_parts")]
    public static void* BlobCreateFromParts(NativeBlobPart* parts, int count, byte* type)
    {
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (parts == null && count > 0) throw new ArgumentNullException(nameof(parts));
            var list = new object[count];
            for (int i = 0; i < count; i++)
            {
                NativeBlobPart part = parts[i];
                list[i] = part.Kind switch
                {
                    0 => Bytes(part.Data, part.Length).ToArray(),
                    1 => Encoding.UTF8.GetString(Bytes(part.Data, part.Length)),
                    2 => Get<Blob>(part.Blob, $"parts[{i}].blob"),
                    _ => throw new ArgumentOutOfRangeException($"parts[{i}].kind", part.Kind, "Use WS_PART_BYTES, WS_PART_TEXT or WS_PART_BLOB."),
                };
            }

            return (void*)NewHandle(new Blob(list, Str(type) ?? ""));
        }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_clone")]
    public static void* BlobClone(void* blob)
    {
        try { return (void*)NewHandle(BlobOf(blob)); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_destroy")]
    public static void BlobDestroy(void* blob)
    {
        try { FreeHandle(blob); }
        catch (Exception ex) { Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_size")]
    public static long BlobSize(void* blob)
    {
        try { return BlobOf(blob).Size; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_type")]
    public static byte* BlobType(void* blob)
    {
        try { return Alloc(BlobOf(blob).Type); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_slice")]
    public static void* BlobSlice(void* blob, long start, long end, byte* contentType)
    {
        try
        {
            long? from = start == long.MinValue ? null : start;
            long? to = end == long.MinValue ? null : end;
            return (void*)NewHandle(BlobOf(blob).Slice(from, to, Str(contentType)));
        }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_bytes")]
    public static long BlobBytes(void* blob, void* buffer, nuint capacity)
    {
        try
        {
            ReadOnlySpan<byte> data = BlobOf(blob).Memory.Span;
            if (buffer != null && capacity >= (nuint)data.Length)
            {
                data.CopyTo(new Span<byte>(buffer, data.Length));
            }

            return data.Length;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_read")]
    public static long BlobRead(void* blob, long offset, void* buffer, nuint capacity)
    {
        try
        {
            ReadOnlySpan<byte> data = BlobOf(blob).Memory.Span;
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= data.Length) return 0;
            int count = (int)Math.Min((ulong)(data.Length - offset), (ulong)capacity);
            if (buffer == null && count > 0) throw new ArgumentNullException(nameof(buffer));
            data.Slice((int)offset, count).CopyTo(new Span<byte>(buffer, count));
            return count;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ws_blob_text")]
    public static byte* BlobText(void* blob)
    {
        try { return Alloc(BlobOf(blob).TextAsync().GetAwaiter().GetResult()); }
        catch (Exception ex) { return FailNull<byte>(ex); }
    }
}
