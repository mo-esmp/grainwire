using GrainWire.Protocol;

namespace GrainWire.Storage;

/// <summary>Everything about a room that is saved when the room type has a storage provider.</summary>
[GenerateSerializer]
public sealed class RoomRecord<TState> where TState : class, new()
{
    [Id(0)] public bool Exists { get; set; }

    /// <summary>Room incarnation (<c>ri</c>): set when the room is created, stable for its lifetime.</summary>
    [Id(1)] public string? Incarnation { get; set; }

    [Id(2)] public TState State { get; set; } = new();

    /// <summary>Members by player id.</summary>
    [Id(3)] public Dictionary<string, MemberRecord> Members { get; set; } = [];

    /// <summary>Outcomes of recent commands by player id, for retry de-duplication.</summary>
    [Id(4)] public Dictionary<string, List<DedupEntry>> Dedup { get; set; } = [];

    /// <summary>Scheduled deadlines by name.</summary>
    [Id(5)] public Dictionary<string, DateTimeOffset> Deadlines { get; set; } = [];

    [Id(6)] public DateTimeOffset CreatedAt { get; set; }
}

[GenerateSerializer]
public sealed class MemberRecord
{
    [Id(0)] public string Player { get; set; } = "";
    [Id(1)] public string? Role { get; set; }
    [Id(2)] public DateTimeOffset JoinedAt { get; set; }
}

[GenerateSerializer]
public sealed class DedupEntry
{
    [Id(0)] public string CommandId { get; set; } = "";
    [Id(1)] public DateTimeOffset At { get; set; }
    [Id(2)] public RoomReply Reply { get; set; } = null!;
}
