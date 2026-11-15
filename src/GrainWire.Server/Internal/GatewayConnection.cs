using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GrainWire.Server.Internal;

/// <summary>One live MQTT connection (one browser tab) held by this gateway.</summary>
internal sealed class GatewayConnection
{
    private static long _lastGeneration;

    public GatewayConnection(string clientId, PlayerId player, IReadOnlyDictionary<string, string> claims, GrainWireGatewayOptions options)
    {
        ClientId = clientId;
        Player = player;
        Claims = claims;
        Generation = NextGeneration();
        Bucket = new TokenBucket(options.CommandsPerSecond, options.CommandBurst);
        Queue = Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(Math.Max(1, options.MaxPendingRequests))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public string ClientId { get; }
    public long Generation { get; }
    public PlayerId Player { get; }
    public IReadOnlyDictionary<string, string> Claims { get; }
    public ConnectionId Id => new(ClientId, Generation);

    /// <summary>Rooms ("type/id") this connection has joined through this gateway.</summary>
    public ConcurrentDictionary<string, RoomAddress> Rooms { get; } = new(StringComparer.Ordinal);

    public TokenBucket Bucket { get; }

    /// <summary>Requests waiting for a room, processed strictly in order by <see cref="Worker"/>.</summary>
    public Channel<InboundMessage> Queue { get; }

    public Task Worker { get; set; } = Task.CompletedTask;

    public volatile bool Closed;

    /// <summary>Set to 1 once the gateway has decided to disconnect this connection for falling behind.</summary>
    public int Evicting;

    /// <summary>Generations grow with time so that a newer connection of the same tab always wins, on any gateway.</summary>
    private static long NextGeneration()
    {
        while (true)
        {
            var last = Volatile.Read(ref _lastGeneration);
            var next = Math.Max(last + 1, DateTime.UtcNow.Ticks);
            if (Interlocked.CompareExchange(ref _lastGeneration, next, last) == last) return next;
        }
    }
}

internal readonly record struct InboundMessage(ClientTopic Topic, byte[] Payload);

/// <summary>Allows a burst, then a steady rate.</summary>
internal sealed class TokenBucket(double perSecond, int burst)
{
    private readonly object _gate = new();
    private double _tokens = burst;
    private long _last = Environment.TickCount64;

    public bool TryTake()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            _tokens = Math.Min(burst, _tokens + ((now - _last) / 1000.0 * perSecond));
            _last = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}

/// <summary>Counts events per key in a sliding one-minute window.</summary>
internal sealed class PerMinuteLimiter(int limit)
{
    private readonly ConcurrentDictionary<string, Queue<long>> _windows = new(StringComparer.Ordinal);

    public bool HasRoom(string key)
    {
        if (!_windows.TryGetValue(key, out var window)) return limit > 0;
        lock (window)
        {
            Prune(window);
            return window.Count < limit;
        }
    }

    public void Record(string key)
    {
        var window = _windows.GetOrAdd(key, static _ => new Queue<long>());
        lock (window)
        {
            Prune(window);
            window.Enqueue(Environment.TickCount64);
        }
    }

    private static void Prune(Queue<long> window)
    {
        var cutoff = Environment.TickCount64 - 60_000;
        while (window.Count > 0 && window.Peek() < cutoff) window.Dequeue();
    }
}
