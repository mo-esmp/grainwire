namespace GrainWire.Client;

/// <summary>The room refused the request. Nothing changed.</summary>
public sealed class RejectedException(string code, string? reason) : GrainWireClientException(reason ?? code)
{
    /// <summary>Machine-readable detail: <c>rejected</c>, or a code the app or the framework defines (see docs/protocol.md).</summary>
    public string Code { get; } = code;

    /// <summary>Human-readable text, when the server sent one.</summary>
    public string? Reason { get; } = reason;
}
