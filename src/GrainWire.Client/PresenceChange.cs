namespace GrainWire.Client;

/// <summary>A member joined, dropped, came back, left, or changed role.</summary>
/// <param name="Player">The player this is about. It can be you, when your role changes.</param>
/// <param name="Status">The player's status after the change.</param>
/// <param name="Role">The player's role after the change, if any.</param>
public sealed record PresenceChange(string Player, MemberStatus Status, string? Role);
