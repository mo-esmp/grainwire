namespace GrainWire.Client.Internal;

/// <summary>
/// Runs the app's handlers one at a time, in the order they were queued, on a thread-pool thread. Handlers are queued
/// under the client's lock, which fixes their order, and never run on the thread that queued them: not while that lock
/// is held, and not on the connection's receive path. However slow a handler is, the client keeps reading its
/// connection, so what waits for the app waits here, where it is counted, and not in the MQTT client's own buffers.
/// </summary>
/// <param name="limit">
/// How many calls may wait. The queue does not enforce it: callers ask <see cref="HasRoom"/> before they queue the
/// calls of a message that may be skipped, and queue the few that must not be lost (a closure, a state change) regardless.
/// </param>
/// <param name="onError">Called with the exception of a handler that throws.</param>
internal sealed class CallbackQueue(int limit, Action<Exception>? onError)
{
    private readonly Lock _sync = new();
    private readonly Queue<Action> _queue = new();
    private bool _running;

    /// <summary>How many calls wait. The one that is running is not counted.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>True once the app has worked off at least half of what may wait.</summary>
    public bool HasCaughtUp => Count <= limit / 2;

    /// <summary>
    /// Whether the handler calls of one more message may be queued: they fit under the limit, or nothing is waiting (so
    /// a message with more handlers than the limit is not held back for good).
    /// </summary>
    public bool HasRoom(int calls)
    {
        lock (_sync)
        {
            return _queue.Count == 0 || _queue.Count + calls <= limit;
        }
    }

    public void Enqueue(Action callback)
    {
        lock (_sync)
        {
            _queue.Enqueue(callback);
            if (_running)
            {
                return;
            }

            _running = true;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static queue => queue.Run(), this, preferLocal: false);
    }

    private void Run()
    {
        while (true)
        {
            Action callback;
            lock (_sync)
            {
                if (!_queue.TryDequeue(out callback!))
                {
                    _running = false;
                    return;
                }
            }

            try
            {
                callback();
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }
    }

    private void Report(Exception exception)
    {
        try
        {
            onError?.Invoke(exception);
        }
        catch
        {
            // A failing error handler has nowhere left to report to.
        }
    }
}
