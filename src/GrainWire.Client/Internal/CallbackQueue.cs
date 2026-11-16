namespace GrainWire.Client.Internal;

/// <summary>
/// Runs the app's handlers one at a time, in the order they were queued, and never while the client's lock is held.
/// Handlers are queued under that lock, which fixes their order, and run after it is released, so a handler may call
/// back into the client, and a slow handler cannot deadlock against another thread that uses the client.
/// </summary>
internal sealed class CallbackQueue(Action<Exception>? onError)
{
    private readonly Lock _sync = new();
    private readonly Queue<Action> _queue = new();
    private bool _draining;

    public void Enqueue(Action callback)
    {
        lock (_sync)
        {
            _queue.Enqueue(callback);
        }
    }

    /// <summary>
    /// Runs what is queued, unless another thread (or a caller further up this thread's stack) is already doing so; that
    /// one then runs the new callbacks too.
    /// </summary>
    public void Drain()
    {
        lock (_sync)
        {
            if (_draining || _queue.Count == 0)
            {
                return;
            }

            _draining = true;
        }

        while (true)
        {
            Action callback;
            lock (_sync)
            {
                if (!_queue.TryDequeue(out callback!))
                {
                    _draining = false;
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
