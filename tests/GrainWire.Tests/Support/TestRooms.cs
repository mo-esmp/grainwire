using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace GrainWire.Tests.Support;

[GenerateSerializer]
public sealed class CounterState
{
    [Id(0)] public int Value { get; set; }
    [Id(1)] public List<string> Log { get; set; } = new();
}

public sealed record CreateOptions(bool Deny = false, int? Start = null);
public sealed record JoinOptions(bool Deny = false, string? Role = null);
public sealed record AddRequest(int Amount);
public sealed record WhisperRequest(string To, string Text);
public sealed record TargetRequest(string Player, string? Role = null);
public sealed record DelayRequest(int Ms);
public sealed record CounterView(int Value, string Viewer, List<string> Log);

/// <summary>A room with one handler for each behaviour the tests need.</summary>
public abstract class CounterRoomBase : RoomGrain<CounterState>
{
    /// <summary>Room ids for which OnRejoin refuses the player named "banned".</summary>
    public static readonly ConcurrentDictionary<string, bool> Banned = new();

    protected override Task<CreateResult> OnCreate(CreateContext context)
    {
        var options = context.Options.As<CreateOptions>();
        if (options?.Deny == true) return Task.FromResult(CreateResult.Deny("creation denied"));
        if (options?.Start is { } start) State.Value = start;
        return Task.FromResult(CreateResult.Allow());
    }

    protected override Task<JoinResult> OnJoin(JoinContext context)
    {
        var data = context.Data.As<JoinOptions>();
        if (data?.Deny == true) return Task.FromResult(JoinResult.Reject("not invited"));
        return Task.FromResult(JoinResult.Accept(context.IsCreator ? "owner" : data?.Role ?? "guest"));
    }

    protected override Task<JoinResult> OnRejoin(JoinContext context) =>
        Task.FromResult(Banned.ContainsKey(RoomId + "/" + context.Player.Value) ? JoinResult.Reject("banned") : JoinResult.Accept());

    protected override object? GetSnapshot(PlayerId viewer) => new CounterView(State.Value, viewer.Value, State.Log);

    protected override Task OnDeadline(string name)
    {
        State.Value += 100;
        Broadcast("bumped", new { value = State.Value, name });
        return Task.CompletedTask;
    }

    protected override Task OnPlayerLeft(PlayerId player, LeaveReason reason)
    {
        State.Log.Add($"left:{player}");
        Broadcast("departed", new { player = player.Value });
        return Task.CompletedTask;
    }

    [OnMessage("add")]
    public int Add(PlayerId player, AddRequest request)
    {
        State.Value += request.Amount;
        Broadcast("changed", new { value = State.Value, by = player.Value });
        return State.Value;
    }

    [OnMessage("add-async")]
    public async Task<int> AddAsync(PlayerId player, AddRequest request)
    {
        await Task.Yield();
        return Add(player, request);
    }

    [OnMessage("touch")]
    public Task Touch(PlayerId player)
    {
        State.Log.Add("touch:" + player.Value);
        return Task.CompletedTask;
    }

    [OnMessage("add-then-reject")]
    public void AddThenReject(PlayerId player, AddRequest request)
    {
        State.Value += request.Amount;
        Broadcast("changed", new { value = State.Value, by = player.Value });
        Reject("nope", "custom-code");
    }

    [OnMessage("add-then-throw")]
    public void AddThenThrow(PlayerId player, AddRequest request)
    {
        State.Value += request.Amount;
        Broadcast("changed", new { value = State.Value, by = player.Value });
        throw new InvalidOperationException("boom");
    }

    [OnMessage("whisper")]
    public void Whisper(PlayerId player, WhisperRequest request)
    {
        Broadcast("before", null);
        SendTo(new PlayerId(request.To), "whispered", new { from = player.Value, text = request.Text });
        Broadcast("after", null);
    }

    [OnMessage("kick", Roles = "owner")]
    public void KickPlayer(PlayerId player, TargetRequest request)
    {
        Broadcast("kicking", new { player = request.Player });
        Kick(new PlayerId(request.Player), "bye");
    }

    [OnMessage("promote", Roles = "owner")]
    public void Promote(PlayerId player, TargetRequest request) => SetRole(new PlayerId(request.Player), request.Role);

    [OnMessage("close", Roles = "owner")]
    public void Close(PlayerId player) => CloseRoom("done");

    [OnMessage("schedule")]
    public void ScheduleBump(PlayerId player, DelayRequest request) => Schedule("bump", TimeSpan.FromMilliseconds(request.Ms));

    [OnMessage("unschedule")]
    public void Unschedule(PlayerId player) => CancelSchedule("bump");

    /// <summary>Broadcasts many events from one command, to overflow a small outgoing queue.</summary>
    [OnMessage("burst")]
    public void Burst(PlayerId player, AddRequest request)
    {
        for (var i = 0; i < request.Amount; i++) Broadcast("noise", new { i, pad = new string('x', 200) });
    }

