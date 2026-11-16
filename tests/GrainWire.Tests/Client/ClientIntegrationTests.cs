using System.Collections.Concurrent;
using GrainWire.Client;
using GrainWire.Tests.Support;
using JoinOptions = GrainWire.Client.JoinOptions;

namespace GrainWire.Tests.Client;

/// <summary>
/// The real .NET client against a real GrainWire server (a silo with its gateway, in this process), with messages
/// lost and connections cut on purpose. Mirrors clients/ts/test/integration.test.ts.
/// </summary>
public sealed class ClientIntegrationTests(ClientServerFixture server) : IClassFixture<ClientServerFixture>, IAsyncLifetime
{
    private readonly List<GrainWireClient> _clients = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (GrainWireClient client in _clients)
        {
            await client.DisposeAsync();
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task<GrainWireClient> Open(string player, Action<GrainWireClientOptions>? configure = null)
    {
        GrainWireClientOptions options = new()
        {
            Url = new Uri(server.Node.MqttUrl),
            Username = player,
            ReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(500),
        };
        configure?.Invoke(options);
        GrainWireClient client = await GrainWireClient.ConnectAsync(options);
        _clients.Add(client);
        return client;
    }

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Records what a room shows the app, to check ordering.</summary>
    private sealed class Seen
    {
        /// <summary>The counter's value after every snapshot and every "changed" event, in the order the app saw them.</summary>
        public ConcurrentQueue<int> Values { get; } = new();

        public ConcurrentQueue<long> Seqs { get; } = new();

        public ConcurrentQueue<int> Snapshots { get; } = new();

        public ConcurrentQueue<PresenceChange> Presence { get; } = new();

        public static Seen Watch(Room<CounterView> room)
        {
            Seen seen = new();
            room.OnSnapshot((view, _) =>
            {
                seen.Snapshots.Enqueue(view!.Value);
                seen.Values.Enqueue(view.Value);
            });
            room.On<Changed>("changed", (data, meta) =>
            {
                seen.Values.Enqueue(data.Value);
                seen.Seqs.Enqueue(meta.Seq);
            });
            room.OnPresence(seen.Presence.Enqueue);
            return seen;
        }
    }

    private sealed record Changed(int Value, string By);

    [Fact]
    public async Task Joins_sends_commands_and_receives_events_in_order()
    {
        string id = NewId();
        GrainWireClient alice = await Open("alice");
        GrainWireClient bob = await Open("bob");
        Assert.Equal("alice", alice.PlayerId);
        Assert.Equal(ConnectionState.Connected, alice.State);

        Room<CounterView> aliceRoom = await alice.JoinAsync<CounterView>("counter", id, new JoinOptions { Create = true, Options = new { Start = 10 } });
        Assert.Equal((10, "alice"), (aliceRoom.View!.Value, aliceRoom.View.Viewer));
        Assert.Equal(new Member("alice", "owner", MemberStatus.Online), aliceRoom.You);
        Assert.Equal(("counter", id, $"counter/{id}"), (aliceRoom.Type, aliceRoom.Id, aliceRoom.Key));

        Room<CounterView> bobRoom = await bob.JoinAsync<CounterView>("counter", id, new JoinOptions { Data = new { Role = "spectator" } });
        Assert.Equal("spectator", bobRoom.You!.Role);
        Assert.Equal(["alice", "bob"], bobRoom.Members.Select(member => member.Player).Order());
        await Until(() => aliceRoom.Members.Count == 2, "alice sees bob join");

        Seen seen = Seen.Watch(bobRoom);
        for (int i = 1; i <= 20; i++)
        {
            Assert.Equal(10 + i, await aliceRoom.RequestAsync<int>("add", new { Amount = 1 }));
        }

        await Until(() => seen.Seqs.Count == 20, "bob receives 20 events");
        Assert.Equal(Enumerable.Range(1, 20).Select(seq => (long)seq), seen.Seqs);
        Assert.Equal(Enumerable.Range(10, 21), seen.Values);

        // Joining again returns the same room object.
        Assert.Same(bobRoom, await bob.JoinAsync<CounterView>("counter", id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.JoinAsync<int>("counter", id));
    }

    [Fact]
    public async Task Join_completes_only_once_the_room_has_its_first_snapshot_even_when_that_snapshot_is_lost()
    {
        string id = NewId();
        GrainWireClient alice = await Open("alice");
        server.Drop(alice.ClientId, "$snapshot", 1);

        // Two callers at once share the join and get the same, ready room.
        JoinOptions create = new() { Create = true, Options = new { Start = 4 } };
        Room<CounterView>[] rooms = await Task.WhenAll(alice.JoinAsync<CounterView>("counter", id, create), alice.JoinAsync<CounterView>("counter", id, create));
        Assert.Same(rooms[0], rooms[1]);
        Assert.Equal((4, "alice"), (rooms[0].View!.Value, rooms[0].View!.Viewer));
        Assert.Equal("owner", rooms[0].You!.Role);
        Assert.Equal(5, await rooms[0].RequestAsync<int>("add", new { Amount = 1 }));
    }

    [Fact]
    public async Task Refusals_and_failures_are_reported_as_different_exceptions()
    {
        GrainWireClient alice = await Open("alice");
        RoomException notFound = await Assert.ThrowsAsync<RoomException>(() => alice.JoinAsync("counter", NewId()));
        Assert.Equal("room-not-found", notFound.Code);
        RoomException unknownType = await Assert.ThrowsAsync<RoomException>(() => alice.JoinAsync("no-such-type", NewId(), new JoinOptions { Create = true }));
        Assert.Equal("unknown-room-type", unknownType.Code);
        RejectedException denied = await Assert.ThrowsAsync<RejectedException>(() => alice.JoinAsync("counter", NewId(), new JoinOptions { Create = true, Options = new { Deny = true } }));
        Assert.Equal("create-denied", denied.Code);

        Room<CounterView> room = await alice.JoinAsync<CounterView>("counter", NewId(), new JoinOptions { Create = true });
        RejectedException rejected = await Assert.ThrowsAsync<RejectedException>(() => room.RequestAsync("add-then-reject", new { Amount = 5 }));
        Assert.Equal(("custom-code", "nope"), (rejected.Code, rejected.Reason));
        RoomException failed = await Assert.ThrowsAsync<RoomException>(() => room.RequestAsync("add-then-throw", new { Amount = 5 }));
        Assert.Equal("handler-error", failed.Code);
        RoomException unknownCommand = await Assert.ThrowsAsync<RoomException>(() => room.RequestAsync("no-such-command"));
        Assert.Equal("unknown-command", unknownCommand.Code);
        Assert.Equal(1, await room.RequestAsync<int>("add", new { Amount = 1 })); // none of the above changed anything

        // The development authenticator refuses a blank user name.
        ConnectException refused = await Assert.ThrowsAsync<ConnectException>(() => Open(" "));
        Assert.NotNull(refused.ReasonCode);
        Assert.Equal("A user name is required.", refused.Message);
    }

    [Fact]
    public async Task Follows_presence_reconnecting_back_online_and_leaving()
    {
        string id = NewId();
        GrainWireClient alice = await Open("alice");
        GrainWireClient bob = await Open("bob");
        Room<CounterView> aliceRoom = await alice.JoinAsync<CounterView>("quick", id, new JoinOptions { Create = true });
        Seen seen = Seen.Watch(aliceRoom);
        await bob.JoinAsync("quick", id);
        await Until(() => seen.Presence.Any(p => p is { Player: "bob", Status: MemberStatus.Online }), "bob online");

        await bob.DisposeAsync();
        await Until(() => seen.Presence.Any(p => p is { Player: "bob", Status: MemberStatus.Reconnecting }), "bob reconnecting");
        await Until(() => seen.Presence.Any(p => p is { Player: "bob", Status: MemberStatus.Left }), "bob left");
        Assert.Equal(["alice"], aliceRoom.Members.Select(member => member.Player));
        Assert.Equal(
            [MemberStatus.Online, MemberStatus.Reconnecting, MemberStatus.Left],
            seen.Presence.Where(p => p.Player == "bob").Select(p => p.Status));
    }

    [Fact]
    public async Task Reconnects_by_itself_gets_a_fresh_snapshot_and_continues_without_duplicates()
    {
        string id = NewId();
        GrainWireClient alice = await Open("alice");
        GrainWireClient bob = await Open("bob");
        Room<CounterView> aliceRoom = await alice.JoinAsync<CounterView>("counter", id, new JoinOptions { Create = true });
        Room<CounterView> bobRoom = await bob.JoinAsync<CounterView>("counter", id);
        Seen bobSees = Seen.Watch(bobRoom);
        Seen aliceSees = Seen.Watch(aliceRoom);
        ConcurrentQueue<ConnectionState> states = new();
        bob.StateChanged += states.Enqueue;

        await aliceRoom.RequestAsync("add", new { Amount = 1 });
        await Until(() => bobSees.Values.LastOrDefault() == 1, "bob receives the first event");
        await server.Disconnect(bob.ClientId);
        await Until(() => states.Contains(ConnectionState.Reconnecting), "bob notices the drop");
        await aliceRoom.RequestAsync("add", new { Amount = 1 }); // bob misses this one, or gets it right after rejoining
        await Until(() => states.LastOrDefault() == ConnectionState.Connected, "bob reconnects");

        await Until(() => bobSees.Snapshots.Count >= 2, "bob gets a fresh snapshot after reconnecting");
        await Until(() => bobSees.Values.LastOrDefault() == 2, "bob has the change he missed");
        await Until(() => aliceSees.Presence.Any(p => p is { Player: "bob", Status: MemberStatus.Online }), "alice sees bob back");
        await aliceRoom.RequestAsync("add", new { Amount = 1 });
        await Until(() => bobSees.Values.LastOrDefault() == 3, "bob receives the next event");

        // What the app saw never went backwards and never repeated an event.
        Assert.Equal(bobSees.Values.Order(), bobSees.Values);
        Assert.Equal(bobSees.Seqs.Distinct(), bobSees.Seqs);
        Assert.DoesNotContain(aliceSees.Presence, p => p.Status == MemberStatus.Left);
        Assert.Same(bobRoom, await bob.JoinAsync<CounterView>("counter", id));
        Assert.False(bobRoom.IsClosed);
    }

    // Failure test #3 of the design.
    [Fact]
    public async Task Converges_when_the_last_event_is_lost_and_the_room_goes_quiet()
    {
        string id = NewId();
        GrainWireClient alice = await Open("alice");
        GrainWireClient bob = await Open("bob", options => options.SyncInterval = TimeSpan.FromMilliseconds(800));
        Room<CounterView> aliceRoom = await alice.JoinAsync<CounterView>("counter", id, new JoinOptions { Create = true });
        Room<CounterView> bobRoom = await bob.JoinAsync<CounterView>("counter", id);
        Seen seen = Seen.Watch(bobRoom);

        server.Drop(bob.ClientId, "changed", 1);
        await aliceRoom.RequestAsync("add", new { Amount = 7 });

        await Until(() => seen.Snapshots.Contains(7), "bob catches up through a sync", 6_000);
        Assert.Empty(seen.Seqs); // the event itself was never delivered; its effect arrived with the snapshot
        await aliceRoom.RequestAsync("add", new { Amount = 1 });
        await Until(() => seen.Values.LastOrDefault() == 8, "events continue after the snapshot");
    }

    [Fact]
    public async Task Repairs_a_gap_in_the_middle_of_the_stream_through_a_sync()
    {
        string id = NewId();
        GrainWireClient alice = await Open("alice");
        GrainWireClient bob = await Open("bob");
        Room<CounterView> aliceRoom = await alice.JoinAsync<CounterView>("counter", id, new JoinOptions { Create = true });
        Room<CounterView> bobRoom = await bob.JoinAsync<CounterView>("counter", id);
        Seen seen = Seen.Watch(bobRoom);

        await aliceRoom.RequestAsync("add", new { Amount = 1 });
        server.Drop(bob.ClientId, "changed", 1);
        await aliceRoom.RequestAsync("add", new { Amount = 1 }); // lost
        await aliceRoom.RequestAsync("add", new { Amount = 1 }); // arrives, but cannot be placed yet
        await Until(() => seen.Values.LastOrDefault() == 3, "bob ends on the right value");

        Assert.Equal(seen.Values.Order(), seen.Values);
        Assert.True(seen.Snapshots.Count >= 2, "There should be the first snapshot, and the one that closed the gap.");
        Assert.DoesNotContain(2, seen.Values); // the lost event was never shown; the snapshot carried its effect
        await aliceRoom.RequestAsync("add", new { Amount = 1 });
        await Until(() => seen.Values.LastOrDefault() == 4, "and keeps following");
    }

    // Failure test #2 of the design.
    [Fact]
    public async Task Sends_a_request_again_when_its_reply_was_lost_and_the_command_still_runs_once()
    {
        GrainWireClient alice = await Open("alice", options => options.RetryInterval = TimeSpan.FromMilliseconds(300));
        Room<CounterView> room = await alice.JoinAsync<CounterView>("pcounter", NewId(), new JoinOptions { Create = true });
        server.Drop(alice.ClientId, "$reply", 1);
        Assert.Equal(5, await room.RequestAsync<int>("add", new { Amount = 5 }));
        Assert.Equal(5, await room.RequestAsync<int>("add", new { Amount = 0 }));
    }

    [Fact]
    public async Task Reports_an_unknown_outcome_not_a_rejection_when_no_reply_ever_arrives()
    {
        GrainWireClient alice = await Open("alice", options =>
        {
            options.RetryInterval = TimeSpan.FromMilliseconds(200);
            options.RequestTimeout = TimeSpan.FromSeconds(1);
        });
        Room<CounterView> room = await alice.JoinAsync<CounterView>("counter", NewId(), new JoinOptions { Create = true });
        server.Drop(alice.ClientId, "$reply", 1000);
        await Assert.ThrowsAsync<OutcomeUnknownException>(() => room.RequestAsync("add", new { Amount = 5 }));
        server.Drop(alice.ClientId, "$reply", 0);
        Assert.Equal(5, await room.RequestAsync<int>("add", new { Amount = 0 })); // it was applied, exactly once
    }

    [Fact]
    public async Task Finishes_a_request_that_was_in_flight_when_the_connection_dropped()
    {
        string id = NewId();
        GrainWireClient alice = await Open("alice");
        GrainWireClient bob = await Open("bob");
        Room<CounterView> room = await alice.JoinAsync<CounterView>("counter", id, new JoinOptions { Create = true });
        Seen bobSees = Seen.Watch(await bob.JoinAsync<CounterView>("counter", id));

        // The command is applied, but its reply is lost and then the connection goes too.
        server.Drop(alice.ClientId, "$reply", 1);
        Task<int> inFlight = room.RequestAsync<int>("add-async", new { Amount = 3 });
        await Until(() => bobSees.Values.LastOrDefault() == 3, "the command to be applied");
        await server.Disconnect(alice.ClientId);

        // After the reconnect it is sent again under the same command id, and the room answers from its memory.
        Assert.Equal(3, await inFlight.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, await room.RequestAsync<int>("add", new { Amount = 0 }));
    }

    [Fact]
    public async Task Fails_fast_instead_of_queueing_while_there_is_no_connection()
    {
        GrainWireClient alice = await Open("alice", options =>
        {
            options.ReconnectDelay = TimeSpan.FromSeconds(2);
            options.MaxReconnectDelay = TimeSpan.FromSeconds(2);
        });
        Room<CounterView> room = await alice.JoinAsync<CounterView>("counter", NewId(), new JoinOptions { Create = true });
        await server.Disconnect(alice.ClientId);
        await Until(() => alice.State == ConnectionState.Reconnecting, "alice notices the drop");
        await Assert.ThrowsAsync<NotConnectedException>(() => room.RequestAsync("add", new { Amount = 1 }));
        Assert.Throws<NotConnectedException>(() => room.Send("add", new { Amount = 1 }));
        await Until(() => alice.State == ConnectionState.Connected, "alice reconnects", 8_000);
        Assert.Equal(1, await room.RequestAsync<int>("add", new { Amount = 1 }));
    }

    // Failure test #5 of the design.
    [Fact]
    public async Task Keeps_a_player_with_two_connections_online_while_both_reconnect()
    {
        string id = NewId();
        GrainWireClient watcher = await Open("watcher");
        Room<CounterView> watcherRoom = await watcher.JoinAsync<CounterView>("quick", id, new JoinOptions { Create = true });
        Seen watcherSees = Seen.Watch(watcherRoom);
        GrainWireClient tab1 = await Open("alice");
        GrainWireClient tab2 = await Open("alice");
        Seen sees1 = Seen.Watch(await tab1.JoinAsync<CounterView>("quick", id));
        Seen sees2 = Seen.Watch(await tab2.JoinAsync<CounterView>("quick", id));

        await Task.WhenAll(server.Disconnect(tab1.ClientId), server.Disconnect(tab2.ClientId));
        await Until(() => sees1.Snapshots.Count >= 2 && sees2.Snapshots.Count >= 2, "both connections come back and rejoin");
        await Task.Delay(1_200); // longer than this room type's grace period

        await watcherRoom.RequestAsync("add", new { Amount = 4 });
        await Until(() => sees1.Values.LastOrDefault() == 4 && sees2.Values.LastOrDefault() == 4, "both connections receive the event");
        Assert.Equal(sees1.Seqs.Last(), sees2.Seqs.Last());
        Assert.DoesNotContain(watcherSees.Presence, p => p is { Player: "alice", Status: MemberStatus.Left });
        Assert.Equal(MemberStatus.Online, watcherRoom.Members.Single(member => member.Player == "alice").Status);
    }

    [Fact]
    public async Task Tells_the_app_when_it_is_kicked_when_the_room_closes_and_when_the_room_is_lost()
    {
        string id = NewId();
        GrainWireClient alice = await Open("alice");
        GrainWireClient bob = await Open("bob");
        Room<CounterView> aliceRoom = await alice.JoinAsync<CounterView>("counter", id, new JoinOptions { Create = true });
        Room<CounterView> bobRoom = await bob.JoinAsync<CounterView>("counter", id);
        ConcurrentQueue<ClosedInfo> bobClosed = new();
        bobRoom.OnClosed(bobClosed.Enqueue);

        await aliceRoom.RequestAsync("kick", new { Player = "bob" });
        await Until(() => bobClosed.Count == 1, "bob is told");
        Assert.Equal([new ClosedInfo(RoomClosedReason.Kicked, "bye")], bobClosed);
        Assert.True(bobRoom.IsClosed);
        RoomClosedException kicked = await Assert.ThrowsAsync<RoomClosedException>(() => bobRoom.RequestAsync("add", new { Amount = 1 }));
        Assert.Equal(RoomClosedReason.Kicked, kicked.Reason);

        // The command that closes the room completes normally, whichever of its reply and the closure notice comes first.
        ConcurrentQueue<ClosedInfo> aliceClosed = new();
        aliceRoom.OnClosed(aliceClosed.Enqueue);
        await aliceRoom.RequestAsync("close");
        await Until(() => aliceClosed.Count == 1, "alice is told");
        Assert.Equal([new ClosedInfo(RoomClosedReason.Closed, "done")], aliceClosed);

        // An in-memory room whose server-side state disappears.
        Room<CounterView> lostRoom = await alice.JoinAsync<CounterView>("counter", NewId(), new JoinOptions { Create = true });
        ConcurrentQueue<ClosedInfo> lost = new();
        lostRoom.OnClosed(lost.Enqueue);
        await lostRoom.RequestAsync("deactivate");
        await Until(() => lost.Count == 1, "alice learns the room is lost");
        Assert.Equal(RoomClosedReason.Lost, lost.Single().Reason);
    }

    [Fact]
    public async Task Recovers_a_persistent_room_that_restarted_without_the_app_doing_anything()
    {
        GrainWireClient alice = await Open("alice");
        Room<CounterView> room = await alice.JoinAsync<CounterView>("pcounter", NewId(), new JoinOptions { Create = true });
        Seen seen = Seen.Watch(room);
        await room.RequestAsync("add", new { Amount = 9 });
        await room.RequestAsync("deactivate");
        await Until(() => seen.Snapshots.Contains(9), "a snapshot from the restarted room");
        Assert.Equal(10, await room.RequestAsync<int>("add", new { Amount = 1 }));
        await Until(() => seen.Values.LastOrDefault() == 10, "events flow in the new epoch");
        Assert.False(room.IsClosed);
    }

    [Fact]
    public async Task Stops_following_a_room_after_leaving_it()
    {
        string id = NewId();
        GrainWireClient alice = await Open("alice");
        GrainWireClient bob = await Open("bob");
        Room<CounterView> aliceRoom = await alice.JoinAsync<CounterView>("counter", id, new JoinOptions { Create = true });
        Room<CounterView> bobRoom = await bob.JoinAsync<CounterView>("counter", id);
        Seen seen = Seen.Watch(bobRoom);
        ConcurrentQueue<ClosedInfo> closed = new();
        bobRoom.OnClosed(closed.Enqueue);

        await bobRoom.LeaveAsync();
        await aliceRoom.RequestAsync("add", new { Amount = 1 });
        await Task.Delay(300);
        Assert.Empty(seen.Seqs);
        Assert.Empty(closed); // leaving is the app's own doing, so nothing is reported back to it
        RoomClosedException left = await Assert.ThrowsAsync<RoomClosedException>(() => bobRoom.RequestAsync("add", new { Amount = 1 }));
        Assert.Equal(RoomClosedReason.Left, left.Reason);
        await bobRoom.LeaveAsync();
    }
}
