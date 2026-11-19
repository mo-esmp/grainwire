namespace GrainWire;

/// <summary>
/// The decision of a room on a request to join it.
/// </summary>
public sealed class JoinResult
{
    private JoinResult(bool accepted, string? role, string? reason)
    {
        Accepted = accepted;
        Role = role;
        Reason = reason;
    }

    /// <summary>
    /// True when the player may join.
    /// </summary>
    public bool Accepted { get; }

    /// <summary>
    /// The role given to the player. Null when the player has no role or was rejected.
    /// </summary>
    public string? Role { get; }

    /// <summary>
    /// Why the join was rejected. Null when it was accepted.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Accepts the player.
    /// </summary>
    /// <param name="role">The role to give the player, if any.</param>
    /// <returns>A result that accepts the join.</returns>
    public static JoinResult Accept(string? role = null) => new(true, role, null);

    /// <summary>
    /// Rejects the player.
    /// </summary>
    /// <param name="reason">Why the join is rejected. It is sent to the client.</param>
    /// <returns>A result that rejects the join.</returns>
    public static JoinResult Reject(string reason) => new(false, null, reason);
}
