# GrainWire

Real-time rooms for .NET. **MQTT** carries the connections; an **Orleans grain** owns each
room's state.

You write one class with your room's rules. GrainWire handles topics, delivery across servers,
ordering, presence, reconnecting, and recovery when something fails. It is for turn-based and
event-driven apps: board and card games, chat rooms, collaborative tools.

> **Status: preview.** The packages are not on NuGet or npm yet. The API may still change.

```csharp
[RoomType("chess")]
public class ChessGameGrain : RoomGrain<GameState>
{
    [OnMessage("move", Roles = "white,black")]
    public void Move(PlayerId player, MoveRequest request)
    {
        if (!State.IsLegal(player, request)) Reject("That move is not legal.");
        State.Apply(request);
        Broadcast("moved", request);     // sent only after the move is committed
    }
}
```

```ts
const client = await connect({ url: "wss://example.com/mqtt", token });
const room = await client.join<GameView>("chess", gameId);

room.onSnapshot((view) => render(view));              // after joining, reconnecting, or catching up
room.on("moved", (move) => applyMove(move));          // in order, each exactly once
room.onPresence((p) => showStatus(p.player, p.status)); // "online" | "reconnecting" | "left"

await room.request("move", { from: "e2", to: "e4" }); // resolves once the move is committed
```

## What you get

- **One grain per room.** State is isolated per room and changed one command at a time, so
  handlers need no locks.
- **Commit, then publish.** If a handler rejects, throws, or the save fails, the state is
  restored and nothing is sent. Clients never see a change the room later forgets.
- **Ordered events.** Each player gets one ordered stream per room, public and private events
  together, with no duplicates.
- **Presence and reconnection.** Others see a dropped player as "reconnecting". When they come
  back, even on another server, they get the current state. A quiet room is checked against the
  server, so a lost last message cannot leave a client stuck.
- **Safe retries.** A request without a reply is re-sent with the same id and never runs twice.
- **Recovery from server failures.** A saved room carries on from another server without
  anyone reconnecting, and nobody loses their seat because a server failed.
- **Two ways to host.** The MQTT endpoint runs inside your Orleans silo, or in separate gateway
  processes. Your room code is the same.

## Packages

| Package | For |
|---|---|
| `GrainWire.Server` (NuGet) | The host: the MQTT gateway, plus everything below. |
| `GrainWire.Abstractions` (NuGet) | Projects that only contain room grains: `RoomGrain<TState>` and silo registration. |
| `@grainwire/client` (npm) | Browsers. TypeScript, built on MQTT.js. |

Requires .NET 10 and Orleans 10. The broker is [MQTTnet](https://github.com/dotnet/MQTTnet) 5.

## Documentation

- [Getting started](docs/getting-started.md): a chat room in fifteen minutes
- [Rooms](docs/rooms.md): commands, hooks, roles, private state, deadlines, saving
- [Guarantees](docs/guarantees.md): what is promised about ordering, delivery and commands
- [Security](docs/security.md): authentication, limits, a checklist for going live
- [Hosting](docs/hosting.md): one process, a cluster, or separate gateways
- [Wire protocol](docs/protocol.md): for building other clients
- [Benchmarks](docs/benchmarks.md)
- [Chess sample](samples/Chess/README.md): a complete small game with a browser client

## Limits of v1

- Rooms of up to about 1,000 members.
- Not for fast action games that need many state updates per second.
- Events are not stored. A client that missed events catches up from a snapshot of the state.
- Browser client only. A .NET client is planned; the [protocol](docs/protocol.md) is documented
  so that other clients can be written.

## Building

```bash
dotnet test
```

```bash
cd clients/ts && npm ci && npm test
```

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
