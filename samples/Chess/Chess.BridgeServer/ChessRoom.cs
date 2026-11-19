using System.Text.Json.Nodes;
using ChessBridge.Server.Bridge;
using GrainWire;

namespace ChessBridge.Server;

/// <summary>
/// The chess room. The rules live in the Python service (samples/Chess/rules-python); this class only lists the
/// commands and who may send them. Same room type and messages as Chess.Server, so both chess clients work unchanged.
/// </summary>
[RoomType("chess")]
public sealed class ChessRoom(RulesClient rules) : ExternalRoomGrain(rules)
{
    [OnMessage("move", Roles = "white,black")]
    public Task Move(PlayerId player, JsonNode? move) => Forward("move", player, move);

    [OnMessage("resign", Roles = "white,black")]
    public Task Resign(PlayerId player) => Forward("resign", player, null);
}
