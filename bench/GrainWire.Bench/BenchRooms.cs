namespace GrainWire.Bench;

[GenerateSerializer]
public sealed class BenchState
{
    /// <summary>Padding that makes the snapshot (and the saved state) as large as the workload asks for.</summary>
    [Id(0)] public string Blob { get; set; } = "";
    [Id(1)] public long Events { get; set; }
}

public sealed record BenchCreateOptions(int SnapshotBytes);

/// <summary><c>Ts</c> is the sender's clock when it published; receivers use it to measure delivery latency.</summary>
public sealed record BenchMessage(long Ts, string? Pad);

/// <summary>The room both workloads use: a command changes the state and broadcasts one event to every member.</summary>
[RoomType("bench")]
public sealed class BenchRoom : RoomGrain<BenchState>
{
    protected override Task<CreateResult> OnCreate(CreateContext context)
    {
        var bytes = Math.Clamp(context.Options.As<BenchCreateOptions>()?.SnapshotBytes ?? 0, 0, 1_000_000);
        State.Blob = new string('s', bytes);
        return Task.FromResult(CreateResult.Allow());
    }

    [OnMessage("say")]
    public void Say(PlayerId player, BenchMessage message)
    {
        State.Events++;
        Broadcast("said", message);
    }
}
