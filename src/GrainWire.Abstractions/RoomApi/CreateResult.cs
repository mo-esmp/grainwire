namespace GrainWire;

/// <summary>
/// The decision of a room on a request to create it.
/// </summary>
public sealed class CreateResult
{
    private CreateResult(bool allowed, string? reason)
    {
        Allowed = allowed;
        Reason = reason;
    }

    /// <summary>
    /// True when the room may be created.
    /// </summary>
    public bool Allowed { get; }

    /// <summary>
    /// Why creation was denied. Null when it was allowed.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Allows the room to be created.
    /// </summary>
    /// <returns>A result that allows creation.</returns>
    public static CreateResult Allow() => new(true, null);

    /// <summary>
    /// Denies creating the room.
    /// </summary>
    /// <param name="reason">Why creation is denied. It is sent to the client.</param>
    /// <returns>A result that denies creation.</returns>
    public static CreateResult Deny(string reason) => new(false, reason);
}
