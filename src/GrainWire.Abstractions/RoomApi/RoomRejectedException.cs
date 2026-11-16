namespace GrainWire;

/// <summary>Thrown by <c>Reject</c> to end a command without changing anything.</summary>
public sealed class RoomRejectedException(string reason, string? code = null) : Exception(reason)
{
    public string? Code { get; } = code;
}
