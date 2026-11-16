namespace GrainWire.Client;

/// <summary>The request could not be processed. Nothing changed.</summary>
public sealed class RoomException(string code, string? reason) : GrainWireClientException(reason ?? code)
{
    /// <summary>Machine-readable detail, such as <c>room-not-found</c> or <c>handler-error</c> (see docs/protocol.md).</summary>
    public string Code { get; } = code;

    /// <summary>Human-readable text, when the server sent one.</summary>
    public string? Reason { get; } = reason;
}
