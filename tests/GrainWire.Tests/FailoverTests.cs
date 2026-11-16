using System.Text.Json;
using GrainWire.Tests.Support;

namespace GrainWire.Tests;

/// <summary>
/// What happens when a server goes away. Each test starts its own cluster because it stops nodes.
/// Covers failure test #4 of the design in both hosting modes.
/// </summary>
public class FailoverTests
{
    private static bool IsPresence(JsonElement message, string player, string status) =>
        message.Str("t") == "$presence" && message.GetProperty("d").Str("player") == player && message.GetProperty("d").Str("status") == status;

    /// <summary>Creates rooms until one is hosted on <paramref name="target"/>, and returns its address ("type/id").</summary>
    private static async Task<string> CreateRoomOn(TestNode target, TestClient creator, string type)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            string room = $"{type}/{Guid.NewGuid():N}";
            JsonElement created = await creator.Join(room);
            Assert.Equal("created", created.Code());
            JsonElement where = await creator.Command(room, "where");
            if (target.Hosts(where.GetProperty("d").GetString()))
            {
                return room;
            }

            await creator.Request(room, "sys/leave");
        }

        throw new InvalidOperationException("No room was placed on the target silo.");
    }

    [Theory]
    [InlineData(false)]   // mode A: the gateway runs inside the surviving silo
    [InlineData(true)]    // mode B: a standalone gateway
    public async Task A_persistent_room_recovers_on_another_silo_while_the_browsers_stay_connected(bool standaloneGateway)
    {
        TestClusterInfo info = new();
        await using TestNode survivor = standaloneGateway ? await TestNode.StartSiloOnly(info, primary: true) : await TestNode.StartSiloWithGateway(info, primary: true, o => o.RoomCreationsPerMinute = 1000);
        await using TestNode doomed = await TestNode.StartSiloOnly(info, primary: false);
        await using TestNode? standalone = standaloneGateway ? await TestNode.StartGatewayOnly(info, o => o.RoomCreationsPerMinute = 1000) : null;
        string url = (standalone ?? survivor).MqttUrl;

        await using TestClient alice = await TestClient.ConnectAsync(url, "alice");
        await using TestClient bob = await TestClient.ConnectAsync(url, "bob");
        string room = await CreateRoomOn(doomed, alice, "pcounter");
        await bob.Join(room, create: false);
        await alice.Command(room, "add", new { amount = 5 });
        await bob.WaitForType(room, "changed");
        string? firstEpoch = alice.Of(room, "$snapshot").Single().Str("se");
        string? incarnation = alice.Of(room, "$snapshot").Single().Str("ri");

        await doomed.StopAsync();

        // No WebSocket drops. The gateway's next lease renewal re-activates the room on the surviving silo,
        // and both connections get a snapshot in a new stream epoch.
        foreach (TestClient? client in new[] { alice, bob })
        {
            List<JsonElement> snapshots = await client.WaitFor(all => all.Any(m => m.Str("t") == "$snapshot" && m.Str("room") == room && m.Str("se") != firstEpoch), timeoutMs: 30000);
            JsonElement recovered = snapshots.Last(m => m.Str("t") == "$snapshot" && m.Str("room") == room);
            Assert.Equal(incarnation, recovered.Str("ri"));
            Assert.Equal(5, recovered.GetProperty("d").Num("value"));
            Assert.True(client.IsConnected);
        }

        JsonElement reply = await alice.Command(room, "add", new { amount = 1 }, ri: incarnation);
        Assert.Equal("ok", reply.Status());
        Assert.Equal(6, reply.GetProperty("d").GetInt32());
        string? newEpoch = alice.Of(room, "$snapshot").Last().Str("se");
        await bob.WaitFor(all => all.Any(m => m.Str("t") == "changed" && m.Str("se") == newEpoch && m.GetProperty("d").Num("value") == 6));

        // Nobody was removed because a server failed.
        JsonElement members = await alice.Command(room, "members");
        Assert.Equal(2, members.GetProperty("d").GetArrayLength());
        Assert.DoesNotContain(alice.Inbox().Concat(bob.Inbox()), m => m.Str("t") == "$presence" && m.GetProperty("d").Str("status") == "left");
    }

    [Fact]
    public async Task An_in_memory_room_on_a_stopped_silo_is_reported_as_lost()
    {
        TestClusterInfo info = new();
        await using TestNode survivor = await TestNode.StartSiloWithGateway(info, primary: true, o => o.RoomCreationsPerMinute = 1000);
        await using TestNode doomed = await TestNode.StartSiloOnly(info, primary: false);
        await using TestClient alice = await TestClient.ConnectAsync(survivor.MqttUrl, "alice");
        string room = await CreateRoomOn(doomed, alice, "counter");

        await doomed.StopAsync();

        JsonElement closed = (await alice.WaitForType(room, "$closed", timeoutMs: 30000)).Single();
        Assert.Equal("lost", closed.GetProperty("d").Str("reason"));
        JsonElement after = await alice.Command(room, "add", new { amount = 1 });
        Assert.Equal(("error", "not-joined"), (after.Status(), after.Code()));
    }

    [Fact]
    public async Task When_a_gateway_goes_away_its_players_show_as_reconnecting_and_then_leave()
    {
        TestClusterInfo info = new();
        await using TestNode silo = await TestNode.StartSiloOnly(info, primary: true);
        await using TestNode stays = await TestNode.StartGatewayOnly(info);
        await using TestNode goes = await TestNode.StartGatewayOnly(info);

        await using TestClient bob = await TestClient.ConnectAsync(stays.MqttUrl, "bob");
        TestClient alice = await TestClient.ConnectAsync(goes.MqttUrl, "alice");
        string room = $"quick/{Guid.NewGuid():N}";
        await bob.Join(room);
        await alice.Join(room, create: false);

        await goes.StopAsync();

        await bob.WaitFor(all => all.Any(m => IsPresence(m, "alice", "reconnecting")), timeoutMs: 15000);
        await bob.WaitFor(all => all.Any(m => IsPresence(m, "alice", "left")), timeoutMs: 15000);
    }

    [Fact]
    public async Task A_client_that_cannot_keep_up_is_disconnected_and_cleaned_up_and_can_come_back()
    {
        TestClusterInfo info = new();
        await using TestNode node = await TestNode.StartSiloWithGateway(info, primary: true, options => options.MaxOutgoingQueue = 2);
        string room = $"counter/{Guid.NewGuid():N}";
        TestClient alice = await TestClient.ConnectAsync(node.MqttUrl, "alice");
        TestClient bob = await TestClient.ConnectAsync(node.MqttUrl, "bob");
        await alice.Join(room);
        await bob.Join(room, create: false);

        // Thousands of events from one command: far more than two can wait for one connection.
        await alice.Publish($"rooms/{room}/cmd/burst", """{"noreply":true,"d":{"amount":5000}}""");
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (alice.IsConnected || bob.IsConnected)
        {
            Assert.True(DateTime.UtcNow < deadline, "the overflowing connections were never disconnected");
            await Task.Delay(50);
        }

        // The gateway forgets them and tells the room. A new member sees both as reconnecting: either already in
        // its snapshot, or in a presence event shortly after (the room hears about a disconnect a moment later).
        await using TestClient carol = await TestClient.ConnectAsync(node.MqttUrl, "carol");
        await carol.Join(room, create: false);
        Dictionary<string, string?> snapshot = carol.Of(room, "$snapshot").Single().GetProperty("members").EnumerateArray().ToDictionary(m => m.Str("player")!, m => m.Str("status"));
        foreach (string? player in new[] { "alice", "bob" })
        {
            if (snapshot[player] != "reconnecting")
            {
                await carol.WaitFor(all => all.Any(m => IsPresence(m, player, "reconnecting")));
            }
        }

        await using TestClient aliceAgain = await TestClient.ConnectAsync(node.MqttUrl, "alice", clientId: alice.ClientId);
        Assert.Equal("joined", (await aliceAgain.Join(room, create: false)).Code());
        await carol.WaitFor(all => all.Any(m => IsPresence(m, "alice", "online")));
        alice.Abort();
        bob.Abort();
    }

    [Fact]
    public async Task A_player_can_move_to_another_gateway_after_theirs_went_away()
    {
        TestClusterInfo info = new();
        await using TestNode silo = await TestNode.StartSiloOnly(info, primary: true);
        await using TestNode stays = await TestNode.StartGatewayOnly(info);
        await using TestNode goes = await TestNode.StartGatewayOnly(info);

        await using TestClient bob = await TestClient.ConnectAsync(stays.MqttUrl, "bob");
        TestClient alice = await TestClient.ConnectAsync(goes.MqttUrl, "alice");
        string room = $"counter/{Guid.NewGuid():N}";
        await bob.Join(room);
        await alice.Join(room, create: false);
        await bob.Command(room, "add", new { amount = 2 });

        await goes.StopAsync();

        await using TestClient aliceAgain = await TestClient.ConnectAsync(stays.MqttUrl, "alice", clientId: alice.ClientId);
        Assert.Equal("joined", (await aliceAgain.Join(room, create: false)).Code());
        Assert.Equal(2, aliceAgain.Of(room, "$snapshot").Single().GetProperty("d").Num("value"));
        await bob.WaitFor(all => all.Any(m => IsPresence(m, "alice", "online") && m.Num("seq") > 1));
    }
}
