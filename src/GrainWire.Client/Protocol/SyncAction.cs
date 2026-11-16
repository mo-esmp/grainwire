namespace GrainWire.Client.Protocol;

/// <summary>Check this stream against the server: publish <c>sys/sync</c> with the engine's current position.</summary>
public sealed record SyncAction : OrderingAction
{
    private SyncAction()
    {
    }

    public static SyncAction Instance { get; } = new();
}
