# Guarantees

What GrainWire promises about ordering, delivery and commands, and what it does not.

## Events

1. **Ordered and duplicate-free.** Within a room, a client app sees each player's events in
   order, never twice, with public and private events ordered together.
2. **Eventual state convergence.** Every attached client converges to the room's current state
   within a bounded time: by default about the sync interval (15 s) plus one round trip, even if
   the last event was lost and the room has gone quiet.
3. **Not guaranteed: delivery of every individual event.** If events are lost, the client
   recovers by snapshot, which reflects their *effects* but not each event. Apps that need every
   message (chat history, for example) put it in room state, so it is in the snapshot.

In practice, events are lost only around failures: a connection that drops, a gateway or silo
that stops. The point of guarantee 3 is that your app must treat `onSnapshot` as "this is the
state now", and not assume it has seen every event that led there.

### How

- Every event carries a per-player sequence number. The client holds back anything that arrives
  early, drops anything it already has, and asks for a snapshot if a gap does not close within
  a quarter of a second.
- A room that has been silent for 15 seconds is checked against the server. This catches a lost
  *last* event, which no gap would ever reveal.
- A snapshot says which sequence number it corresponds to, so events that arrive around it are
  neither repeated nor skipped.
- When a room restarts on the server, its stream starts a new epoch. Clients notice and wait
  for a snapshot.

## Commands

- **Commit, then publish.** Nothing a command sends leaves the room before the command has
  committed. If the handler rejects, throws, or the save fails, the state is restored and
  nothing is sent. A client never sees a change that the room later forgets.
- **Every request has an outcome.** `ok`, `rejected` or `error`, including for handlers that
  return nothing.
- **`ok` means committed** under the room's persistence mode: saved, for a persistent room.
- **A retry never runs twice.** The client re-sends a request that has no reply with the same
  command id. The room remembers the outcome of each command id (at least the last 256 per
  player, and everything from the last 10 minutes) and returns it instead of running the
  command again. In a persistent room this memory is saved with the room.
- **A timeout means "unknown", not "rejected".** If no reply arrives in time, the client
  reports `OutcomeUnknownError`. The command may have been applied; sync to find out.
- **Commands name the room they are meant for.** A command queued for a room that was since
  closed and re-created under the same id is refused (`stale-room`).

## What can still be lost

| Situation | What happens |
|---|---|
| The server crashes after saving a command but before sending its events | Clients catch up by snapshot. A retried request gets `ok` from the recorded outcome. |
| An in-memory room's server fails | The room is lost. Clients are told (`onClosed`, reason `lost`). |
| A persistent room's server fails | The room carries on elsewhere from its last save. Clients get a snapshot without reconnecting. Nobody is removed. |
| A gateway fails | Its clients reconnect to another gateway and get a snapshot. Until they do, others see them as "reconnecting". |
| A client is too slow to keep up | It is disconnected once too many messages are waiting, reconnects, and gets a snapshot. |

## Presence

Presence is accurate when connections close normally. When a gateway dies without notice, the
room notices within its lease timeout (25 s by default) and then shows that gateway's players as
"reconnecting". The grace period starts from that moment.

## What GrainWire is not for

- **Fast action games** that need many state updates per second with client-side prediction.
- **A durable message log.** Events are not stored. Keep history in room state, or in your own
  store.
- **Rooms much larger than about 1,000 members.** See [benchmarks](benchmarks.md).
