namespace GrainWire.Client.Protocol;

/// <summary>Timing of an <see cref="OrderingEngine"/>.</summary>
public sealed record OrderingOptions
{
    /// <summary>How long to wait for a missing event before asking the server for a snapshot. Default 250 ms.</summary>
    public TimeSpan GapWait { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>A room that has been silent this long is checked against the server. Default 15 s.</summary>
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Minimum time between two sync requests while the client is still behind. Default 2 s.</summary>
    public TimeSpan SyncRetry { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Events held while waiting for a missing one. Default 2,000.</summary>
    public int MaxBuffered { get; init; } = 2_000;
}
