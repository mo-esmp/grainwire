namespace GrainWire.Storage;

/// <summary>
/// A saved member of a room.
/// </summary>
[GenerateSerializer]
public sealed class MemberRecord
{
    /// <summary>
    /// The member's player id.
    /// </summary>
    [Id(0)]
    public string Player { get; set; } = "";

    /// <summary>
    /// The member's role, or null when it has none.
    /// </summary>
    [Id(1)]
    public string? Role { get; set; }

    /// <summary>
    /// When the member joined the room.
    /// </summary>
    [Id(2)]
    public DateTimeOffset JoinedAt { get; set; }
}
