// A loopback WebSocket server for the native demo (samples/native/ws_demo.c).
//   dotnet run --project samples/EchoServer -- [port]     (default 8765)
//   /echo           echoes every message, selects the first requested subprotocol, answers Close with the same code
//   /server-close   sends ?messages=N numbered text messages, then closes with ?code= and ?reason=
using System.Net.WebSockets;
using System.Text;

int port = args.Length > 0 ? int.Parse(args[0]) : 8765;
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();
var app = builder.Build();
app.UseWebSockets();

app.Map("/echo", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
    {
        SubProtocol = context.WebSockets.WebSocketRequestedProtocols.FirstOrDefault(),
        DangerousEnableCompression = true,
    });
    var buffer = new byte[64 * 1024];
    var message = new MemoryStream();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            WebSocketCloseStatus echo = result.CloseStatus is null or WebSocketCloseStatus.Empty ? WebSocketCloseStatus.NormalClosure : result.CloseStatus.Value;
            await socket.CloseOutputAsync(echo, result.CloseStatusDescription, CancellationToken.None);
            return;
        }

        message.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
        {
            await socket.SendAsync(message.ToArray(), result.MessageType, true, CancellationToken.None);
            message.SetLength(0);
        }
    }
});

app.Map("/server-close", async (HttpContext context) =>
{
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    int count = int.Parse(context.Request.Query["messages"].FirstOrDefault() ?? "0");
    for (int i = 0; i < count; i++)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(i.ToString()), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    var code = (WebSocketCloseStatus)int.Parse(context.Request.Query["code"].FirstOrDefault() ?? "1000");
    await socket.CloseAsync(code, context.Request.Query["reason"].FirstOrDefault() ?? "", CancellationToken.None);
});

Console.WriteLine($"Listening on ws://127.0.0.1:{port}");
app.Run();
