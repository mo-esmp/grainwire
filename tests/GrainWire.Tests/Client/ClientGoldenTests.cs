using System.Text;
using System.Text.Json;
using GrainWire.Client;
using GrainWire.Client.Internal;
using GrainWire.Client.Protocol;

namespace GrainWire.Tests.Client;

/// <summary>
/// The .NET client against the example messages in docs/protocol-golden: it must understand every message a server
/// sends, and publish exactly what the client examples show.
/// </summary>
public class ClientGoldenTests
{
    private const string Room = "chess/abc123";
    private static readonly string s_directory = Path.Combine(AppContext.BaseDirectory, "golden");

    /// <summary>What the client publishes for each client-to-server example.</summary>
    private static readonly Dictionary<string, Func<byte[]>> s_clientMessages = new()
    {
        ["client-join.json"] = () => ClientPayload.Join("0b6e", create: true, Json("""{"minutes":10}"""), Json("""{"role":"player"}""")),
        ["client-command.json"] = () => ClientPayload.Command("0b6e", "c1a0", Json("""{"from":"e2","to":"e4"}"""), noReply: false),
        ["client-sync.json"] = () => ClientPayload.Sync("0b6e", "c1a0", "7f30", 42),
    };

    /// <summary>What the client must read from each server-to-client example.</summary>
    private static readonly Dictionary<string, Action<InboxEnvelope>> s_serverMessages = new()
    {
        ["snapshot.json"] = envelope =>
        {
            SnapshotEnvelope snapshot = Assert.IsType<SnapshotEnvelope>(envelope);
            Assert.Equal(("$snapshot", Room, "c1a0", "7f30", 41L), (snapshot.Type, snapshot.Room, snapshot.Incarnation, snapshot.Epoch, snapshot.At));
            Assert.Equal(3, snapshot.Data.GetProperty("value").GetInt32());
            Assert.Equal<Member>([new Member("alice", "white", MemberStatus.Online), new Member("bob", null, MemberStatus.Reconnecting)], snapshot.Members);
            Assert.Equal(new Member("alice", "white", MemberStatus.Online), snapshot.You);
        },
        ["event.json"] = envelope =>
        {
            EventEnvelope @event = Assert.IsType<EventEnvelope>(envelope);
            Assert.Equal(("moved", Room, "c1a0", "7f30", 42L), (@event.Type, @event.Room, @event.Incarnation, @event.Epoch, @event.Seq));
            Assert.Equal("""{"from":"e2","to":"e4"}""", @event.Data.GetRawText());
        },
        ["event-no-data.json"] = envelope =>
        {
            EventEnvelope @event = Assert.IsType<EventEnvelope>(envelope);
            Assert.Equal(("ping", 43L), (@event.Type, @event.Seq));
            Assert.Equal(JsonValueKind.Undefined, @event.Data.ValueKind);
        },
        ["presence.json"] = envelope =>
        {
            EventEnvelope @event = Assert.IsType<EventEnvelope>(envelope);
            Assert.Equal(("$presence", 44L), (@event.Type, @event.Seq));
            Assert.Equal(new PresenceChange("bob", MemberStatus.Reconnecting, "black"), InboxParser.Presence(@event.Data));
        },
        ["reply-ok.json"] = envelope =>
        {
            ReplyEnvelope reply = Assert.IsType<ReplyEnvelope>(envelope);
            Assert.Equal((Room, "0b6e", ReplyStatus.Ok, (string?)null, "c1a0"), (reply.Room, reply.CommandId, reply.Status, reply.Code, reply.Incarnation));
            Assert.True(reply.Data.GetProperty("accepted").GetBoolean());
            Assert.Null(GrainWireClient.ToException(reply));
        },
        ["reply-rejected.json"] = envelope =>
        {
            ReplyEnvelope reply = Assert.IsType<ReplyEnvelope>(envelope);
            Assert.Equal(("0b6e", ReplyStatus.Rejected, "rejected", "illegal move"), (reply.CommandId, reply.Status, reply.Code, reply.Reason));
            RejectedException rejected = Assert.IsType<RejectedException>(GrainWireClient.ToException(reply));
            Assert.Equal(("rejected", "illegal move", "illegal move"), (rejected.Code, rejected.Reason, rejected.Message));
        },
        ["reply-error.json"] = envelope =>
        {
            ReplyEnvelope reply = Assert.IsType<ReplyEnvelope>(envelope);
            Assert.Equal(("0b6e", ReplyStatus.Error, "room-not-found", (string?)null), (reply.CommandId, reply.Status, reply.Code, reply.Reason));
            RoomException error = Assert.IsType<RoomException>(GrainWireClient.ToException(reply));
            Assert.Equal("room-not-found", error.Code);
        },
        ["closed.json"] = envelope =>
        {
            ClosedEnvelope closed = Assert.IsType<ClosedEnvelope>(envelope);
            Assert.Equal((Room, "c1a0", RoomClosedReason.Kicked, "bye"), (closed.Room, closed.Incarnation, closed.Reason, closed.Message));
        },
    };

