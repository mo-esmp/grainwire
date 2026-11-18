namespace GrainWire.Storage;

/// <summary>
/// Everything about a room that is saved when the room type has a storage provider.
/// </summary>
/// <typeparam name="TState">The room's state type.</typeparam>
[GenerateSerializer]
public sealed class RoomRecord<TState> where TState : class, new()
{
    /// <summary>
    /// True once the room has been created.
    /// </summary>
    [Id(0)]
    public bool Exists { get; set; }

    /// <summary>
    /// Room incarnation (<c>ri</c>): set when the room is created, stable for its lifetime.
    /// </summary>
    [Id(1)]
    public string? Incarnation { get; set; }

    /// <summary>
    /// The app-defined room state.
    /// </summary>
    [Id(2)]
    public TState State { get; set; } = new();

    /// <summary>
    /// Members by player id.
    /// </summary>
    [Id(3)]
    public Dictionary<string, MemberRecord> Members { get; set; } = [];

    /// <summary>
    /// Outcomes of recent commands by player id, for retry de-duplication.
    /// </summary>
    [Id(4)]
    public Dictionary<string, List<DedupEntry>> Dedup { get; set; } = [];

    /// <summary>
    /// Scheduled deadlines by name.
    /// </summary>
    [Id(5)]
    public Dictionary<string, DateTimeOffset> Deadlines { get; set; } = [];

    /// <summary>
    /// When the room was created.
    /// </summary>
    [Id(6)]
    public DateTimeOffset CreatedAt { get; set; }
}
