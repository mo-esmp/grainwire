namespace GrainWire.Client.Protocol;

/// <summary>
/// What an <see cref="OrderingEngine"/> tells its caller to do: a <see cref="SnapshotAction"/>, an
/// <see cref="EventAction"/> or a <see cref="SyncAction"/>.
/// </summary>
public abstract record OrderingAction
{
    private protected OrderingAction()
    {
    }
}
