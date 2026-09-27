using System.Collections.Concurrent;
using System.Threading.Channels;

namespace WebSockets.Tests;

/// <summary>Records a WebSocket's events in order and lets tests await them.</summary>
public sealed class EventLog
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CloseEventArgs> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<MessageEventArgs> _messages = Channel.CreateUnbounded<MessageEventArgs>();

    public EventLog(WebSocket socket)
    {
        socket.OnOpen += (_, _) =>
        {
            Events.Enqueue($"open:{socket.ReadyState}");
            _opened.TrySetResult();
        };
        socket.OnMessage += (_, e) =>
        {
            Events.Enqueue("message");
            _messages.Writer.TryWrite(e);
        };
        socket.OnError += (_, _) => Events.Enqueue($"error:{socket.ReadyState}");
        socket.OnClose += (_, e) =>
        {
            Events.Enqueue($"close:{socket.ReadyState}");
            _closed.TrySetResult(e);
        };
    }

    /// <summary>Each event as "name:readyState-during-dispatch", or "message".</summary>
    public ConcurrentQueue<string> Events { get; } = new();

    public Task Opened => _opened.Task.WaitAsync(Timeout);

    public Task<CloseEventArgs> Closed => _closed.Task.WaitAsync(Timeout);

    public async Task<MessageEventArgs> NextMessageAsync()
    {
        using var cts = new CancellationTokenSource(Timeout);
        return await _messages.Reader.ReadAsync(cts.Token);
    }
}
