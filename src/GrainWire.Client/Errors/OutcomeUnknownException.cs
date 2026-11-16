namespace GrainWire.Client;

/// <summary>
/// No reply arrived in time. The command may or may not have been applied: this is not a rejection.
/// The room's next snapshot or events will show what happened.
/// </summary>
public sealed class OutcomeUnknownException(string commandId)
    : GrainWireClientException("No reply arrived in time; the outcome of the request is unknown.")
{
    /// <summary>The command id the request was sent with.</summary>
    public string CommandId { get; } = commandId;
}
