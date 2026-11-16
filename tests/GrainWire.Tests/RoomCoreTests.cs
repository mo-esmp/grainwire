using GrainWire.Internal;
using GrainWire.Protocol;
using GrainWire.Tests.Support;
using Orleans.Concurrency;

namespace GrainWire.Tests;

/// <summary>The room pipeline, tested through the grain interface with a fake gateway (no MQTT involved).</summary>
[Collection(ClusterCollection.Name)]
public class RoomCoreTests(ClusterFixture fixture)
{
    private FakeGateway NewGateway() => new(fixture.Cluster.Client);

    /// <summary>A room with alice (owner, connection "a1") and bob (guest, connection "b1").</summary>
    private async Task<(FakeGateway Gateway, RoomAddress Room, string Incarnation)> TwoPlayers(string type = "counter")
    {
        FakeGateway gateway = NewGateway();
        RoomAddress room = Rooms.New(type);
        RoomReply created = await gateway.Join(room, "alice", "a1");
        Assert.Equal(ReplyStatus.Ok, created.Status);
        RoomReply joined = await gateway.Join(room, "bob", "b1", create: false);
        Assert.Equal(ReplyStatus.Ok, joined.Status);
        return (gateway, room, created.Incarnation!);
    }

    [Fact]
    public async Task Joining_a_room_that_was_never_created_is_not_found()
    {
        RoomReply reply = await NewGateway().Join(Rooms.New("counter"), "alice", "a1", create: false);
        Assert.Equal(ReplyStatus.Error, reply.Status);
        Assert.Equal(Wire.Codes.RoomNotFound, reply.Code);
    }

    [Fact]
    public async Task Creating_and_joining_gives_each_player_a_snapshot_and_tells_the_others()
    {
        FakeGateway gateway = NewGateway();
        RoomAddress room = Rooms.New("counter");

        RoomReply created = await gateway.Join(room, "alice", "a1", options: new CreateOptions(Start: 7));
        Assert.Equal(Wire.Codes.Created, created.Code);
        Assert.NotNull(created.Incarnation);
        Assert.Equal(0, created.Snapshot!.At);
        Assert.Equal("owner", created.Snapshot.You.Role);
        Assert.Equal(7, FakeGateway.Read<CounterView>(created.Snapshot.View).Value);
        Assert.Equal("alice", FakeGateway.Read<CounterView>(created.Snapshot.View).Viewer);

        RoomReply joined = await gateway.Join(room, "bob", "b1", create: false, data: new JoinOptions(Role: "spectator"));
        Assert.Equal(Wire.Codes.Joined, joined.Code);
        Assert.Equal(created.Incarnation, joined.Incarnation);
        Assert.Equal(2, joined.Snapshot!.Members.Count);
        Assert.Equal("spectator", joined.Snapshot.You.Role);
        Assert.Equal(created.Snapshot.Epoch, joined.Snapshot.Epoch);

        List<Delivered> seen = await gateway.WaitFor("a1", e => e.Count == 1);
        Assert.Equal(Wire.Types.Presence, seen[0].Type);
        Assert.Equal(1, seen[0].Seq);
        Assert.Equal("bob", seen[0].Data.GetProperty("player").GetString());
        Assert.Equal("online", seen[0].Data.GetProperty("status").GetString());
        Assert.Equal("spectator", seen[0].Data.GetProperty("role").GetString());
        Assert.Empty(gateway.For("b1"));
    }

    [Fact]
    public async Task A_denied_creation_or_a_rejected_first_join_leaves_no_room_behind()
    {
        FakeGateway gateway = NewGateway();
        RoomAddress room = Rooms.New("counter");

        RoomReply denied = await gateway.Join(room, "alice", "a1", options: new CreateOptions(Deny: true));
        Assert.Equal(ReplyStatus.Rejected, denied.Status);
        Assert.Equal(Wire.Codes.CreateDenied, denied.Code);
        Assert.Equal("creation denied", denied.Reason);

        RoomReply rejected = await gateway.Join(room, "alice", "a1", options: new CreateOptions(Start: 5), data: new JoinOptions(Deny: true));
        Assert.Equal(Wire.Codes.JoinRejected, rejected.Code);
        Assert.Equal("not invited", rejected.Reason);

        Assert.Equal(Wire.Codes.RoomNotFound, (await gateway.Join(room, "alice", "a1", create: false)).Code);

        // And a later creation starts from clean state, not from what the rejected attempt set.
        RoomReply created = await gateway.Join(room, "alice", "a1");
        Assert.Equal(0, FakeGateway.Read<CounterView>(created.Snapshot!.View).Value);
    }

