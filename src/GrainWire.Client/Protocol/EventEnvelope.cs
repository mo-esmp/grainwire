using System.Text.Json;

namespace GrainWire.Client.Protocol;

/// <summary>A sequenced event: an app event, or <c>$presence</c>.</summary>
public sealed record EventEnvelope : InboxEnvelope
{
    /// <summary>The room incarnation (<c>ri</c>): stable for the room's lifetime.</summary>
    public required string Incarnation { get; init; }

    /// <summary>The stream epoch (<c>se</c>): changes whenever the room restarts on the server.</summary>
    public required string Epoch { get; init; }

    /// <summary>The position in this player's event stream for the room, within the epoch.</summary>
    public required long Seq { get; init; }

    /// <summary>The event payload (<c>d</c>). <see cref="JsonValueKind.Undefined"/> when the event has none.</summary>
    public JsonElement Data { get; init; }
}
