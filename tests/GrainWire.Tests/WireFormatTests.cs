using System.Text;
using GrainWire.Internal;
using GrainWire.Protocol;
using GrainWire.Server.Internal;

namespace GrainWire.Tests;

/// <summary>The server's envelopes must match the example files in docs/protocol-golden byte for byte.</summary>
public class WireFormatTests
{
    private const string Room = "chess/abc123";

    private static string Golden(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", name)).Trim();

    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    private static DeliveryBatch Batch() => new() { Room = Room, Incarnation = "c1a0", Epoch = "7f30" };

    [Fact]
    public void Events_match_the_golden_files()
    {
        var moved = new DeliveryItem { Type = "moved", Data = Json("""{"from":"e2","to":"e4"}""") };
        Assert.Equal(Golden("event.json"), Encoding.UTF8.GetString(new EventTemplate(Batch(), moved).Render(42)));

        var ping = new DeliveryItem { Type = "ping" };
        Assert.Equal(Golden("event-no-data.json"), Encoding.UTF8.GetString(new EventTemplate(Batch(), ping).Render(43)));

        var presence = new DeliveryItem { Type = Wire.Types.Presence, Data = FrameworkJson.Serialize(new PresenceNotice("bob", "reconnecting", "black")) };
        Assert.Equal(Golden("presence.json"), Encoding.UTF8.GetString(new EventTemplate(Batch(), presence).Render(44)));
    }

    [Fact]
    public void Snapshots_match_the_golden_file()
    {
        var alice = new MemberInfo { Player = "alice", Role = "white", Status = PresenceStatus.Online };
        var snapshot = new SnapshotData
        {
            Incarnation = "c1a0",
            Epoch = "7f30",
            At = 41,
            View = Json("""{"value":3}"""),
            Members = [alice, new MemberInfo { Player = "bob", Status = PresenceStatus.Reconnecting }],
            You = alice,
        };
        Assert.Equal(Golden("snapshot.json"), Encoding.UTF8.GetString(Envelopes.Snapshot(Room, snapshot)));
    }

    [Fact]
    public void Replies_match_the_golden_files()
    {
        var ok = RoomReply.Ok(data: Json("""{"accepted":true}"""));
        ok.Incarnation = "c1a0";
        Assert.Equal(Golden("reply-ok.json"), Encoding.UTF8.GetString(Envelopes.Reply(Room, "0b6e", ok)));
        Assert.Equal(Golden("reply-rejected.json"), Encoding.UTF8.GetString(Envelopes.Reply(Room, "0b6e", RoomReply.Rejected(Wire.Codes.Rejected, "illegal move"))));
        Assert.Equal(Golden("reply-error.json"), Encoding.UTF8.GetString(Envelopes.Reply(Room, "0b6e", RoomReply.Error(Wire.Codes.RoomNotFound))));
    }

    [Fact]
    public void Closure_notices_match_the_golden_file()
    {
        var item = new DeliveryItem { Type = Wire.Types.Closed, Sequenced = false, Data = FrameworkJson.Serialize(new ClosedNotice("kicked", "bye")) };
        Assert.Equal(Golden("closed.json"), Encoding.UTF8.GetString(Envelopes.Notice(Batch(), item)));
        Assert.Equal(Golden("closed.json"), Encoding.UTF8.GetString(Envelopes.Closed(Room, "c1a0", "kicked", "bye")));
    }

    [Fact]
    public void Client_messages_in_the_golden_files_are_understood()
    {
        Assert.True(ClientMessage.TryParse(Json(Golden("client-join.json")), out var join));
        Assert.Equal("0b6e", join.CommandId);
        Assert.True(join.Create);
        Assert.Equal("""{"minutes":10}""", Encoding.UTF8.GetString(join.Options!));
        Assert.Equal("""{"role":"player"}""", Encoding.UTF8.GetString(join.JoinData!));

        Assert.True(ClientMessage.TryParse(Json(Golden("client-command.json")), out var command));
        Assert.Equal("c1a0", command.Incarnation);
        Assert.False(command.NoReply);
        Assert.Equal("""{"from":"e2","to":"e4"}""", Encoding.UTF8.GetString(command.Data!));

        Assert.True(ClientMessage.TryParse(Json(Golden("client-sync.json")), out var sync));
        Assert.Equal(("c1a0", "7f30", 42L), (sync.Incarnation, sync.Epoch, sync.Seq));
    }

    [Fact]
    public void Client_messages_tolerate_empty_payloads_and_refuse_non_objects()
    {
        Assert.True(ClientMessage.TryParse(ReadOnlyMemory<byte>.Empty, out var empty));
        Assert.Null(empty.CommandId);
        Assert.False(ClientMessage.TryParse(Json("[1,2]"), out _));
        Assert.False(ClientMessage.TryParse(Json("{not json"), out _));
        Assert.True(ClientMessage.TryParse(Json("""{"cmd":42,"d":null,"noreply":true}"""), out var odd));
        Assert.Null(odd.CommandId);
        Assert.Null(odd.Data);
        Assert.True(odd.NoReply);
    }

    [Theory]
    [InlineData("rooms/chess/abc/cmd/move", true)]
    [InlineData("rooms/chess/abc/sys/join", true)]
    [InlineData("rooms/chess/abc/evt", false)]
    [InlineData("rooms/chess/abc/cmd/move/extra", false)]
    [InlineData("rooms/chess/+/cmd/move", false)]
    [InlineData("rooms/chess/#", false)]
    [InlineData("rooms/ch ess/abc/cmd/move", false)]
    [InlineData("clients/abc/inbox", false)]
    [InlineData("rooms//abc/cmd/move", false)]
    [InlineData("$SYS/broker", false)]
    public void Only_room_command_topics_are_accepted(string topic, bool valid)
    {
        Assert.Equal(valid, ClientTopic.TryParse(topic, out var parsed));
        if (valid) Assert.Equal(new RoomAddress("chess", "abc"), parsed.Room);
    }
}
