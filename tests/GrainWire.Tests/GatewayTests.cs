using System.Text;
using System.Text.Json;
using GrainWire.Server;
using GrainWire.Tests.Support;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Formatter;

namespace GrainWire.Tests;

/// <summary>End to end: real MQTT clients over WebSockets against two silos with gateways and one standalone gateway.</summary>
[Collection(GatewayCollection.Name)]
public class GatewayTests(GatewayClusterFixture cluster)
{
    private static string NewRoom(string type = "counter") => $"{type}/{Guid.NewGuid():N}";

    private static bool IsPresence(System.Text.Json.JsonElement message, string player, string status) =>
        message.GetProperty("t").GetString() == "$presence" && message.GetProperty("d").Str("player") == player && message.GetProperty("d").Str("status") == status;

    [Fact]
    public async Task Two_players_on_different_servers_share_a_room()
    {
        string room = NewRoom();
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        await using TestClient bob = await TestClient.ConnectAsync(cluster.NodeB.MqttUrl, "bob");
        Assert.Equal("alice", Encoding.UTF8.GetString(alice.ConnectResult.UserProperties.Single(p => p.Name == "gw-player").ValueBuffer.Span));

        JsonElement created = await alice.Join(room, options: new { start = 10 });
        Assert.Equal(("ok", "created"), (created.Status(), created.Code()));
        JsonElement snapshot = Assert.Single(alice.Of(room, "$snapshot"));
        Assert.Equal(0, snapshot.Num("at"));
        Assert.Equal(10, snapshot.GetProperty("d").Num("value"));
        Assert.Equal("owner", snapshot.GetProperty("you").Str("role"));
        Assert.Equal(created.Str("ri"), snapshot.Str("ri"));

        JsonElement joined = await bob.Join(room, create: false);
        Assert.Equal(("ok", "joined"), (joined.Status(), joined.Code()));
        Assert.Equal(2, Assert.Single(bob.Of(room, "$snapshot")).GetProperty("members").GetArrayLength());
        JsonElement presence = (await alice.WaitForType(room, "$presence")).Single();
        Assert.Equal(1, presence.Num("seq"));
        Assert.Equal("bob", presence.GetProperty("d").Str("player"));

        JsonElement reply = await alice.Command(room, "add", new { amount = 5 }, ri: created.Str("ri"));
        Assert.Equal("ok", reply.Status());
        Assert.Equal(15, reply.GetProperty("d").GetInt32());

        JsonElement seenByBob = (await bob.WaitForType(room, "changed")).Single();
        Assert.Equal(1, seenByBob.Num("seq"));
        Assert.Equal(15, seenByBob.GetProperty("d").Num("value"));
        Assert.Equal(snapshot.Str("se"), seenByBob.Str("se"));
        JsonElement seenByAlice = (await alice.WaitForType(room, "changed")).Single();
        Assert.Equal(2, seenByAlice.Num("seq"));
    }

    [Fact]
    public async Task Requests_are_refused_with_a_clear_code_when_they_cannot_be_routed()
    {
        string room = NewRoom();
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");

        JsonElement notJoined = await alice.Command(room, "add", new { amount = 1 });
        Assert.Equal(("error", "not-joined"), (notJoined.Status(), notJoined.Code()));

        JsonElement noSuchRoom = await alice.Join(room, create: false);
        Assert.Equal(("error", "room-not-found"), (noSuchRoom.Status(), noSuchRoom.Code()));

        JsonElement noSuchType = await alice.Join("nonsense/abc");
        Assert.Equal(("error", "unknown-room-type"), (noSuchType.Status(), noSuchType.Code()));

        JsonElement noSuchSys = await alice.Request(room, "sys/explode");
        Assert.Equal(("error", "unknown-command"), (noSuchSys.Status(), noSuchSys.Code()));

        await alice.Join(room);
        JsonElement rejected = await alice.Command(room, "add-then-reject", new { amount = 1 });
        Assert.Equal(("rejected", "custom-code", "nope"), (rejected.Status(), rejected.Code(), rejected.Str("reason")));
    }

    [Fact]
    public async Task A_connection_can_only_subscribe_to_its_own_inbox()
    {
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        await using TestClient mallory = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "mallory");

