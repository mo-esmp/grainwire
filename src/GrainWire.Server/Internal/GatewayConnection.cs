using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GrainWire.Server.Internal;

/// <summary>One live MQTT connection (one browser tab) held by this gateway.</summary>
internal sealed class GatewayConnection(
    string clientId,
    PlayerId player,
    IReadOnlyDictionary<string, string> claims,
    GrainWireGatewayOptions options)
{
    private static long s_lastGeneration;

    public string ClientId { get; } = clientId;

    public long Generation { get; } = NextGeneration();

    public PlayerId Player { get; } = player;

    public IReadOnlyDictionary<string, string> Claims { get; } = claims;

    public ConnectionId Id => new(ClientId, Generation);

    /// <summary>Rooms ("type/id") this connection has joined through this gateway.</summary>
    public ConcurrentDictionary<string, RoomAddress> Rooms { get; } = new(StringComparer.Ordinal);

    public TokenBucket Bucket { get; } = new(options.CommandsPerSecond, options.CommandBurst);

    /// <summary>Requests waiting for a room, processed strictly in order by <see cref="Worker"/>.</summary>
    public Channel<InboundMessage> Queue { get; } = Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(Math.Max(1, options.MaxPendingRequests))
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    public Task Worker { get; set; } = Task.CompletedTask;

    public volatile bool Closed;

    /// <summary>Set to 1 once the gateway has decided to disconnect this connection for falling behind.</summary>
    public int Evicting;

    /// <summary>Generations grow with time so that a newer connection of the same tab always wins, on any gateway.</summary>
    private static long NextGeneration()
    {
        while (true)
        {
            long last = Volatile.Read(ref s_lastGeneration);
            long next = Math.Max(last + 1, DateTime.UtcNow.Ticks);
            if (Interlocked.CompareExchange(ref s_lastGeneration, next, last) == last)
            {
                return next;
            }
        }
    }
}
