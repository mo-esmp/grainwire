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
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var room = $"{type}/{Guid.NewGuid():N}";
            var created = await creator.Join(room);
            Assert.Equal("created", created.Code());
            var where = await creator.Command(room, "where");
            if (target.Hosts(where.GetProperty("d").GetString())) return room;
            await creator.Request(room, "sys/leave");
        }

        throw new InvalidOperationException("No room was placed on the target silo.");
    }

    [Theory]
    [InlineData(false)]   // mode A: the gateway runs inside the surviving silo
    [InlineData(true)]    // mode B: a standalone gateway
    public async Task A_persistent_room_recovers_on_another_silo_while_the_browsers_stay_connected(bool standaloneGateway)
    {
        var info = new TestClusterInfo();
        await using var survivor = standaloneGateway ? await TestNode.StartSiloOnly(info, primary: true) : await TestNode.StartSiloWithGateway(info, primary: true, o => o.RoomCreationsPerMinute = 1000);
        await using var doomed = await TestNode.StartSiloOnly(info, primary: false);
        await using var standalone = standaloneGateway ? await TestNode.StartGatewayOnly(info, o => o.RoomCreationsPerMinute = 1000) : null;
        var url = (standalone ?? survivor).MqttUrl;

        await using var alice = await TestClient.ConnectAsync(url, "alice");
        await using var bob = await TestClient.ConnectAsync(url, "bob");
        var room = await CreateRoomOn(doomed, alice, "pcounter");
        await bob.Join(room, create: false);
        await alice.Command(room, "add", new { amount = 5 });
        await bob.WaitForType(room, "changed");
        var firstEpoch = alice.Of(room, "$snapshot").Single().Str("se");
        var incarnation = alice.Of(room, "$snapshot").Single().Str("ri");

        await doomed.StopAsync();

        // No WebSocket drops. The gateway's next lease renewal re-activates the room on the surviving silo,
        // and both connections get a snapshot in a new stream epoch.
        foreach (var client in new[] { alice, bob })
        {
            var snapshots = await client.WaitFor(all => all.Any(m => m.Str("t") == "$snapshot" && m.Str("room") == room && m.Str("se") != firstEpoch), timeoutMs: 30000);
            var recovered = snapshots.Last(m => m.Str("t") == "$snapshot" && m.Str("room") == room);
            Assert.Equal(incarnation, recovered.Str("ri"));
            Assert.Equal(5, recovered.GetProperty("d").Num("value"));
            Assert.True(client.IsConnected);
        }

        var reply = await alice.Command(room, "add", new { amount = 1 }, ri: incarnation);
        Assert.Equal("ok", reply.Status());
        Assert.Equal(6, reply.GetProperty("d").GetInt32());
        var newEpoch = alice.Of(room, "$snapshot").Last().Str("se");
        await bob.WaitFor(all => all.Any(m => m.Str("t") == "changed" && m.Str("se") == newEpoch && m.GetProperty("d").Num("value") == 6));

        // Nobody was removed because a server failed.
        var members = await alice.Command(room, "members");
        Assert.Equal(2, members.GetProperty("d").GetArrayLength());
        Assert.DoesNotContain(alice.Inbox().Concat(bob.Inbox()), m => m.Str("t") == "$presence" && m.GetProperty("d").Str("status") == "left");
    }

    [Fact]
    public async Task An_in_memory_room_on_a_stopped_silo_is_reported_as_lost()
    {
        var info = new TestClusterInfo();
        await using var survivor = await TestNode.StartSiloWithGateway(info, primary: true, o => o.RoomCreationsPerMinute = 1000);
        await using var doomed = await TestNode.StartSiloOnly(info, primary: false);
        await using var alice = await TestClient.ConnectAsync(survivor.MqttUrl, "alice");
        var room = await CreateRoomOn(doomed, alice, "counter");

        await doomed.StopAsync();

        var closed = (await alice.WaitForType(room, "$closed", timeoutMs: 30000)).Single();
        Assert.Equal("lost", closed.GetProperty("d").Str("reason"));
        var after = await alice.Command(room, "add", new { amount = 1 });
        Assert.Equal(("error", "not-joined"), (after.Status(), after.Code()));
    }

    [Fact]
    public async Task When_a_gateway_goes_away_its_players_show_as_reconnecting_and_then_leave()
    {
        var info = new TestClusterInfo();
        await using var silo = await TestNode.StartSiloOnly(info, primary: true);
        await using var stays = await TestNode.StartGatewayOnly(info);
        await using var goes = await TestNode.StartGatewayOnly(info);

        await using var bob = await TestClient.ConnectAsync(stays.MqttUrl, "bob");
        var alice = await TestClient.ConnectAsync(goes.MqttUrl, "alice");
        var room = $"quick/{Guid.NewGuid():N}";
        await bob.Join(room);
        await alice.Join(room, create: false);

        await goes.StopAsync();

        await bob.WaitFor(all => all.Any(m => IsPresence(m, "alice", "reconnecting")), timeoutMs: 15000);
        await bob.WaitFor(all => all.Any(m => IsPresence(m, "alice", "left")), timeoutMs: 15000);
    }

    [Fact]
    public async Task A_player_can_move_to_another_gateway_after_theirs_went_away()
    {
        var info = new TestClusterInfo();
        await using var silo = await TestNode.StartSiloOnly(info, primary: true);
        await using var stays = await TestNode.StartGatewayOnly(info);
        await using var goes = await TestNode.StartGatewayOnly(info);

        await using var bob = await TestClient.ConnectAsync(stays.MqttUrl, "bob");
        var alice = await TestClient.ConnectAsync(goes.MqttUrl, "alice");
        var room = $"counter/{Guid.NewGuid():N}";
        await bob.Join(room);
        await alice.Join(room, create: false);
        await bob.Command(room, "add", new { amount = 2 });

        await goes.StopAsync();

        await using var aliceAgain = await TestClient.ConnectAsync(stays.MqttUrl, "alice", clientId: alice.ClientId);
        Assert.Equal("joined", (await aliceAgain.Join(room, create: false)).Code());
        Assert.Equal(2, aliceAgain.Of(room, "$snapshot").Single().GetProperty("d").Num("value"));
        await bob.WaitFor(all => all.Any(m => IsPresence(m, "alice", "online") && m.Num("seq") > 1));
    }
}
