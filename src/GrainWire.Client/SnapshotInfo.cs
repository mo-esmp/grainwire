namespace GrainWire.Client;

/// <summary>What a snapshot says besides the view: who is in the room, and this player's own membership.</summary>
public sealed record SnapshotInfo(IReadOnlyList<Member> Members, Member You);
