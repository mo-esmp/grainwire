using System.Text.Json;

namespace GrainWire.Client.Protocol;

/// <summary>The outcome of a request. Replies are not part of the event stream.</summary>
public sealed record ReplyEnvelope : InboxEnvelope
{
    /// <summary>The command id of the request (<c>cmd</c>).</summary>
    public string? CommandId { get; init; }

    public required ReplyStatus Status { get; init; }

    /// <summary>Machine-readable detail. See the table in docs/protocol.md, section 4.3.</summary>
    public string? Code { get; init; }

    /// <summary>Human-readable text, when there is one.</summary>
    public string? Reason { get; init; }

    /// <summary>The room incarnation (<c>ri</c>), when the room answered.</summary>
    public string? Incarnation { get; init; }

    /// <summary>
    /// The stream epoch (<c>se</c>) of the snapshot that was sent just before this reply. Null when none was sent.
    /// With <see cref="At"/> it tells which snapshot the reply speaks of, since that snapshot can be lost on the way.
    /// </summary>
    public string? Epoch { get; init; }

    /// <summary>The position (<c>at</c>) of the snapshot that was sent just before this reply. Null when none was sent.</summary>
    public long? At { get; init; }

    /// <summary>The handler's return value (<c>d</c>). <see cref="JsonValueKind.Undefined"/> when there is none.</summary>
    public JsonElement Data { get; init; }
}
