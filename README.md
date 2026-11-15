# GrainWire

Real-time rooms for .NET: **MQTT** carries the connections, an **Orleans grain** owns each
room's state.

You write one grain class with your room's rules. GrainWire handles topics, delivery across
servers, presence, reconnection and failure recovery.

> Status: early preview, not yet published. See [the design](docs/specs/2026-11-15-grainwire-design.md)
> and [the build plan](docs/plans/2026-11-15-grainwire-implementation-plan.md).

```csharp
[RoomType("chess")]
public class ChessGameGrain : RoomGrain<ChessState>
{
    [OnMessage("move")]
    public void OnMove(PlayerId player, MoveDto move)
    {
        if (!State.IsLegal(player, move)) Reject("illegal move");
        State.Apply(move);
        Broadcast("moved", move);
    }
}
```

```ts
const client = await connect({ url: "wss://example/mqtt", token });
const room = await client.join<ChessView>("chess", gameId);
room.on("moved", move => applyMove(move));
await room.request("move", { from: "e2", to: "e4" });
```

## License

MIT