    [Fact]
    public async Task A_full_room_rejects_new_members_but_lets_existing_ones_back_in()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers("tiny");
        RoomReply third = await gateway.Join(room, "carol", "c1", create: false);
        Assert.Equal(Wire.Codes.RoomFull, third.Code);
        Assert.Equal(ReplyStatus.Ok, (await gateway.Join(room, "bob", "b2", create: false)).Status);
    }

    [Fact]
    public async Task A_command_replies_with_its_result_and_broadcasts_in_each_players_own_sequence()
    {
        (FakeGateway? gateway, RoomAddress room, string? incarnation) = await TwoPlayers();

        RoomReply reply = await gateway.Command(room, "alice", "a1", "add", new AddRequest(3), incarnation: incarnation);
        Assert.Equal(ReplyStatus.Ok, reply.Status);
        Assert.Equal(3, FakeGateway.Read<int>(reply.Data));
        Assert.Null(reply.Snapshot);

        List<Delivered> alice = await gateway.WaitFor("a1", e => e.Count == 2);
        Assert.Equal(("changed", 2L), (alice[1].Type, alice[1].Seq));   // seq 1 was bob's arrival
        List<Delivered> bob = await gateway.WaitFor("b1", e => e.Count == 1);
        Assert.Equal(("changed", 1L), (bob[0].Type, bob[0].Seq));
        Assert.Equal(3, bob[0].Data.GetProperty("value").GetInt32());

        RoomReply asyncReply = await gateway.Command(room, "bob", "b1", "add-async", new AddRequest(2));
        Assert.Equal(5, FakeGateway.Read<int>(asyncReply.Data));

        // A handler that returns a plain Task still gets an explicit "ok".
        RoomReply touch = await gateway.Command(room, "bob", "b1", "touch", commandId: "t1");
        Assert.Equal(ReplyStatus.Ok, touch.Status);
        Assert.Null(touch.Data);
    }

    [Fact]
    public async Task A_rejected_command_changes_nothing_and_sends_nothing()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));

        RoomReply reply = await gateway.Command(room, "alice", "a1", "add-then-reject", new AddRequest(50), commandId: "r1");
        Assert.Equal(ReplyStatus.Rejected, reply.Status);
        Assert.Equal("custom-code", reply.Code);
        Assert.Equal("nope", reply.Reason);

        RoomReply after = await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        Assert.Equal(2, FakeGateway.Read<int>(after.Data));
        List<Delivered> bob = await gateway.WaitFor("b1", e => e.Count == 2);
        Assert.Equal([1L, 2L], bob.Select(e => e.Seq));
        Assert.All(bob, e => Assert.NotEqual(51, e.Data.GetProperty("value").GetInt32()));
    }

    [Fact]
    public async Task A_handler_that_throws_is_rolled_back_and_reported_as_an_error()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        RoomReply reply = await gateway.Command(room, "alice", "a1", "add-then-throw", new AddRequest(50));
        Assert.Equal(ReplyStatus.Error, reply.Status);
        Assert.Equal(Wire.Codes.HandlerError, reply.Code);
        Assert.DoesNotContain("boom", reply.Reason);

        RoomReply after = await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        Assert.Equal(1, FakeGateway.Read<int>(after.Data));
        List<Delivered> bob = await gateway.WaitFor("b1", e => e.Count == 1);
        Assert.Equal(1, bob[0].Seq);
    }

    [Fact]
    public async Task A_bad_payload_is_rejected_before_the_handler_runs()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        RoomReply reply = await gateway.Command(room, "alice", "a1", "add", "not an object");
        Assert.Equal(ReplyStatus.Rejected, reply.Status);
        Assert.Equal(Wire.Codes.BadPayload, reply.Code);
    }

    [Fact]
    public async Task A_retried_command_runs_once_and_returns_the_recorded_outcome()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        RoomReply first = await gateway.Command(room, "alice", "a1", "add", new AddRequest(4), commandId: "cmd-1");
        RoomReply retry = await gateway.Command(room, "alice", "a1", "add", new AddRequest(4), commandId: "cmd-1");
        Assert.Equal(ReplyStatus.Ok, retry.Status);
        Assert.Equal(4, FakeGateway.Read<int>(first.Data));
        Assert.Equal(4, FakeGateway.Read<int>(retry.Data));

        // The same id from another player is a different command.
        RoomReply other = await gateway.Command(room, "bob", "b1", "add", new AddRequest(1), commandId: "cmd-1");
        Assert.Equal(5, FakeGateway.Read<int>(other.Data));
        List<Delivered> bob = await gateway.WaitFor("b1", e => e.Count == 2);
        Assert.Equal(2, bob.Count);
    }

    [Fact]
    public async Task Commands_are_validated_by_the_room()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        Assert.Equal(Wire.Codes.StaleRoom, (await gateway.Command(room, "alice", "a1", "add", new AddRequest(1), incarnation: "an-older-room")).Code);
        Assert.Equal(Wire.Codes.NotMember, (await gateway.Command(room, "mallory", "m1", "add", new AddRequest(1))).Code);
        Assert.Equal(Wire.Codes.UnknownCommand, (await gateway.Command(room, "alice", "a1", "no-such-command")).Code);
        RoomReply forbidden = await gateway.Command(room, "bob", "b1", "kick", new TargetRequest("alice"));
        Assert.Equal(ReplyStatus.Rejected, forbidden.Status);
        Assert.Equal(Wire.Codes.Forbidden, forbidden.Code);
        Assert.Equal(Wire.Codes.RoomNotFound, (await gateway.Command(Rooms.New("counter"), "alice", "a1", "add", new AddRequest(1))).Code);
    }

    [Fact]
    public async Task Private_and_public_events_share_one_gapless_sequence_per_player()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        await gateway.Command(room, "alice", "a1", "whisper", new WhisperRequest("bob", "psst"));

        List<Delivered> bob = await gateway.WaitFor("b1", e => e.Count == 3);
        Assert.Equal(["before", "whispered", "after"], bob.Select(e => e.Type));
        Assert.Equal([1L, 2L, 3L], bob.Select(e => e.Seq));

        List<Delivered> alice = await gateway.WaitFor("a1", e => e.Count == 3);
        Assert.Equal([Wire.Types.Presence, "before", "after"], alice.Select(e => e.Type));
        Assert.Equal([1L, 2L, 3L], alice.Select(e => e.Seq));
    }

    [Fact]
    public async Task A_snapshot_is_a_baseline_and_does_not_use_up_a_sequence_number()
    {
        (FakeGateway? gateway, RoomAddress room, string? incarnation) = await TwoPlayers();
        await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        List<Delivered> bob = await gateway.WaitFor("b1", e => e.Count == 1);
        string epoch = bob[0].Epoch;

        RoomReply inSync = await gateway.Sync(room, "bob", "b1", incarnation, epoch, seq: 1);
        Assert.Equal(Wire.Codes.InSync, inSync.Code);
        Assert.Null(inSync.Snapshot);

        RoomReply behind = await gateway.Sync(room, "bob", "b1", incarnation, epoch, seq: 0);
        Assert.Equal(Wire.Codes.Resynced, behind.Code);
        Assert.Equal(1, behind.Snapshot!.At);
        Assert.Equal(1, FakeGateway.Read<CounterView>(behind.Snapshot.View).Value);

        RoomReply wrongEpoch = await gateway.Sync(room, "bob", "b1", incarnation, "another-epoch", seq: 1);
        Assert.Equal(Wire.Codes.Resynced, wrongEpoch.Code);

        await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        bob = await gateway.WaitFor("b1", e => e.Count == 2);
        Assert.Equal(2, bob[1].Seq);
    }

    [Fact]
    public async Task Two_tabs_of_one_player_get_the_same_sequence_and_one_closing_changes_nothing()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        RoomReply second = await gateway.Join(room, "bob", "b2", create: false);
        Assert.Equal(0, second.Snapshot!.At);

        await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        List<Delivered> tab1 = await gateway.WaitFor("b1", e => e.Count == 1);
        List<Delivered> tab2 = await gateway.WaitFor("b2", e => e.Count == 1);
        Assert.Equal(tab1[0].Seq, tab2[0].Seq);

        await gateway.Detach(room, "b1");
        await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        List<Delivered> alice = await gateway.WaitFor("a1", e => e.Count(x => x.Type == "changed") == 2);
        Assert.DoesNotContain(alice, e => e.Type == Wire.Types.Presence && e.Data.GetProperty("status").GetString() == "reconnecting");
        Assert.Single(gateway.For("b1"));
        Assert.Equal(2, gateway.For("b2").Count);
    }

    [Fact]
    public async Task A_late_disconnect_of_an_old_connection_does_not_remove_the_new_one()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        await gateway.Join(room, "bob", "b1", create: false, generation: 2);   // the tab reconnected
        await gateway.Detach(room, "b1", generation: 1);                        // the old connection's disconnect arrives late

        await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        await gateway.WaitFor("b1", e => e.Count == 1);
        Assert.DoesNotContain(gateway.For("a1"), e => e.Type == Wire.Types.Presence && e.Data.GetProperty("status").GetString() == "reconnecting");
    }

    [Fact]
    public async Task A_dropped_player_shows_as_reconnecting_then_comes_back_online()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        await gateway.Detach(room, "b1");
        List<Delivered> alice = await gateway.WaitFor("a1", e => e.Count == 2);
        Assert.Equal("reconnecting", alice[1].Data.GetProperty("status").GetString());
        Assert.Equal("guest", alice[1].Data.GetProperty("role").GetString());

        RoomReply back = await gateway.Join(room, "bob", "b1", create: false, generation: 2);
        Assert.Equal(Wire.Codes.Joined, back.Code);
        Assert.Equal("guest", back.Snapshot!.You.Role);
        alice = await gateway.WaitFor("a1", e => e.Count == 3);
        Assert.Equal("online", alice[2].Data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task When_the_grace_period_runs_out_the_player_is_removed()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers("quick");
        await gateway.Detach(room, "b1");

        List<Delivered> alice = await gateway.WaitFor("a1", e => e.Any(x => x.Type == Wire.Types.Presence && x.Data.GetProperty("status").GetString() == "left"));
        Assert.Equal([Wire.Types.Presence, Wire.Types.Presence, "departed", Wire.Types.Presence], alice.Select(e => e.Type));
        Assert.Equal([1L, 2L, 3L, 4L], alice.Select(e => e.Seq));

        Assert.Equal(Wire.Codes.NotMember, (await gateway.Command(room, "bob", "b1", "add", new AddRequest(1))).Code);
        RoomReply members = await gateway.Command(room, "alice", "a1", "members");
        Assert.Equal(1, FakeGateway.ReadJson(members.Data).GetArrayLength());

        // Coming back after that is a new join.
        RoomReply again = await gateway.Join(room, "bob", "b1", create: false, generation: 3);
        Assert.Equal(Wire.Codes.Joined, again.Code);
        Assert.Contains("left:bob", FakeGateway.Read<CounterView>(again.Snapshot!.View).Log);
    }

    [Fact]
    public async Task An_in_memory_room_ceases_to_exist_when_its_last_member_is_gone()
    {
        FakeGateway gateway = NewGateway();
        RoomAddress room = Rooms.New("quick");
        await gateway.Join(room, "alice", "a1");
        await gateway.Detach(room, "a1");
        await Task.Delay(1200);
        Assert.Equal(Wire.Codes.RoomNotFound, (await gateway.Join(room, "alice", "a1", create: false, generation: 2)).Code);
    }

    [Fact]
    public async Task Kicking_tells_the_player_why_and_tells_the_others_they_left()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        RoomReply reply = await gateway.Command(room, "alice", "a1", "kick", new TargetRequest("bob"));
        Assert.Equal(ReplyStatus.Ok, reply.Status);

        List<Delivered> bob = await gateway.WaitFor("b1", e => e.Count == 2);
        Assert.Equal("kicking", bob[0].Type);                 // events from the same command still reach the kicked player
        Assert.Equal(Wire.Types.Closed, bob[1].Type);
        Assert.False(bob[1].Sequenced);
        Assert.Equal("kicked", bob[1].Data.GetProperty("reason").GetString());
        Assert.Equal("bye", bob[1].Data.GetProperty("message").GetString());

        List<Delivered> alice = await gateway.WaitFor("a1", e => e.Count == 3);
        Assert.Equal("left", alice[2].Data.GetProperty("status").GetString());
        Assert.Equal(Wire.Codes.NotMember, (await gateway.Command(room, "bob", "b1", "add", new AddRequest(1))).Code);
    }

    [Fact]
    public async Task Closing_a_room_tells_everyone_and_deletes_it()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        await gateway.Command(room, "alice", "a1", "close");

        foreach (string? client in new[] { "a1", "b1" })
        {
            List<Delivered> events = await gateway.WaitFor(client, e => e.Any(x => x.Type == Wire.Types.Closed));
            Delivered closed = events.Last();
            Assert.Equal("closed", closed.Data.GetProperty("reason").GetString());
            Assert.Equal("done", closed.Data.GetProperty("message").GetString());
        }

        Assert.Equal(Wire.Codes.RoomNotFound, (await gateway.Join(room, "alice", "a1", create: false)).Code);
    }

    [Fact]
    public async Task Changing_a_role_is_announced_to_everyone_including_the_player()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        await gateway.Command(room, "alice", "a1", "promote", new TargetRequest("bob", "moderator"));
        List<Delivered> bob = await gateway.WaitFor("b1", e => e.Count == 1);
        Assert.Equal(Wire.Types.Presence, bob[0].Type);
        Assert.Equal("moderator", bob[0].Data.GetProperty("role").GetString());
        Assert.Equal("online", bob[0].Data.GetProperty("status").GetString());
        await gateway.WaitFor("a1", e => e.Count == 2);
    }

    [Fact]
    public async Task A_scheduled_deadline_runs_through_the_pipeline_and_can_be_cancelled()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        await gateway.Command(room, "alice", "a1", "schedule", new DelayRequest(200));
        List<Delivered> bob = await gateway.WaitFor("b1", e => e.Any(x => x.Type == "bumped"));
        Assert.Equal(100, bob.Last().Data.GetProperty("value").GetInt32());
        Assert.Equal("bump", bob.Last().Data.GetProperty("name").GetString());

        await gateway.Command(room, "alice", "a1", "schedule", new DelayRequest(300));
        await gateway.Command(room, "alice", "a1", "unschedule");
        await Task.Delay(700);
        Assert.Single(gateway.For("b1"), e => e.Type == "bumped");
    }

    [Fact]
    public async Task A_gateway_that_stops_renewing_is_treated_as_dead()
    {
        FakeGateway silent = NewGateway();
        FakeGateway alive = NewGateway();
        RoomAddress room = Rooms.New("quick");
        await silent.Join(room, "alice", "a1");
        await alive.Join(room, "bob", "b1", create: false);

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!alive.For("b1").Any(e => e.Type == Wire.Types.Presence && e.Data.GetProperty("status").GetString() == "reconnecting"))
        {
            Assert.True(DateTime.UtcNow < deadline, "bob was never told that alice is reconnecting");
            await alive.Renew(room, ("b1", "bob"));
            await Task.Delay(100);
        }

        Assert.Equal("alice", alive.For("b1").First(e => e.Type == Wire.Types.Presence).Data.GetProperty("player").GetString());
    }

    [Fact]
    public async Task A_lease_renewal_reports_missing_rooms_and_non_members()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        RenewReply renewed = await gateway.Renew(room, ("a1", "alice"), ("x1", "mallory"));
        Assert.True(renewed.RoomExists);
        Assert.Equal("x1", Assert.Single(renewed.NotMembers).ClientId);
        Assert.Empty(renewed.Snapshots);   // a1 was attached already

        Assert.False((await gateway.Renew(Rooms.New("counter"), ("a1", "alice"))).RoomExists);
    }

    [Fact]
    public async Task OnRejoin_can_refuse_a_member_who_lost_access()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers();
        CounterRoomBase.Banned[room.Id + "/bob"] = true;
        RoomReply reply = await gateway.Join(room, "bob", "b1", create: false, generation: 2);
        Assert.Equal(ReplyStatus.Rejected, reply.Status);
        Assert.Equal(Wire.Codes.JoinRejected, reply.Code);
        Assert.Equal("banned", reply.Reason);
    }

    [Fact]
    public async Task OnPresenceChanged_runs_inside_the_pipeline()
    {
        (FakeGateway? gateway, RoomAddress room, string _) = await TwoPlayers("watcher");
        await gateway.Detach(room, "b1");
        await gateway.WaitFor("a1", e => e.Count == 2);
        RoomReply snapshot = await gateway.Sync(room, "alice", "a1", null, null, 0);
        Assert.Contains("Reconnecting:bob", FakeGateway.Read<CounterView>(snapshot.Snapshot!.View).Log);
    }

    // Gateways rank a room's epochs by order. Clocks differ between silos, so a new epoch could start out ranked
    // below one a gateway has already seen; the gateway would then drop everything the room sends.
    [Fact]
    public async Task A_room_ranks_its_epoch_above_any_other_epoch_a_gateway_has_seen()
    {
        FakeGateway gateway = NewGateway();
        long ahead = DateTime.UtcNow.AddYears(5).Ticks;   // as if the previous silo's clock had been far ahead
        gateway.Known = new KnownEpoch("an-earlier-epoch", ahead);

        RoomAddress room = Rooms.New("counter");
        await gateway.Join(room, "alice", "a1");
        await gateway.Join(room, "bob", "b1", create: false);
        await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        await gateway.WaitFor("b1", e => e.Any(x => x.Type == "changed"));
        Assert.True(gateway.HighestEpochOrder > ahead);
    }

    [Fact]
    public async Task A_room_keeps_its_epoch_order_when_the_gateway_reports_the_same_epoch_or_an_older_one()
    {
        FakeGateway gateway = NewGateway();
        RoomAddress room = Rooms.New("counter");
        RoomReply created = await gateway.Join(room, "alice", "a1");
        await gateway.Join(room, "bob", "b1", create: false);
        await gateway.WaitFor("a1", e => e.Count == 1);
        long order = gateway.HighestEpochOrder;
        Assert.InRange(order, DateTime.UtcNow.AddMinutes(-5).Ticks, DateTime.UtcNow.AddMinutes(5).Ticks);

        gateway.Known = new KnownEpoch(created.Snapshot!.Epoch, order);
        await gateway.Renew(room, ("a1", "alice"), ("b1", "bob"));
        gateway.Known = new KnownEpoch("an-earlier-epoch", order - 1);
        await gateway.Renew(room, ("a1", "alice"), ("b1", "bob"));

        await gateway.Command(room, "alice", "a1", "add", new AddRequest(1));
        await gateway.WaitFor("b1", e => e.Any(x => x.Type == "changed"));
        Assert.Equal(order, gateway.HighestEpochOrder);
    }

    [Fact]
    public void A_room_grain_that_could_interleave_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => RoomTypeInfo.EnsureNotInterleaving(typeof(ReentrantRoom)));
        Assert.Throws<InvalidOperationException>(() => RoomTypeInfo.EnsureNotInterleaving(typeof(InterleavingRoom)));
        RoomTypeInfo.EnsureNotInterleaving(typeof(CounterRoom));
    }

    [Reentrant]
    private sealed class ReentrantRoom;

    private interface IExtra
    {
        [AlwaysInterleave]
        Task Peek();
    }

    private sealed class InterleavingRoom : IExtra
    {
        public Task Peek() => Task.CompletedTask;
    }
}
