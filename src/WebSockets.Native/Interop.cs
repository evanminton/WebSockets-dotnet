using System.Runtime.InteropServices;
using System.Text;

namespace WebSockets.Native;

/// <summary>ws_message in websockets.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeMessage
{
    public int Type;
    public byte* Data;
    public nuint Length;
    public void* Blob;
    public byte* Origin;
    public byte* LastEventId;
}

/// <summary>ws_close_event in websockets.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCloseEvent
{
    public int WasClean;
    public ushort Code;
    public byte* Reason;
}

/// <summary>ws_callbacks in websockets.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCallbacks
{
    public delegate* unmanaged[Cdecl]<void*, void*, void> OnOpen;
    public delegate* unmanaged[Cdecl]<void*, void*, NativeMessage*, void> OnMessage;
    public delegate* unmanaged[Cdecl]<void*, void*, void> OnError;
    public delegate* unmanaged[Cdecl]<void*, void*, NativeCloseEvent*, void> OnClose;
    public void* User;
}

/// <summary>ws_blob_part in websockets.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeBlobPart
{
    public int Kind;
    public void* Data;
    public nuint Length;
    public void* Blob;
}

/// <summary>Strings, handles and the per-thread last error shared by the exports.</summary>
internal static unsafe class Interop
{
    public const int MessageText = 0;
    public const int MessageArrayBuffer = 1;
    public const int MessageBlob = 2;

    [ThreadStatic] private static nint t_error;
    [ThreadStatic] private static nint t_errorName;
    [ThreadStatic] private static int t_errorCode;

    public static readonly byte* Empty = Alloc("");

    /// <summary>Copies <paramref name="s"/> to a NUL-terminated UTF-8 string the caller frees with ws_free.</summary>
    public static byte* Alloc(string s)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        byte* p = (byte*)NativeMemory.Alloc((nuint)n + 1);
        Encoding.UTF8.GetBytes(s, new Span<byte>(p, n));
        p[n] = 0;
        return p;
    }

    /// <summary>A NUL-terminated UTF-8 copy of <paramref name="s"/> for pinning during a callback.</summary>
    public static byte[] Utf8Z(string s)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }

    public static string? Str(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((nint)p);

    public static string Need(byte* p, string name) => Str(p) ?? throw new ArgumentNullException(name);

    public static ReadOnlySpan<byte> Bytes(void* data, nuint length)
    {
        if (length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length), "At most 2 GiB at a time.");
        if (data == null && length > 0) throw new ArgumentNullException(nameof(data));
        return new ReadOnlySpan<byte>(data, (int)length);
    }

    public static nint NewHandle(object target) => GCHandle.ToIntPtr(GCHandle.Alloc(target));

    public static T Get<T>(void* handle, string name) where T : class
    {
        if (handle == null) throw new ArgumentNullException(name);
        return GCHandle.FromIntPtr((nint)handle).Target as T ?? throw new ArgumentException($"Not a {name} handle.", name);
    }

    public static void FreeHandle(void* handle)
    {
        if (handle != null) GCHandle.FromIntPtr((nint)handle).Free();
    }

    public static byte* LastError => t_error == 0 ? Empty : (byte*)t_error;

    public static byte* LastErrorName => t_errorName == 0 ? Empty : (byte*)t_errorName;

    public static int LastErrorCode => t_errorCode;

    public static int Fail(Exception ex)
    {
        if (ex is AggregateException { InnerException: { } inner }) ex = inner;
        Replace(ref t_error, ex.Message);
        if (ex is DomException dom)
        {
            Replace(ref t_errorName, dom.Name);
            t_errorCode = dom.Code;
        }
        else
        {
            Replace(ref t_errorName, ex.GetType().Name);
            t_errorCode = 0;
        }

        return -1;
    }

    public static T* FailNull<T>(Exception ex) where T : unmanaged
    {
        Fail(ex);
        return null;
    }

    private static void Replace(ref nint slot, string value)
    {
        nint old = slot;
        slot = (nint)Alloc(value);
        if (old != 0) NativeMemory.Free((void*)old);
    }
}