        foreach (string? topic in new[] { alice.InboxTopic, "clients/+/inbox", "#", "rooms/#", $"$share/group/{mallory.InboxTopic}" })
        {
            MqttClientSubscribeResult result = await mallory.Subscribe(topic);
            Assert.True((int)result.Items.Single().ResultCode >= 0x80, $"{topic} was granted");
        }
    }

    [Fact]
    public async Task Clients_cannot_publish_to_each_other()
    {
        string room = NewRoom();
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        await using TestClient mallory = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "mallory");
        await alice.Join(room);

        string fake = """{"t":"moved","room":"x/y","seq":1}""";
        Assert.False((await mallory.Publish(alice.InboxTopic, fake)).IsSuccess);
        Assert.False((await mallory.Publish("rooms/counter/abc/evt", fake)).IsSuccess);
        Assert.False((await mallory.Publish("some/other/topic", fake)).IsSuccess);
        Assert.False((await mallory.Publish($"rooms/{room}/cmd/add", """{"d":{"amount":1}}""", retain: true)).IsSuccess);

        // A command for a room mallory never joined is accepted by the broker but goes nowhere.
        Assert.True((await mallory.Publish($"rooms/{room}/cmd/add", """{"d":{"amount":1}}""")).IsSuccess);
        await Task.Delay(300);
        Assert.DoesNotContain(alice.Inbox(), m => m.Str("t") is "moved" or "changed");
    }

    [Fact]
    public async Task A_will_message_cannot_reach_another_client()
    {
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        TestClient mallory = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "mallory", configure: o => o
            .WithWillTopic(alice.InboxTopic)
            .WithWillPayload("""{"t":"$closed","room":"x/y","d":{"reason":"kicked"}}"""));
        mallory.Abort();
        await Task.Delay(500);
        Assert.Empty(alice.Inbox());
    }

    [Fact]
    public async Task Connections_that_do_not_follow_the_session_rules_are_refused()
    {
        string url = cluster.NodeA.MqttUrl;
        ConnectRefusedException shortId = await Assert.ThrowsAsync<ConnectRefusedException>(() => TestClient.ConnectAsync(url, "alice", clientId: "short"));
        Assert.Equal(MqttClientConnectResultCode.ClientIdentifierNotValid, shortId.Code);
        Assert.Contains("16 to 128", shortId.Reason);
        await Assert.ThrowsAsync<ConnectRefusedException>(() => TestClient.ConnectAsync(url, "alice", clientId: "$grainwire-0123456789"));
        await Assert.ThrowsAnyAsync<Exception>(() => TestClient.ConnectAsync(url, "alice", configure: o => o.WithProtocolVersion(MqttProtocolVersion.V311)));
        ConnectRefusedException noUser = await Assert.ThrowsAsync<ConnectRefusedException>(() => TestClient.ConnectAsync(url, "", configure: o => o.WithCredentials("", "x")));
        Assert.Equal(MqttClientConnectResultCode.BadUserNameOrPassword, noUser.Code);
    }

    [Fact]
    public async Task A_retried_command_is_applied_once()
    {
        string room = NewRoom();
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        await alice.Join(room);
        JsonElement first = await alice.Command(room, "add", new { amount = 2 }, commandId: "same-id-1");
        JsonElement retry = await alice.Request(room, "cmd/add", new() { ["d"] = new { amount = 2 } }, commandId: "same-id-1");
        Assert.Equal(2, first.GetProperty("d").GetInt32());
        Assert.Equal(2, alice.Inbox().Last(m => m.Str("t") == "$reply").GetProperty("d").GetInt32());
        Assert.Equal("ok", retry.Status());
        await Task.Delay(200);
        Assert.Single(alice.Of(room, "changed"));
    }

    [Fact]
    public async Task Fire_and_forget_commands_get_no_reply()
    {
        string room = NewRoom();
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        await alice.Join(room);
        await alice.Publish($"rooms/{room}/cmd/add", """{"cmd":"quiet","noreply":true,"d":{"amount":1}}""");
        await alice.WaitForType(room, "changed");
        Assert.DoesNotContain(alice.Inbox(), m => m.Str("t") == "$reply" && m.Str("cmd") == "quiet");
    }

    [Fact]
    public async Task A_player_who_drops_shows_as_reconnecting_and_gets_the_state_back_on_another_server()
    {
        string room = NewRoom();
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        TestClient bob = await TestClient.ConnectAsync(cluster.NodeB.MqttUrl, "bob");
        await alice.Join(room);
        await bob.Join(room, create: false);
        await alice.Command(room, "add", new { amount = 3 });

        bob.Abort();
        await alice.WaitFor(all => all.Any(m => IsPresence(m, "bob", "reconnecting")));
        await alice.Command(room, "add", new { amount = 4 });   // bob misses this one

        await using TestClient bobAgain = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "bob", clientId: bob.ClientId);
        JsonElement rejoined = await bobAgain.Join(room, create: false);
        Assert.Equal(("ok", "joined"), (rejoined.Status(), rejoined.Code()));
        JsonElement snapshot = Assert.Single(bobAgain.Of(room, "$snapshot"));
        Assert.Equal(7, snapshot.GetProperty("d").Num("value"));
        Assert.Equal(2, snapshot.Num("at"));
        await alice.WaitFor(all => all.Any(m => IsPresence(m, "bob", "online")));

        await alice.Command(room, "add", new { amount = 1 });
        JsonElement next = (await bobAgain.WaitForType(room, "changed")).Single();
        Assert.Equal(3, next.Num("seq"));
    }

    [Fact]
    public async Task When_the_grace_period_runs_out_the_others_are_told_the_player_left()
    {
        string room = NewRoom("quick");
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        TestClient bob = await TestClient.ConnectAsync(cluster.NodeB.MqttUrl, "bob");
        await alice.Join(room);
        await bob.Join(room, create: false);
        await bob.DisposeAsync();   // a clean disconnect counts the same

        await alice.WaitFor(all => all.Any(m => IsPresence(m, "bob", "reconnecting")));
        await alice.WaitFor(all => all.Any(m => IsPresence(m, "bob", "left")));
        Assert.Contains(alice.Of(room), m => m.Str("t") == "departed");
    }

    [Fact]
    public async Task A_kicked_connection_is_told_and_is_out_of_the_room()
    {
        string room = NewRoom();
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        await using TestClient bob = await TestClient.ConnectAsync(cluster.NodeB.MqttUrl, "bob");
        await alice.Join(room);
        await bob.Join(room, create: false);

        Assert.Equal("ok", (await alice.Command(room, "kick", new { player = "bob" })).Status());
        JsonElement closed = (await bob.WaitForType(room, "$closed")).Single();
        Assert.Equal("kicked", closed.GetProperty("d").Str("reason"));
        Assert.Equal("bye", closed.GetProperty("d").Str("message"));

        JsonElement after = await bob.Command(room, "add", new { amount = 1 });
        Assert.Equal(("error", "not-joined"), (after.Status(), after.Code()));
    }

    [Fact]
    public async Task Two_tabs_of_one_player_both_follow_the_room()
    {
        string room = NewRoom();
        await using TestClient tab1 = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        await using TestClient tab2 = await TestClient.ConnectAsync(cluster.NodeB.MqttUrl, "alice");
        await tab1.Join(room);
        await tab2.Join(room, create: false);
        await tab1.Command(room, "add", new { amount = 1 });
        JsonElement first = (await tab1.WaitForType(room, "changed")).Single();
        JsonElement second = (await tab2.WaitForType(room, "changed")).Single();
        Assert.Equal(first.Num("seq"), second.Num("seq"));
        Assert.DoesNotContain(tab1.Inbox(), m => m.Str("t") == "$presence");
    }

    [Fact]
    public async Task Leaving_detaches_only_that_connection()
    {
        string room = NewRoom();
        await using TestClient alice = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "alice");
        await using TestClient bob = await TestClient.ConnectAsync(cluster.NodeB.MqttUrl, "bob");
        await alice.Join(room);
        await bob.Join(room, create: false);

        JsonElement left = await bob.Request(room, "sys/leave");
        Assert.Equal(("ok", "left"), (left.Status(), left.Code()));
        await alice.WaitFor(all => all.Any(m => IsPresence(m, "bob", "reconnecting")));
        await alice.Command(room, "add", new { amount = 1 });
        await Task.Delay(200);
        Assert.Empty(bob.Of(room, "changed"));
    }

    [Fact]
    public async Task The_standalone_gateway_enforces_its_limits()
    {
        string url = cluster.Strict.MqttUrl;
        string room = NewRoom();
        await using TestClient carol = await TestClient.ConnectAsync(url, "carol");
        Assert.Equal("created", (await carol.Join(room)).Code());

        // Payload size.
        MqttClientPublishResult big = await carol.Publish($"rooms/{room}/cmd/add", "{\"d\":{\"amount\":1,\"pad\":\"" + new string('x', 600) + "\"}}");
        Assert.False(big.IsSuccess);

        // Rooms per connection (2).
        Assert.Equal("created", (await carol.Join(NewRoom())).Code());
        JsonElement third = await carol.Join(NewRoom());
        Assert.Equal(("rejected", "too-many-rooms"), (third.Status(), third.Code()));

        // Connections per player (2).
        await using TestClient second = await TestClient.ConnectAsync(url, "carol");
        ConnectRefusedException tooMany = await Assert.ThrowsAsync<ConnectRefusedException>(() => TestClient.ConnectAsync(url, "carol"));
        Assert.Equal(MqttClientConnectResultCode.QuotaExceeded, tooMany.Code);

        // Command rate: a burst of 5 (4 were used above), then refused, with a reply that says why.
        int refused = 0;
        for (int i = 0; i < 6; i++)
        {
            MqttClientPublishResult result = await carol.Publish($"rooms/{room}/cmd/add", "{\"cmd\":\"burst-" + i + "\",\"d\":{\"amount\":1}}");
            if (!result.IsSuccess)
            {
                refused++;
            }
        }

        Assert.True(refused >= 4, $"only {refused} publishes were refused");
        await carol.WaitFor(all => all.Any(m => m.Str("t") == "$reply" && m.Str("code") == "rate-limited"));
    }

    [Fact]
    public async Task A_player_can_only_create_so_many_rooms_per_minute_but_can_still_join()
    {
        string url = cluster.Strict.MqttUrl;
        string existing = NewRoom();
        await using TestClient owner = await TestClient.ConnectAsync(cluster.NodeA.MqttUrl, "owner");
        await owner.Join(existing);

        int created = 0;
        for (int i = 0; i < 4; i++)
        {
            // A fresh connection each time, so the per-connection limits do not get in the way.
            await using TestClient dave = await TestClient.ConnectAsync(url, "dave");
            JsonElement reply = await dave.Join(NewRoom());
            if (reply.Code() == "created")
            {
                created++;
            }
            else
            {
                Assert.Equal(("rejected", "rate-limited"), (reply.Status(), reply.Code()));
            }
        }

        Assert.Equal(3, created);
        await using TestClient again = await TestClient.ConnectAsync(url, "dave");
        Assert.Equal("joined", (await again.Join(existing)).Code());
    }

    // Requests that arrive together must not all pass one check of the limit.
    [Fact]
    public async Task Room_creations_that_arrive_together_cannot_exceed_the_limit_per_minute()
    {
        string url = cluster.Strict.MqttUrl;   // 3 creations per minute, 2 connections per player, 2 rooms per connection
        await using TestClient one = await TestClient.ConnectAsync(url, "frank");
        await using TestClient two = await TestClient.ConnectAsync(url, "frank");
        JsonElement[] replies = await Task.WhenAll(one.Join(NewRoom()), two.Join(NewRoom()), one.Join(NewRoom()), two.Join(NewRoom()));

        Assert.Equal(3, replies.Count(r => r.Code() == "created"));
        JsonElement refused = Assert.Single(replies, r => r.Code() != "created");
        Assert.Equal(("rejected", "rate-limited"), (refused.Status(), refused.Code()));
    }

    [Fact]
    public async Task A_burst_of_connections_cannot_exceed_the_limit_per_player()
    {
        string url = cluster.Strict.MqttUrl;   // 2 connections per player
        TestClient?[] clients = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            try
            {
                return await TestClient.ConnectAsync(url, "erin");
            }
            catch (Exception)
            {
                // Refused at CONNECT, or disconnected again before it could subscribe.
                return null;
            }
        }));

        try
        {
            await Task.Delay(500);
            Assert.Equal(2, clients.Count(c => c is { IsConnected: true }));
        }
        finally
        {
            foreach (TestClient? client in clients)
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                }
            }
        }

        // The count did not drift: once they are gone, the player has exactly two slots again.
        TestClient? first = null;
        for (int attempt = 0; first is null; attempt++)
        {
            try
            {
                first = await TestClient.ConnectAsync(url, "erin");
            }
            catch (ConnectRefusedException) when (attempt < 50)
            {
                await Task.Delay(100);
            }
        }

        await using TestClient firstAgain = first;
        await using TestClient second = await TestClient.ConnectAsync(url, "erin");
        ConnectRefusedException tooMany = await Assert.ThrowsAsync<ConnectRefusedException>(() => TestClient.ConnectAsync(url, "erin"));
        Assert.Equal(MqttClientConnectResultCode.QuotaExceeded, tooMany.Code);
    }

    [Fact]
    public void The_development_authenticator_refuses_to_run_in_production()
    {
        FakeEnvironment production = new(Environments.Production);
        Assert.Throws<InvalidOperationException>(() => new DevelopmentAuthenticator(production, new GrainWireGatewayOptions(), NullLogger<DevelopmentAuthenticator>.Instance));
        _ = new DevelopmentAuthenticator(production, new GrainWireGatewayOptions { AllowDevelopmentAuthenticatorInProduction = true }, NullLogger<DevelopmentAuthenticator>.Instance);
        _ = new DevelopmentAuthenticator(new FakeEnvironment(Environments.Development), new GrainWireGatewayOptions(), NullLogger<DevelopmentAuthenticator>.Instance);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
