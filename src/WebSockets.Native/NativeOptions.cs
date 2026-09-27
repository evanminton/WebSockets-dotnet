using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace WebSockets.Native;

/// <summary>
/// The state behind a ws_options handle: the <see cref="WebSocketOptions"/> a C caller can express, plus the
/// <see cref="SocketsHttpHandler"/> settings that <see cref="WebSocketOptions.ConfigureHandler"/> takes in .NET.
/// Each socket gets its own <see cref="WebSocketOptions"/> built from a snapshot, so the handle can change afterwards.
/// </summary>
internal sealed class NativeOptions
{
    public Uri? BaseUrl { get; set; }

    public string? Origin { get; set; }

    public bool PerMessageDeflate { get; set; } = true;

    public long MaxBufferedAmount { get; set; } = long.MaxValue;

    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan KeepAliveInterval { get; set; } = System.Net.WebSockets.WebSocket.DefaultKeepAliveInterval;

    public Dictionary<string, string> RequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public NativeDispatcher? Dispatcher { get; set; }

    /// <summary>null: the system proxy; "": no proxy; otherwise a proxy URL.</summary>
    public string? Proxy { get; set; }

    public TimeSpan ConnectTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    public bool AcceptAnyCertificate { get; set; }

    public List<X509Certificate2> ClientCertificates { get; } = [];

    public WebSocketOptions Build(SynchronizationContext? fallbackContext)
    {
        var options = new WebSocketOptions
        {
            BaseUrl = BaseUrl,
            Origin = Origin,
            PerMessageDeflate = PerMessageDeflate,
            MaxBufferedAmount = MaxBufferedAmount,
            CloseTimeout = CloseTimeout,
            KeepAliveInterval = KeepAliveInterval,
            SynchronizationContext = (SynchronizationContext?)Dispatcher ?? fallbackContext,
            EventHandlerException = ex => System.Diagnostics.Trace.TraceError("Unhandled exception in a WebSocket callback: {0}", ex),
        };
        foreach (var (name, value) in RequestHeaders)
        {
            options.RequestHeaders[name] = value;
        }

        string? proxy = Proxy;
        TimeSpan connectTimeout = ConnectTimeout;
        bool acceptAny = AcceptAnyCertificate;
        X509Certificate2[] certificates = [.. ClientCertificates];
        options.ConfigureHandler = handler =>
        {
            if (proxy is not null)
            {
                handler.UseProxy = proxy.Length > 0;
                handler.Proxy = proxy.Length > 0 ? new WebProxy(proxy) : null;
            }

            handler.ConnectTimeout = connectTimeout;
            if (acceptAny)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            if (certificates.Length > 0)
            {
                handler.SslOptions.ClientCertificates = [.. certificates];
            }
        };
        return options;
    }
}