    public static TheoryData<string> GoldenFiles() => [.. Directory.GetFiles(s_directory, "*.json").Select(path => Path.GetFileName(path)!).Order()];

    private static string Golden(string name) => File.ReadAllText(Path.Combine(s_directory, name)).Trim();

    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>Runs for every file in the directory, so a new example fails here until the client covers it.</summary>
    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Every_example_message_is_read_or_produced_exactly(string name)
    {
        if (name.StartsWith("client-", StringComparison.Ordinal))
        {
            Assert.True(s_clientMessages.TryGetValue(name, out Func<byte[]>? produce), $"No client payload is checked against {name}.");
            Assert.Equal(Golden(name), Encoding.UTF8.GetString(produce()));
        }
        else
        {
            Assert.True(s_serverMessages.TryGetValue(name, out Action<InboxEnvelope>? check), $"Nothing checks how the client reads {name}.");
            Assert.True(InboxEnvelope.TryParse(Json(Golden(name)), out InboxEnvelope? envelope), $"The client could not read {name}.");
            check(envelope);
        }
    }

    [Fact]
    public void The_examples_flow_through_the_ordering_engine()
    {
        // Snapshot at 41, then events 42, 43, 44.
        OrderingEngine engine = new(new OrderingOptions(), 0);
        Assert.True(InboxEnvelope.TryParse(Json(Golden("snapshot.json")), out InboxEnvelope? snapshot));
        Assert.IsType<SnapshotAction>(Assert.Single(engine.ReceiveSnapshot((SnapshotEnvelope)snapshot, 0)));

        foreach (string name in (string[])["event.json", "event-no-data.json", "presence.json"])
        {
            Assert.True(InboxEnvelope.TryParse(Json(Golden(name)), out InboxEnvelope? @event));
            Assert.IsType<EventAction>(Assert.Single(engine.ReceiveEvent((EventEnvelope)@event, 1)));
        }

        Assert.Equal(new StreamPosition("c1a0", "7f30", 44), engine.Current);
    }

    [Fact]
    public void Fields_without_a_value_are_left_out_of_what_the_client_publishes()
    {
        Assert.Equal("""{"cmd":"c1","create":false}""", Encoding.UTF8.GetString(ClientPayload.Join("c1", create: false, null, null)));
        Assert.Equal("""{"cmd":"c1"}""", Encoding.UTF8.GetString(ClientPayload.Command("c1", null, null, noReply: false)));
        Assert.Equal("""{"cmd":"c1","noreply":true,"ri":"r","d":5}""", Encoding.UTF8.GetString(ClientPayload.Command("c1", "r", Json("5"), noReply: true)));
        Assert.Equal("""{"cmd":"c1","seq":-1}""", Encoding.UTF8.GetString(ClientPayload.Sync("c1", null, null, -1)));
        Assert.Equal("""{"cmd":"c1"}""", Encoding.UTF8.GetString(ClientPayload.Leave("c1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"room":"chess/abc123"}""")]
    [InlineData("""{"t":"moved"}""")]
    [InlineData("""{"t":"moved","room":"chess/abc123","ri":"c1a0","se":"7f30"}""")]
    [InlineData("""{"t":"moved","room":"chess/abc123","seq":1}""")]
    [InlineData("""{"t":"$snapshot","room":"chess/abc123","ri":"c1a0","se":"7f30","at":1}""")]
    public void Messages_the_client_cannot_place_are_refused_instead_of_guessed_at(string payload) =>
        Assert.False(InboxEnvelope.TryParse(Json(payload), out _));

    [Fact]
    public void Names_and_client_ids_are_checked_before_anything_is_sent()
    {
        Wire.EnsureName("chess", "type");
        Wire.EnsureName("a.b~c_d-9", "id");
        Assert.Throws<ArgumentException>(() => Wire.EnsureName("", "type"));
        Assert.Throws<ArgumentException>(() => Wire.EnsureName("a/b", "id"));
        Assert.Throws<ArgumentException>(() => Wire.EnsureName("a b", "name"));
        Assert.Throws<ArgumentException>(() => Wire.EnsureName(new string('x', 129), "id"));

        Assert.True(Wire.IsClientId("0123456789abcdef"));
        Assert.True(Wire.IsClientId("ABC-def_0123456789"));
        Assert.False(Wire.IsClientId("short"));
        Assert.False(Wire.IsClientId("0123456789abcde."));
        Assert.False(Wire.IsClientId(new string('x', 129)));
        Assert.Equal("clients/0123456789abcdef/inbox", Wire.InboxTopic("0123456789abcdef"));
        Assert.Equal("rooms/chess/abc123/cmd/move", Wire.RoomTopic(Room, "cmd/move"));
    }
}
