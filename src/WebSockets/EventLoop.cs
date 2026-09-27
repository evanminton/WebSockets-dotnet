using System.Diagnostics;
using System.Threading.Channels;

namespace WebSockets;

/// <summary>
/// The task queue WebSocket events are dispatched on. Tasks run one at a time, in the order they were queued.
/// With a <see cref="SynchronizationContext"/>, each task is posted to it only after the previous one finished,
/// since contexts such as the default one do not promise to run posts in order.
/// </summary>
internal sealed class EventLoop
{
    private readonly SynchronizationContext? _context;
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<Exception>? _onException;

    public EventLoop(SynchronizationContext? context, Action<Exception>? onException)
    {
        _context = context;
        _onException = onException;
        _ = Task.Run(RunAsync);
    }

    public void Queue(Action task) => _queue.Writer.TryWrite(task);

    /// <summary>Stops the loop once the tasks already queued have run.</summary>
    public void Complete() => _queue.Writer.TryComplete();

    private async Task RunAsync()
    {
        await foreach (Action task in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_context is null)
            {
                Run(task);
                continue;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _context.Post(
                _ =>
                {
                    try
                    {
                        Run(task);
                    }
                    finally
                    {
                        done.SetResult();
                    }
                },
                null);
            await done.Task.ConfigureAwait(false);
        }
    }

    private void Run(Action task)
    {
        try
        {
            task();
        }
        catch (Exception ex)
        {
            if (_onException is not null)
            {
                _onException(ex);
            }
            else
            {
                Trace.TraceError("Unhandled exception in a WebSocket event handler: {0}", ex);
            }
        }
    }
}
