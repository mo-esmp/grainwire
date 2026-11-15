using GrainWire.Protocol;
using GrainWire.Tests.Support;

namespace GrainWire.Tests;

/// <summary>Persistent rooms: commit-then-publish, durable de-duplication, deadlines, and recovery after re-activation.</summary>
[Collection(ClusterCollection.Name)]
public class RoomPersistenceTests(ClusterFixture fixture)
{
    private FakeGateway NewGateway() => new(fixture.Cluster.Client);

    private static async Task<(RoomAddress Room, string Incarnation, string Epoch)> AliceAndBob(FakeGateway gateway, string type = "pcounter")
    {
        var room = Rooms.New(type);
        var created = await gateway.Join(room, "alice", "a1");
        await gateway.Join(room, "bob", "b1", create: false);
        return (room, created.Incarnation!, created.Snapshot!.Epoch);
    }

    /// <summary>Makes the room deactivate, so that the next call runs on a fresh activation loaded from storage.</summary>
    private static async Task Reactivate(FakeGateway gateway, RoomAddress room)
    {
        await gateway.Command(room, "alice", "a1", "deactivate");
        await Task.Delay(300);
    }

    // Failure test #1 of the design: a failed save after a state change.
    [Fact]
    public async Task When_saving_fails_nobody_sees_the_change_and_the_state_is_restored()
    {
        var gateway = NewGateway();
        var (room, _, _) = await AliceAndBob(gateway);
        Assert.Equal(5, FakeGateway.Read<int>((await gateway.Command(room, "alice", "a1", "add", new AddRequest(5))).Data));
        await gateway.WaitFor("b1", e => e.Count == 1);

        SharedTestStorage.FailWrites[room.Id] = true;
        try
        {
            var failed = await gateway.Command(room, "alice", "a1", "add", new AddRequest(7), commandId: "will-fail");
            Assert.Equal(ReplyStatus.Error, failed.Status);
            Assert.Equal(Wire.Codes.NotCommitted, failed.Code);
        }
        finally
        {
            SharedTestStorage.FailWrites.TryRemove(room.Id, out _);
        }

        await Task.Delay(200);
        Assert.Single(gateway.For("b1"));   // no "changed" for the failed command

        // Later commands work from the restored state, and the failed command id was not recorded as done.
        var retry = await gateway.Command(room, "alice", "a1", "add", new AddRequest(7), commandId: "will-fail");
        Assert.Equal(12, FakeGateway.Read<int>(retry.Data));
        var bob = await gateway.WaitFor("b1", e => e.Count == 2);
        Assert.Equal([1L, 2L], bob.Select(e => e.Seq));
        Assert.Equal(12, bob[1].Data.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task A_join_that_cannot_be_saved_does_not_add_the_member()
    {
        var gateway = NewGateway();
        var (room, _, _) = await AliceAndBob(gateway);
        SharedTestStorage.FailWrites[room.Id] = true;
        try
        {
            Assert.Equal(Wire.Codes.NotCommitted, (await gateway.Join(room, "carol", "c1", create: false)).Code);
        }
        finally
        {
            SharedTestStorage.FailWrites.TryRemove(room.Id, out _);
        }

        Assert.Equal(Wire.Codes.NotMember, (await gateway.Command(room, "carol", "c1", "add", new AddRequest(1))).Code);
        Assert.DoesNotContain(gateway.For("a1"), e => e.Type == Wire.Types.Presence && e.Data.GetProperty("player").GetString() == "carol");
        var members = await gateway.Command(room, "alice", "a1", "members");
        Assert.Equal(2, FakeGateway.ReadJson(members.Data).GetArrayLength());
    }

    // Failure test #2 of the design: the success reply is lost and the client retries.
    [Fact]
    public async Task A_retry_after_the_room_restarted_still_returns_the_recorded_outcome()
    {
        var gateway = NewGateway();
        var (room, incarnation, epoch) = await AliceAndBob(gateway);
        var first = await gateway.Command(room, "alice", "a1", "add", new AddRequest(4), commandId: "once", incarnation: incarnation);
        Assert.Equal(4, FakeGateway.Read<int>(first.Data));

        await Reactivate(gateway, room);

        var retry = await gateway.Command(room, "alice", "a1", "add", new AddRequest(4), commandId: "once", incarnation: incarnation);
        Assert.Equal(ReplyStatus.Ok, retry.Status);
        Assert.Equal(4, FakeGateway.Read<int>(retry.Data));

        var sync = await gateway.Sync(room, "alice", "a1", incarnation, epoch, 0);
        Assert.Equal(4, FakeGateway.Read<CounterView>(sync.Snapshot!.View).Value);
        Assert.Equal(incarnation, sync.Snapshot.Incarnation);     // same room...
        Assert.NotEqual(epoch, sync.Snapshot.Epoch);              // ...new stream epoch
    }

    [Fact]
    public async Task After_a_restart_a_lease_renewal_reattaches_connections_and_sends_snapshots()
    {
        var gateway = NewGateway();
        var (room, incarnation, epoch) = await AliceAndBob(gateway);
        await gateway.Command(room, "alice", "a1", "add", new AddRequest(9));
        await Reactivate(gateway, room);

        var renewed = await gateway.Renew(room, ("a1", "alice"), ("b1", "bob"));
        Assert.True(renewed.RoomExists);
        Assert.Equal(2, renewed.Snapshots.Count);
        Assert.All(renewed.Snapshots, s =>
        {
            Assert.Equal(incarnation, s.Snapshot.Incarnation);
            Assert.NotEqual(epoch, s.Snapshot.Epoch);
            Assert.Equal(9, FakeGateway.Read<CounterView>(s.Snapshot.View).Value);
            Assert.All(s.Snapshot.Members, m => Assert.NotEqual(PresenceStatus.Left, m.Status));
        });

        // Events flow again, in the new epoch, from a fresh sequence.
        var before = gateway.For("b1").Count;
        await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        var bob = await gateway.WaitFor("b1", e => e.Count > before && e.Last().Type == "changed");
        Assert.Equal(renewed.Snapshots[0].Snapshot.Epoch, bob.Last().Epoch);
    }

    [Fact]
    public async Task After_a_restart_nobody_is_removed_before_a_fresh_grace_period_has_passed()
    {
        var gateway = NewGateway();
        var (room, _, _) = await AliceAndBob(gateway, "pquick");
        await Reactivate(gateway, room);

        // The room is active again but knows no connections: everyone is "reconnecting".
        var renewed = await gateway.Renew(room, ("a1", "alice"));
        Assert.Equal(PresenceStatus.Online, renewed.Snapshots[0].Snapshot.Members.Single(m => m.Player == "alice").Status);
        Assert.Equal(PresenceStatus.Reconnecting, renewed.Snapshots[0].Snapshot.Members.Single(m => m.Player == "bob").Status);

        // Bob's gateway never reattaches him, so after the grace period he is removed; alice stays.
        await gateway.WaitFor("a1", e => e.Any(x => x.Type == Wire.Types.Presence && x.Data.GetProperty("status").GetString() == "left"));
        for (var i = 0; i < 5; i++)
        {
            await gateway.Renew(room, ("a1", "alice"));
            await Task.Delay(100);
        }

        var members = FakeGateway.ReadJson((await gateway.Command(room, "alice", "a1", "members")).Data);
        Assert.Equal("alice", Assert.Single(members.EnumerateArray()).GetProperty("player").GetString());
    }

    [Fact]
    public async Task Deadlines_survive_a_restart()
    {
        var gateway = NewGateway();
        var (room, _, _) = await AliceAndBob(gateway);
        await gateway.Command(room, "alice", "a1", "schedule", new DelayRequest(1500));
        await Reactivate(gateway, room);
        await gateway.Renew(room, ("a1", "alice"), ("b1", "bob"));   // activates the room again; the deadline is re-armed

        var bob = await gateway.WaitFor("b1", e => e.Any(x => x.Type == "bumped"), timeoutMs: 8000);
        Assert.Equal(100, bob.Last(e => e.Type == "bumped").Data.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task An_in_memory_room_is_lost_when_it_restarts_and_a_persistent_one_is_deleted_when_closed()
    {
        var gateway = NewGateway();
        var memory = Rooms.New("counter");
        await gateway.Join(memory, "alice", "a1");
        await Reactivate(gateway, memory);
        Assert.False((await gateway.Renew(memory, ("a1", "alice"))).RoomExists);

        var (room, _, _) = await AliceAndBob(gateway);
        Assert.True(SharedTestStorage.Contains("pcounter", room.Id));
        await gateway.Command(room, "alice", "a1", "close");
        Assert.False(SharedTestStorage.Contains("pcounter", room.Id));
        Assert.Equal(Wire.Codes.RoomNotFound, (await gateway.Join(room, "alice", "a1", create: false)).Code);
    }

    [Fact]
    public async Task Rejected_commands_do_not_write_to_storage()
    {
        var gateway = NewGateway();
        var (room, _, _) = await AliceAndBob(gateway);
        SharedTestStorage.FailWrites[room.Id] = true;   // any write would now fail the command
        try
        {
            var reply = await gateway.Command(room, "alice", "a1", "add-then-reject", new AddRequest(1));
            Assert.Equal("custom-code", reply.Code);
        }
        finally
        {
            SharedTestStorage.FailWrites.TryRemove(room.Id, out _);
        }
    }
}
