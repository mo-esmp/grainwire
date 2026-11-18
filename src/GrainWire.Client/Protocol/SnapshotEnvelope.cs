using System.Text.Json;

namespace GrainWire.Client.Protocol;

/// <summary>A baseline: the room's state as this player may see it, and the stream position it corresponds to.</summary>
public sealed record SnapshotEnvelope : InboxEnvelope
{
    /// <summary>The room incarnation (<c>ri</c>).</summary>
    public required string Incarnation { get; init; }

    /// <summary>The stream epoch (<c>se</c>).</summary>
    public required string Epoch { get; init; }

    /// <summary>The <c>seq</c> this snapshot corresponds to (<c>at</c>). The next event after it has <c>seq = at + 1</c>.</summary>
    public required long At { get; init; }

    /// <summary>The view (<c>d</c>). <see cref="JsonValueKind.Undefined"/> when the room has none.</summary>
    public JsonElement Data { get; init; }

    public required IReadOnlyList<Member> Members { get; init; }

    /// <summary>This player's own membership.</summary>
    public required Member You { get; init; }
}
