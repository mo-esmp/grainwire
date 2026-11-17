namespace GrainWire.Client.Protocol;

/// <summary>Replace the local state with this snapshot.</summary>
public sealed record SnapshotAction(SnapshotEnvelope Snapshot) : OrderingAction;
