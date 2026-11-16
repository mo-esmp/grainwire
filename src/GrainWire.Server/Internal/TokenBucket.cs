namespace GrainWire.Server.Internal;

/// <summary>Allows a burst, then a steady rate.</summary>
internal sealed class TokenBucket(double perSecond, int burst)
{
    private readonly Lock _gate = new();
    private double _tokens = burst;
    private long _last = Environment.TickCount64;

    public bool TryTake()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            _tokens = Math.Min(burst, _tokens + ((now - _last) / 1000.0 * perSecond));
            _last = now;
            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }
}
