using System.Collections.Concurrent;

namespace WebSockets.Native;

/// <summary>
/// The state behind a ws_dispatcher handle: a <see cref="SynchronizationContext"/> whose posts run when the C caller
/// calls ws_dispatcher_run, on the caller's thread.
/// </summary>
internal sealed class NativeDispatcher : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        using var done = new ManualResetEventSlim();
        Post(s => { try { d(s); } finally { done.Set(); } }, state);
        done.Wait();
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Waits up to <paramref name="timeoutMs"/> for a post, then runs every post that is ready.</summary>
    public int Run(int timeoutMs)
    {
        if (!_queue.TryTake(out var item, timeoutMs))
        {
            return 0;
        }

        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        try
        {
            int count = 0;
            do
            {
                item.Callback(item.State);
                count++;
            }
            while (_queue.TryTake(out item));
            return count;
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}

/// <summary>
/// Runs posts on the thread pool, but only once <see cref="Release"/> is called. Sockets created without a dispatcher use
/// it so no event can be dispatched before ws_socket_create has attached the callbacks.
/// </summary>
internal sealed class GatedContext : SynchronizationContext
{
    private readonly Lock _gate = new();
    private List<(SendOrPostCallback Callback, object? State)>? _pending = [];

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                _pending.Add((d, state));
                return;
            }
        }

        ThreadPool.UnsafeQueueUserWorkItem(s => d(s), state, preferLocal: false);
    }

    public override SynchronizationContext CreateCopy() => this;

    public void Release()
    {
        List<(SendOrPostCallback Callback, object? State)> pending;
        lock (_gate)
        {
            pending = _pending!;
            _pending = null;
        }

        foreach (var (callback, state) in pending)
        {
            ThreadPool.UnsafeQueueUserWorkItem(s => callback(s), state, preferLocal: false);
        }
    }
}
