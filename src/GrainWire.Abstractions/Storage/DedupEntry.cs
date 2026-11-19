using GrainWire.Protocol;

namespace GrainWire.Storage;

/// <summary>
/// A remembered command outcome, kept so a retry of the same command gets the same reply.
/// </summary>
[GenerateSerializer]
public sealed class DedupEntry
{
    /// <summary>
    /// The client-chosen id of the command.
    /// </summary>
    [Id(0)]
    public string CommandId { get; set; } = "";

    /// <summary>
    /// When the command was handled.
    /// </summary>
    [Id(1)]
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// The reply that was sent.
    /// </summary>
    [Id(2)]
    public RoomReply Reply { get; set; } = null!;
}
