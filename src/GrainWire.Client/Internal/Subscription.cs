namespace GrainWire.Client.Internal;

/// <summary>Removes a handler when disposed.</summary>
internal sealed class Subscription(Action remove) : IDisposable
{
    private Action? _remove = remove;

    public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
}
