namespace GrainWire;

/// <summary>
/// Thrown by <c>Reject</c> to end a command without changing anything.
/// </summary>
/// <param name="reason">Why the command was rejected. It is sent to the client.</param>
/// <param name="code">Optional machine-readable code for the client.</param>
public sealed class RoomRejectedException(string reason, string? code = null) : Exception(reason)
{
    /// <summary>
    /// The machine-readable code sent to the client, or null to use the default.
    /// </summary>
    public string? Code { get; } = code;
}