    [OnMessage("where")]
    public string Where() => RuntimeIdentity;

    [OnMessage("deactivate")]
    public void Deactivate() => DeactivateOnIdle();

    [OnMessage("slow")]
    public async Task Slow(PlayerId player, DelayRequest request) => await Task.Delay(request.Ms);

    [OnMessage("members")]
    public object MembersNow() => Members.Select(m => new { player = m.Player.Value, role = m.Role, presence = m.Presence.ToString() }).ToList();
}

[RoomType("counter")]
public sealed class CounterRoom : CounterRoomBase;

/// <summary>Saved to the shared test storage.</summary>
[RoomType("pcounter")]
public sealed class PersistentCounterRoom : CounterRoomBase;

/// <summary>In memory, RollbackMode.None.</summary>
[RoomType("trusting")]
public sealed class TrustingRoom : CounterRoomBase;

/// <summary>At most two members.</summary>
[RoomType("tiny")]
public sealed class TinyRoom : CounterRoomBase;

/// <summary>In memory with a very short grace period and lease timeout.</summary>
[RoomType("quick")]
public sealed class QuickRoom : CounterRoomBase;

/// <summary>Persistent with a very short grace period and lease timeout.</summary>
[RoomType("pquick")]
public sealed class PersistentQuickRoom : CounterRoomBase;

/// <summary>Records presence changes in its state through OnPresenceChanged.</summary>
[RoomType("watcher")]
public sealed class WatcherRoom : CounterRoomBase
{
    protected override Task OnPresenceChanged(PlayerId player, PresenceStatus status)
    {
        State.Log.Add($"{status}:{player}");
        return Task.CompletedTask;
    }
}

public static class TestSilo
{
    public const string Storage = "shared";

    public static void Configure(ISiloBuilder silo)
    {
        silo.Services.AddKeyedSingleton<IGrainStorage>(Storage, (services, _) => new SharedTestStorage(services.GetRequiredService<Serializer>()));
        silo.AddGrainWire(gw => gw
            .ConfigureRoom("pcounter", o => o.StorageProvider = Storage)
            .ConfigureRoom("trusting", o => o.Rollback = RollbackMode.None)
            .ConfigureRoom("tiny", o => o.MaxMembers = 2)
            .ConfigureRoom("quick", Quick)
            .ConfigureRoom("watcher", Quick)
            .ConfigureRoom("pquick", o =>
            {
                Quick(o);
                o.StorageProvider = Storage;
            }));
    }

    private static void Quick(RoomOptions options)
    {
        options.GracePeriod = TimeSpan.FromMilliseconds(500);
        options.LeaseTimeout = TimeSpan.FromMilliseconds(900);
        options.LeaseCheckInterval = TimeSpan.FromMilliseconds(100);
    }
}

/// <summary>Grain storage kept in a static dictionary, so it survives a silo being stopped. Writes can be made to fail per room.</summary>
public sealed class SharedTestStorage(Serializer serializer) : IGrainStorage
{
    private static readonly ConcurrentDictionary<string, (byte[] Data, string ETag)> Store = new();

    /// <summary>Room ids whose writes fail.</summary>
    public static readonly ConcurrentDictionary<string, bool> FailWrites = new();

    public static int WriteCount;

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (Store.TryGetValue(Key(stateName, grainId), out var stored))
        {
            grainState.State = serializer.Deserialize<T>(stored.Data);
            grainState.ETag = stored.ETag;
            grainState.RecordExists = true;
        }
        else
        {
            grainState.State = Activator.CreateInstance<T>();
            grainState.ETag = null;
            grainState.RecordExists = false;
        }

        return Task.CompletedTask;
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (FailWrites.ContainsKey(grainId.Key.ToString()!)) throw new IOException("Simulated storage failure.");
        var key = Key(stateName, grainId);
        if (Store.TryGetValue(key, out var stored) && stored.ETag != grainState.ETag) throw new InconsistentStateException("ETag mismatch.");
        var etag = Guid.NewGuid().ToString("N");
        Store[key] = (serializer.SerializeToArray(grainState.State), etag);
        grainState.ETag = etag;
        grainState.RecordExists = true;
        Interlocked.Increment(ref WriteCount);
        return Task.CompletedTask;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (FailWrites.ContainsKey(grainId.Key.ToString()!)) throw new IOException("Simulated storage failure.");
        Store.TryRemove(Key(stateName, grainId), out _);
        grainState.State = Activator.CreateInstance<T>();
        grainState.ETag = null;
        grainState.RecordExists = false;
        return Task.CompletedTask;
    }

    public static bool Contains(string roomType, string roomId) => Store.Keys.Any(k => k.EndsWith($"gw-room-{roomType}/{roomId}", StringComparison.Ordinal));

    private static string Key(string stateName, GrainId grainId) => $"{stateName}|{grainId}";
}
