using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WebSockets.Tests;

/// <summary>What the server saw on one connection, keyed by the <c>id</c> query parameter.</summary>
public sealed class ServerRecord
{
    public ConcurrentQueue<(WebSocketMessageType Type, byte[] Data)> Messages { get; } = new();

    public TaskCompletionSource<(WebSocketCloseStatus? Status, string? Description)> Closed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? Origin { get; set; }

    public string? RequestedProtocols { get; set; }
}

/// <summary>A Kestrel server on a loopback port with WebSocket endpoints that exercise the client.</summary>
public sealed class TestServer : IAsyncLifetime
{
    private WebApplication? _app;

    public ConcurrentDictionary<string, ServerRecord> Records { get; } = new();

    public string BaseUrl { get; private set; } = "";

    public string Ws(string path) => "ws" + BaseUrl["http".Length..] + path;

    public ServerRecord Record(string id) => Records.GetOrAdd(id, _ => new ServerRecord());

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseWebSockets();

        // Echoes every message; picks the first requested subprotocol unless ?protocol=none.
        // Records what it received and answers the client's Close frame with the same code.
        _app.Map("/echo", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            ServerRecord record = Record(context.Request.Query["id"].ToString());
            record.Origin = context.Request.Headers.Origin.ToString();
            record.RequestedProtocols = context.Request.Headers["Sec-WebSocket-Protocol"].ToString();
            string? protocol = context.Request.Query["protocol"] == "none" ? null : context.WebSockets.WebSocketRequestedProtocols.FirstOrDefault();
            using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                SubProtocol = protocol,
                DangerousEnableCompression = true,
            });

            bool silent = context.Request.Query["silent"] == "1";
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    record.Closed.TrySetResult((result.CloseStatus, result.CloseStatusDescription));
                    if (silent)
                    {
                        await Task.Delay(Timeout.Infinite, context.RequestAborted).ContinueWith(_ => { });
                        return;
                    }

                    // System.Net.WebSockets cannot send a Close frame without a code, so answer an empty one with 1000.
                    WebSocketCloseStatus echo = result.CloseStatus is null or WebSocketCloseStatus.Empty ? WebSocketCloseStatus.NormalClosure : result.CloseStatus.Value;
                    await socket.CloseOutputAsync(echo, result.CloseStatusDescription, CancellationToken.None);
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    byte[] data = message.ToArray();
                    message.SetLength(0);
                    record.Messages.Enqueue((result.MessageType, data));
                    await socket.SendAsync(data, result.MessageType, true, CancellationToken.None);
                }
            }
        });

        // Sends ?messages=N numbered text messages, then closes with ?code= and ?reason=.
        _app.Map("/server-close", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            int count = int.Parse(context.Request.Query["messages"].FirstOrDefault() ?? "0");
            for (int i = 0; i < count; i++)
            {
                await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(i.ToString()), WebSocketMessageType.Text, true, CancellationToken.None);
            }

            var code = (WebSocketCloseStatus)int.Parse(context.Request.Query["code"].FirstOrDefault() ?? "1000");
            await socket.CloseAsync(code, context.Request.Query["reason"].FirstOrDefault() ?? "", CancellationToken.None);
        });

        // Accepts, then drops the TCP connection without a Close frame.
        _app.Map("/drop", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await Task.Delay(100);
            socket.Abort();
            context.Abort();
        });

        _app.Map("/forbidden", (HttpContext context) => context.Response.StatusCode = 403);

        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}

/// <summary>A TCP listener that accepts connections and never answers, so WebSockets stay CONNECTING.</summary>
public sealed class BlackHole : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<Socket> _accepted = new();

    public BlackHole()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public string Url => $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                Socket socket = await _listener.AcceptSocketAsync();
                lock (_accepted)
                {
                    _accepted.Add(socket);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        lock (_accepted)
        {
            _accepted.ForEach(s => s.Dispose());
        }
    }
}
