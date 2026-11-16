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

    /// <summary>The handler's return value (<c>d</c>). <see cref="JsonValueKind.Undefined"/> when there is none.</summary>
    public JsonElement Data { get; init; }
}
