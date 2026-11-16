using System.Collections.Concurrent;
using GrainWire.Client;
using MQTTnet;
using JoinOptions = GrainWire.Client.JoinOptions;

namespace GrainWire.Tests.Client;

/// <summary>
/// The .NET client against a scripted broker (<see cref="ScriptedGateway"/>), for behaviour that depends on the order
/// in which messages arrive, and on messages that never arrive.
/// </summary>
public sealed class ClientProtocolTests : IAsyncLifetime
{
    private static readonly TimeSpan s_patience = TimeSpan.FromSeconds(10);
    private readonly List<GrainWireClient> _clients = [];
    private ScriptedGateway _gateway = null!;

    public async Task InitializeAsync() => _gateway = await ScriptedGateway.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (GrainWireClient client in _clients)
        {
            await client.DisposeAsync();
        }

        await _gateway.DisposeAsync();
    }

    private sealed record Counter(int Value);

    private async Task<GrainWireClient> Connect(Action<GrainWireClientOptions>? configure = null)
    {
        GrainWireClientOptions options = new()
        {
            // The scripted broker speaks MQTT over plain TCP, so the WebSocket address is replaced below.
            Url = new Uri("ws://127.0.0.1/mqtt"),
            Username = "alice",
            ConfigureMqtt = mqtt => mqtt.WithTcpServer("127.0.0.1", _gateway.Port),

            // Nothing happens on a timer unless a test asks for it, so every publish a test sees has a known cause.
            SyncInterval = TimeSpan.FromMinutes(5),
            RetryInterval = TimeSpan.FromMinutes(5),
            ReconnectDelay = TimeSpan.FromMilliseconds(50),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
        };
        configure?.Invoke(options);
        GrainWireClient client = await GrainWireClient.ConnectAsync(options);
        _clients.Add(client);
        return client;
    }

    /// <summary>Joins the scripted room; the "server" sends a snapshot at position <paramref name="at"/> and accepts.</summary>
    private async Task<Room<Counter>> Join(GrainWireClient client, long at = 0, int value = 0)
    {
        Task<Room<Counter>> joining = client.JoinAsync<Counter>("counter", "r1", new JoinOptions { Create = true });
        Published join = await _gateway.Next("sys/join");
        await _gateway.Snapshot(client.ClientId, at, value);
        await _gateway.Reply(client.ClientId, join.CommandId, code: "created");
        return await joining.WaitAsync(s_patience);
    }

    /// <summary>Records what a room shows the app, in the order the app sees it: "S5" is a snapshot with value 5, "E6" an event.</summary>
    private static ConcurrentQueue<string> Watch(Room<Counter> room)
    {
        ConcurrentQueue<string> seen = new();
        room.OnSnapshot((view, _) => seen.Enqueue("S" + view.Value));
        room.On<Counter>("changed", (data, meta) => seen.Enqueue($"E{data.Value}#{meta.Seq}"));
        return seen;
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

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Connects_with_mqtt_5_and_publishes_what_the_protocol_describes()
    {
        GrainWireClient client = await Connect();
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.Matches("^[A-Za-z0-9_-]{16,128}$", client.ClientId);

        Task<Room<Counter>> joining = client.JoinAsync<Counter>("counter", "r1", new JoinOptions { Create = true, Options = new { Start = 4 }, Data = new { Role = "player" } });
        Published join = await _gateway.Next("sys/join");
        Assert.Equal($$$"""{"cmd":"{{{join.CommandId}}}","create":true,"options":{"start":4},"data":{"role":"player"}}""", join.Body.GetRawText());
        await _gateway.Snapshot(client.ClientId, at: 41, value: 4);
        await _gateway.Reply(client.ClientId, join.CommandId, code: "created");
        Room<Counter> room = await joining.WaitAsync(s_patience);
        Assert.Equal(new Counter(4), room.View);
        Assert.Equal(new Member("alice", "owner", MemberStatus.Online), room.You);
        Assert.Equal<Member>([new Member("alice", "owner", MemberStatus.Online)], room.Members);

        Task<int> adding = room.RequestAsync<int>("add", new { Amount = 2 });
        Published add = await _gateway.Next("cmd/add");
        Assert.Equal($$$"""{"cmd":"{{{add.CommandId}}}","ri":"i1","d":{"amount":2}}""", add.Body.GetRawText());
        Assert.NotEqual(join.CommandId, add.CommandId);
        await _gateway.Reply(client.ClientId, add.CommandId, d: "6");
        Assert.Equal(6, await adding.WaitAsync(s_patience));

        room.Send("touch");
        Published touch = await _gateway.Next("cmd/touch");
        Assert.Equal($$$"""{"cmd":"{{{touch.CommandId}}}","noreply":true,"ri":"i1"}""", touch.Body.GetRawText());

        Task syncing = room.SyncAsync();
        Published sync = await _gateway.Next("sys/sync");
        Assert.Equal($$$"""{"cmd":"{{{sync.CommandId}}}","ri":"i1","se":"e1","seq":41}""", sync.Body.GetRawText());
        await _gateway.Reply(client.ClientId, sync.CommandId, code: "in-sync");
        await syncing.WaitAsync(s_patience);

        Task leaving = room.LeaveAsync();
        Published leave = await _gateway.Next("sys/leave");
        await _gateway.Reply(client.ClientId, leave.CommandId, code: "left");
        await leaving.WaitAsync(s_patience);
        Assert.Equal(RoomClosedReason.Left, room.ClosedReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Join_completes_once_both_the_reply_and_the_snapshot_are_in_whichever_comes_first(bool snapshotFirst)
    {
        GrainWireClient client = await Connect();
        Task<Room<Counter>> joining = client.JoinAsync<Counter>("counter", "r1");
        Published join = await _gateway.Next("sys/join");

        if (snapshotFirst)
        {
            await _gateway.Snapshot(client.ClientId, at: 3, value: 7);
        }
        else
        {
            await _gateway.Reply(client.ClientId, join.CommandId, code: "joined");
        }

        await Task.Delay(100);
        Assert.False(joining.IsCompleted, "The room must not be handed out with only half of the join.");

        if (snapshotFirst)
        {
            await _gateway.Reply(client.ClientId, join.CommandId, code: "joined");
        }
        else
        {
            await _gateway.Snapshot(client.ClientId, at: 3, value: 7);
        }

        Room<Counter> room = await joining.WaitAsync(s_patience);
        Assert.Equal(7, room.View!.Value);

        // The stream continues right after the snapshot's position.
        ConcurrentQueue<string> seen = Watch(room);
        await _gateway.Event(client.ClientId, seq: 4, value: 8);
        await Until(() => seen.Count == 2, "the event after the snapshot");
        Assert.Equal(["S7", "E8#4"], seen);
    }

    [Fact]
    public async Task Join_asks_for_the_snapshot_again_when_it_was_lost()
    {
        GrainWireClient client = await Connect();
        Task<Room<Counter>> joining = client.JoinAsync<Counter>("counter", "r1");
        Published join = await _gateway.Next("sys/join");
        await _gateway.Reply(client.ClientId, join.CommandId, code: "joined");

        // No snapshot: after the gap wait the client says it has nothing yet.
        Published sync = await _gateway.Next("sys/sync");
        Assert.Equal(-1, sync.Body.GetProperty("seq").GetInt64());
        Assert.False(joining.IsCompleted);
        await _gateway.Snapshot(client.ClientId, at: 2, value: 9);
        await _gateway.Reply(client.ClientId, sync.CommandId, code: "resynced");
        Assert.Equal(9, (await joining.WaitAsync(s_patience)).View!.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_command_completes_whether_its_events_or_its_reply_arrive_first(bool eventsFirst)
    {
        GrainWireClient client = await Connect();
        Room<Counter> room = await Join(client);
        ConcurrentQueue<string> seen = Watch(room);

        Task<int> adding = room.RequestAsync<int>("add", new { Amount = 1 });
        Published add = await _gateway.Next("cmd/add");
        if (eventsFirst)
        {
            await _gateway.Event(client.ClientId, seq: 1, value: 1);
            await _gateway.Event(client.ClientId, seq: 2, value: 2);
            await Until(() => seen.Count == 3, "both events");
            Assert.False(adding.IsCompleted, "Events do not stand in for the reply.");
            await _gateway.Reply(client.ClientId, add.CommandId, d: "2");
        }
        else
        {
            await _gateway.Reply(client.ClientId, add.CommandId, d: "2");
            Assert.Equal(2, await adding.WaitAsync(s_patience));
            await _gateway.Event(client.ClientId, seq: 1, value: 1);
            await _gateway.Event(client.ClientId, seq: 2, value: 2);
        }

        Assert.Equal(2, await adding.WaitAsync(s_patience));
        await Until(() => seen.Count == 3, "both events");
        Assert.Equal(["S0", "E1#1", "E2#2"], seen);
    }

    [Fact]
    public async Task Events_reach_the_app_in_order_and_once_however_they_arrive()
    {
        GrainWireClient client = await Connect();
        Room<Counter> room = await Join(client);
        ConcurrentQueue<string> seen = Watch(room);
        List<string> any = [];
        room.OnAny((name, data, meta) => any.Add($"{name}:{data.GetProperty("value").GetInt32()}#{meta.Seq}"));

        await _gateway.Event(client.ClientId, seq: 2, value: 2);
        await _gateway.Event(client.ClientId, seq: 3, value: 3);
        await _gateway.Event(client.ClientId, seq: 2, value: 2);
        await _gateway.Event(client.ClientId, seq: 1, value: 1);
        await _gateway.Event(client.ClientId, seq: 1, value: 1);
        await _gateway.Event(client.ClientId, seq: 3, value: 3);
        await _gateway.Event(client.ClientId, seq: 4, value: 4);

        await Until(() => seen.Count == 5, "four events");
        Assert.Equal(["S0", "E1#1", "E2#2", "E3#3", "E4#4"], seen);
        Assert.Equal(["changed:1#1", "changed:2#2", "changed:3#3", "changed:4#4"], any);
        Assert.DoesNotContain(_gateway.All(), p => p.Topic.EndsWith("/sys/sync", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("e2", "i1")] // the room restarted on the server: a new stream epoch
    [InlineData("e9", "i2")] // the room was closed and created again under the same id: a new incarnation
    public async Task A_new_epoch_or_incarnation_is_followed_only_through_a_snapshot(string se, string ri)
    {
        GrainWireClient client = await Connect();
        Room<Counter> room = await Join(client, at: 5, value: 5);
        ConcurrentQueue<string> seen = Watch(room);

        // An event of a stream the client is not following cannot be placed: it asks where it stands.
        await _gateway.Event(client.ClientId, seq: 1, value: 100, se, ri);
        Published sync = await _gateway.Next("sys/sync");
        Assert.Equal(("i1", "e1", 5L), (sync.Body.GetProperty("ri").GetString(), sync.Body.GetProperty("se").GetString(), sync.Body.GetProperty("seq").GetInt64()));
        Assert.Equal(["S5"], seen);

        await _gateway.Snapshot(client.ClientId, at: 0, value: 99, se, ri);
        await _gateway.Reply(client.ClientId, sync.CommandId, code: "resynced");
        await Until(() => seen.Count == 3, "the snapshot and the held event");
        Assert.Equal(["S5", "S99", "E100#1"], seen);

        // Commands now name the stream the client follows.
        Task adding = room.RequestAsync("add");
        Published add = await _gateway.Next("cmd/add");
        Assert.Equal(ri, add.Body.GetProperty("ri").GetString());
        await _gateway.Reply(client.ClientId, add.CommandId);
        await adding.WaitAsync(s_patience);
    }

    [Fact]
    public async Task A_closure_notice_that_overtakes_the_reply_does_not_fail_the_command_that_closed_the_room()
    {
        GrainWireClient client = await Connect();
        Room<Counter> room = await Join(client);
        ConcurrentQueue<ClosedInfo> closed = new();
        room.OnClosed(closed.Enqueue);

        Task<string> closing = room.RequestAsync<string>("close");
        Published close = await _gateway.Next("cmd/close");
        await _gateway.Closed(client.ClientId, "closed", "done");
        await Until(() => room.IsClosed, "the room to close");
        await Task.Delay(50);
        Assert.False(closing.IsCompleted, "The command still waits for its own reply.");

        await _gateway.Reply(client.ClientId, close.CommandId, d: "\"bye\"");
        Assert.Equal("bye", await closing.WaitAsync(s_patience));
        Assert.Equal([new ClosedInfo(RoomClosedReason.Closed, "done")], closed);
        Assert.Equal(RoomClosedReason.Closed, room.ClosedReason);

        RoomClosedException after = await Assert.ThrowsAsync<RoomClosedException>(() => room.RequestAsync("add"));
        Assert.Equal(RoomClosedReason.Closed, after.Reason);
        Assert.Throws<RoomClosedException>(() => room.Send("add"));

        // The room is forgotten, so joining again starts over instead of returning the closed one.
        Task<Room<Counter>> again = client.JoinAsync<Counter>("counter", "r1");
        Published join = await _gateway.Next("sys/join");
        await _gateway.Snapshot(client.ClientId, at: 0, value: 0, se: "e5", ri: "i2");
        await _gateway.Reply(client.ClientId, join.CommandId, code: "joined");
        Assert.NotSame(room, await again.WaitAsync(s_patience));
    }

    [Fact]
    public async Task A_request_without_a_reply_is_sent_again_with_the_same_command_id()
    {
        GrainWireClient client = await Connect(options => options.RetryInterval = TimeSpan.FromMilliseconds(150));
        Room<Counter> room = await Join(client);

        Task<int> adding = room.RequestAsync<int>("add", new { Amount = 5 });
        Published first = await _gateway.Next("cmd/add");
        Published second = await _gateway.Next("cmd/add");
        Published third = await _gateway.Next("cmd/add");
        Assert.Equal(first.CommandId, second.CommandId);
        Assert.Equal(first.Body.GetRawText(), third.Body.GetRawText());

        await _gateway.Reply(client.ClientId, first.CommandId, d: "5");
        Assert.Equal(5, await adding.WaitAsync(s_patience));

        // A late duplicate of the reply is ignored, and the next request gets an id of its own.
        await _gateway.Reply(client.ClientId, first.CommandId, d: "5");
        Task<int> next = room.RequestAsync<int>("add", new { Amount = 5 });
        Published other = await _gateway.Next("cmd/add");
        Assert.NotEqual(first.CommandId, other.CommandId);
        await _gateway.Reply(client.ClientId, other.CommandId, d: "10");
        Assert.Equal(10, await next.WaitAsync(s_patience));
    }

    [Fact]
    public async Task A_request_that_never_gets_a_reply_ends_as_unknown_not_as_rejected()
    {
        GrainWireClient client = await Connect(options =>
        {
            options.RetryInterval = TimeSpan.FromMilliseconds(150);
            options.RequestTimeout = TimeSpan.FromMilliseconds(700);
        });
        Room<Counter> room = await Join(client);

        Task<int> adding = room.RequestAsync<int>("add", new { Amount = 5 });
        Published sent = await _gateway.Next("cmd/add");
        OutcomeUnknownException unknown = await Assert.ThrowsAsync<OutcomeUnknownException>(() => adding.WaitAsync(s_patience));
        Assert.Equal(sent.CommandId, unknown.CommandId);
        Assert.False(room.IsClosed);

        // A timeout of its own, shorter than the client's.
        Task quick = room.RequestAsync("add", timeout: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OutcomeUnknownException>(() => quick.WaitAsync(TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public async Task Rejections_and_errors_are_different_exceptions()
    {
        GrainWireClient client = await Connect();
        Room<Counter> room = await Join(client);

        Task rejectedTask = room.RequestAsync("add");
        await _gateway.Reply(client.ClientId, (await _gateway.Next("cmd/add")).CommandId, "rejected", "custom-code");
        RejectedException rejected = await Assert.ThrowsAsync<RejectedException>(() => rejectedTask.WaitAsync(s_patience));
        Assert.Equal("custom-code", rejected.Code);

        Task failedTask = room.RequestAsync("add");
        await _gateway.Reply(client.ClientId, (await _gateway.Next("cmd/add")).CommandId, "error", "handler-error");
        RoomException failed = await Assert.ThrowsAsync<RoomException>(() => failedTask.WaitAsync(s_patience));
        Assert.Equal("handler-error", failed.Code);
        Assert.False(room.IsClosed);

        // Some errors say the room is gone for this client.
        ConcurrentQueue<ClosedInfo> closed = new();
        room.OnClosed(closed.Enqueue);
        Task staleTask = room.RequestAsync("add");
        await _gateway.Reply(client.ClientId, (await _gateway.Next("cmd/add")).CommandId, "error", "stale-room");
        Assert.Equal("stale-room", (await Assert.ThrowsAsync<RoomException>(() => staleTask.WaitAsync(s_patience))).Code);
        await Until(() => closed.Count == 1, "the room to be reported lost");
        Assert.Equal(RoomClosedReason.Lost, room.ClosedReason);
    }

    [Fact]
    public async Task An_unavailable_room_leaves_the_request_waiting_to_be_sent_again()
    {
        GrainWireClient client = await Connect(options => options.RetryInterval = TimeSpan.FromMilliseconds(150));
        Room<Counter> room = await Join(client);

        Task<int> adding = room.RequestAsync<int>("add", new { Amount = 5 });
        Published first = await _gateway.Next("cmd/add");
        await _gateway.Reply(client.ClientId, first.CommandId, "error", "unavailable");
        Published second = await _gateway.Next("cmd/add");
        Assert.Equal(first.CommandId, second.CommandId);
        Assert.False(adding.IsCompleted);
        await _gateway.Reply(client.ClientId, second.CommandId, d: "5");
        Assert.Equal(5, await adding.WaitAsync(s_patience));
    }

    [Fact]
    public async Task A_gateway_that_does_not_know_the_connection_is_joined_again_before_the_command_is_sent_again()
    {
        GrainWireClient client = await Connect();
        Room<Counter> room = await Join(client);
        ConcurrentQueue<string> seen = Watch(room);

        Task<int> adding = room.RequestAsync<int>("add", new { Amount = 5 });
        Published first = await _gateway.Next("cmd/add");
        await _gateway.Reply(client.ClientId, first.CommandId, "error", "not-joined");

        Published rejoin = await _gateway.Next("sys/join");
        Assert.False(rejoin.Body.GetProperty("create").GetBoolean());
        await _gateway.Snapshot(client.ClientId, at: 0, value: 0);
        await _gateway.Reply(client.ClientId, rejoin.CommandId, code: "joined");

        Published second = await _gateway.Next("cmd/add");
        Assert.Equal(first.CommandId, second.CommandId);
        await _gateway.Reply(client.ClientId, second.CommandId, d: "5");
        Assert.Equal(5, await adding.WaitAsync(s_patience));
        Assert.Equal(["S0", "S0"], seen);
    }

    [Fact]
    public async Task After_a_reconnect_rooms_are_joined_again_and_unanswered_commands_are_sent_again()
    {
        GrainWireClient client = await Connect();
        ConcurrentQueue<ConnectionState> states = new();
        client.StateChanged += states.Enqueue;
        Task<Room<Counter>> joining = client.JoinAsync<Counter>("counter", "r1", new JoinOptions { Create = true, Data = new { Role = "player" } });
        Published join = await _gateway.Next("sys/join");
        await _gateway.Snapshot(client.ClientId, at: 0, value: 0);
        await _gateway.Reply(client.ClientId, join.CommandId, code: "created");
        Room<Counter> room = await joining.WaitAsync(s_patience);
        ConcurrentQueue<string> seen = Watch(room);

        Task<int> adding = room.RequestAsync<int>("add", new { Amount = 1 });
        Published first = await _gateway.Next("cmd/add");
        await _gateway.Disconnect(client.ClientId);

        // The room is joined again first: with the same join data, and without creating anything.
        Published rejoin = await _gateway.Next("sys/join");
        Assert.Equal($$$"""{"cmd":"{{{rejoin.CommandId}}}","create":false,"data":{"role":"player"}}""", rejoin.Body.GetRawText());
        Assert.NotEqual(join.CommandId, rejoin.CommandId);
        await Until(() => states.Count == 2, "both state changes");
        Assert.Equal([ConnectionState.Reconnecting, ConnectionState.Connected], states);
        Assert.False(adding.IsCompleted);

        // The server applied the command before the connection dropped; the fresh snapshot shows it.
        await _gateway.Snapshot(client.ClientId, at: 1, value: 1);
        await _gateway.Reply(client.ClientId, rejoin.CommandId, code: "joined");

        // Then the command goes out again under its original id, so the room answers from its memory.
        Published second = await _gateway.Next("cmd/add");
        Assert.Equal(first.Body.GetRawText(), second.Body.GetRawText());
        await _gateway.Reply(client.ClientId, second.CommandId, d: "1");
        Assert.Equal(1, await adding.WaitAsync(s_patience));

        // The event the client missed is not replayed: its effect came with the snapshot.
        await _gateway.Event(client.ClientId, seq: 1, value: 1);
        await _gateway.Event(client.ClientId, seq: 2, value: 2);
        await Until(() => seen.Count == 3, "the next event");
        Assert.Equal(["S0", "S1", "E2#2"], seen);
    }

    [Fact]
    public async Task Requests_fail_at_once_while_there_is_no_connection_and_when_the_client_is_disposed()
    {
        GrainWireClient client = await Connect(options =>
        {
            options.ReconnectDelay = TimeSpan.FromSeconds(30);
            options.MaxReconnectDelay = TimeSpan.FromSeconds(30);
        });
        Room<Counter> room = await Join(client);
        Task<int> inFlight = room.RequestAsync<int>("add");
        await _gateway.Next("cmd/add");

        await _gateway.Disconnect(client.ClientId);
        await Until(() => client.State == ConnectionState.Reconnecting, "the client to notice the drop");
        await Assert.ThrowsAsync<NotConnectedException>(() => room.RequestAsync("add"));
        Assert.Throws<NotConnectedException>(() => room.Send("add"));
        await Assert.ThrowsAsync<NotConnectedException>(() => room.SyncAsync());
        await Assert.ThrowsAsync<NotConnectedException>(() => client.JoinAsync("counter", "other"));
        Assert.False(inFlight.IsCompleted, "A request that was sent keeps waiting for the connection to come back.");

        await client.DisposeAsync();
        await Assert.ThrowsAsync<NotConnectedException>(() => inFlight.WaitAsync(s_patience));
        Assert.Equal(ConnectionState.Disconnected, client.State);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task A_join_without_an_answer_fails_as_unknown_and_leaves_nothing_behind()
    {
        GrainWireClient client = await Connect(options => options.RequestTimeout = TimeSpan.FromMilliseconds(500));
        Task<Room<Counter>> joining = client.JoinAsync<Counter>("counter", "r1");
        Task<Room<Counter>> same = client.JoinAsync<Counter>("counter", "r1");
        Published join = await _gateway.Next("sys/join");
        await Assert.ThrowsAsync<OutcomeUnknownException>(() => joining.WaitAsync(s_patience));
        await Assert.ThrowsAsync<OutcomeUnknownException>(() => same.WaitAsync(s_patience));

        // Accepted by the server, but the snapshot never comes: the client gives up and tells the server to stop sending.
        Task<Room<Counter>> again = client.JoinAsync<Counter>("counter", "r1");
        Published second = await _gateway.Next("sys/join");
        Assert.NotEqual(join.CommandId, second.CommandId);
        await _gateway.Reply(client.ClientId, second.CommandId, code: "joined");
        await Assert.ThrowsAsync<OutcomeUnknownException>(() => again.WaitAsync(s_patience));
        await _gateway.Next("sys/leave");
    }

    [Fact]
    public async Task Cancelling_a_request_stops_the_wait_and_the_resending()
    {
        GrainWireClient client = await Connect(options => options.RetryInterval = TimeSpan.FromMilliseconds(100));
        Room<Counter> room = await Join(client);
        using CancellationTokenSource cancel = new();
        Task<int> adding = room.RequestAsync<int>("add", cancellationToken: cancel.Token);
        await _gateway.Next("cmd/add");
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding);

        int sent = _gateway.All().Count;
        await Task.Delay(350);
        Assert.Equal(sent, _gateway.All().Count);
    }

    [Fact]
    public async Task A_failing_handler_does_not_stop_the_others_or_the_stream()
    {
        ConcurrentQueue<Exception> errors = new();
        GrainWireClient client = await Connect(options => options.OnHandlerError = errors.Enqueue);
        Room<Counter> room = await Join(client);
        room.On<Counter>("changed", _ => throw new InvalidOperationException("boom"));
        ConcurrentQueue<string> seen = Watch(room);
        using IDisposable removed = room.On<Counter>("changed", data => seen.Enqueue("removed" + data.Value));
        removed.Dispose();

        await _gateway.Event(client.ClientId, seq: 1, value: 1);
        await _gateway.Event(client.ClientId, seq: 2, value: 2);
        await Until(() => seen.Count == 3, "both events");
        Assert.Equal(["S0", "E1#1", "E2#2"], seen);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Equal("boom", error.Message));
    }

    [Fact]
    public async Task Options_are_checked_before_connecting()
    {
        static GrainWireClientOptions Valid() => new() { Url = new Uri("ws://127.0.0.1:1/mqtt") };

        GrainWireClientOptions shortId = Valid();
        shortId.ClientId = "short";
        await Assert.ThrowsAsync<ArgumentException>(() => GrainWireClient.ConnectAsync(shortId));

        GrainWireClientOptions http = Valid();
        http.Url = new Uri("http://127.0.0.1:1/mqtt");
        await Assert.ThrowsAsync<ArgumentException>(() => GrainWireClient.ConnectAsync(http));

        GrainWireClientOptions noTimeout = Valid();
        noTimeout.RequestTimeout = TimeSpan.Zero;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GrainWireClient.ConnectAsync(noTimeout));

        // Nothing listens there: the first connection fails with a ConnectException, not with a reconnect loop.
        GrainWireClientOptions unreachable = Valid();
        unreachable.ConnectTimeout = TimeSpan.FromSeconds(3);
        await Assert.ThrowsAsync<ConnectException>(() => GrainWireClient.ConnectAsync(unreachable));
    }
}
