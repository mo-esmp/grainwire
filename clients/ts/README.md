# @grainwire/client

Browser client for [GrainWire](https://github.com/mo-esmp/grainwire): real-time rooms over MQTT,
with each room's state owned by an Orleans grain on the server.

```ts
import { connect, RejectedError, OutcomeUnknownError } from "@grainwire/client";

const client = await connect({ url: "wss://example.com/mqtt", token });
const room = await client.join<ChessView>("chess", gameId, { create: true });

room.onSnapshot((view) => render(view));               // after joining, reconnecting, or catching up
room.on<Move>("moved", (move) => applyMove(move));     // in order, each exactly once
room.onPresence((p) => showStatus(p.player, p.status)); // "online" | "reconnecting" | "left"
room.onClosed((c) => showClosed(c.reason));            // "closed" | "kicked" | "removed" | "lost"

try {
  await room.request("move", { from: "e2", to: "e4" }); // resolves once the move is committed
} catch (error) {
  if (error instanceof RejectedError) showMessage(error.reason);   // the room refused it; nothing changed
  else if (error instanceof OutcomeUnknownError) await room.sync(); // no reply in time: look at the state
  else throw error;
}
```

## What it does for you

- **Ordering.** Events of a room arrive in order, each exactly once, public and private together.
- **Catching up.** After a reconnect, a lost message, or a server restart, the room gets a fresh
  snapshot (`onSnapshot`) and events continue from there. A quiet room is checked against the
  server every 15 seconds, so a lost last event cannot leave you stuck.
- **Safe retries.** A request without a reply is re-sent with the same command id, so it never
  runs twice. If no reply arrives in time you get `OutcomeUnknownError`, never a false "rejected".
- **Reconnecting.** With back-off; rooms are re-joined and requests in flight are re-sent.
- **No offline queue.** While disconnected, `request()` and `send()` fail fast with
  `NotConnectedError`, so your UI can say so.

Events are not applied to `room.view` for you: `view` is the latest snapshot. Apply events to
your own state in the `on(...)` handlers, and replace it in `onSnapshot`.

## Errors from `request()`

| Error | Meaning |
|---|---|
| `RejectedError` (`code`, `reason`) | The room refused the command. Nothing changed. |
| `RoomError` (`code`, `reason`) | It could not be processed (`handler-error`, `not-committed`, `room-not-found` ...). Nothing changed. |
| `OutcomeUnknownError` | No reply in time. The command may or may not have been applied. |
| `NotConnectedError` | Not sent: there is no connection right now. |
| `RoomClosedError` | The room is closed for this client. |

## Options

| Option | Default | |
|---|---|---|
| `url` | | The gateway endpoint, e.g. `wss://example.com/mqtt` |
| `token` | | Access token, or a function returning one (called again before each reconnect) |
| `username` | `"token"` | MQTT user name; its meaning is up to your server's authenticator |
| `clientId` | per-tab random id | Kept in `sessionStorage`, so a reconnect is recognised as the same tab |
| `syncIntervalMs` | 15000 | How long a room may be silent before it is checked against the server |
| `gapWaitMs` | 250 | How long to wait for a missing event before asking for a snapshot |
| `requestTimeoutMs` | 15000 | How long `request()` waits before `OutcomeUnknownError` |
| `retryIntervalMs` | 3000 | How often a request without a reply is re-sent |
| `reconnectDelayMs` / `maxReconnectDelayMs` | 500 / 15000 | Reconnect back-off |

The wire protocol is documented in
[docs/protocol.md](https://github.com/mo-esmp/grainwire/blob/main/docs/protocol.md).

## License

MIT
