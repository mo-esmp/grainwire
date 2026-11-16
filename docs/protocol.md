# GrainWire wire protocol (v1)

This is the contract between a client and a GrainWire gateway. The TypeScript client implements
it; a .NET client or any third-party client can be built from this document alone.

Example messages live in [`protocol-golden/`](protocol-golden/). Both the server tests and the
TypeScript client tests read those files, so they cannot drift from this document unnoticed.

## 1. Transport and session

| Setting | Value |
|---|---|
| Protocol | MQTT **5** over WebSockets (sub-protocol `mqtt`). MQTT 3.x is refused. |
| Endpoint | `ws(s)://host/mqtt` by default |
| Client id | 16–128 characters of `A-Z a-z 0-9 - _`. Random per browser tab, reused when that tab reconnects. Never share it: it names the inbox. |
| Credentials | `username` / `password` of CONNECT. The password field carries the access token. What they mean is up to the server's authenticator. |
| Clean Start | `true`. Session Expiry `0`. The broker keeps no session state. |
| Publish QoS (client → server) | 1 |
| Delivery QoS (server → client) | 0 |
| Retained messages | refused |
| Will messages | treated like any other publish from that client (section 2); they can never reach another client |

On success, CONNACK carries the user property `gw-player` with the player id the server derived
from the credentials. A refused CONNECT carries a reason code and reason string.

## 2. Topics

A client may **subscribe** to exactly one topic, its own inbox:

```
clients/{clientId}/inbox
```

Any other subscription (another inbox, wildcards, shared subscriptions) is refused with
`NotAuthorized`.

A client may **publish** only to:

```
rooms/{type}/{id}/sys/{join|leave|sync}     framework requests
rooms/{type}/{id}/cmd/{name}                 app commands
```

`type`, `id` and `name` are 1–128 characters of `A-Z a-z 0-9 _ . ~ -`. Anything else a client
publishes is refused in the PUBACK (reason code and string). Nothing a client publishes is ever
routed to another client.

## 3. Messages from client to server

The payload is a JSON object. Every field is optional; an empty payload means `{}`.

| Field | Used by | Meaning |
|---|---|---|
| `cmd` | all | Command id chosen by the client (a UUID). Echoed in the reply. A retry **must reuse it** (section 6). |
| `noreply` | commands | `true`: do not send a reply (fire-and-forget). |
| `ri` | commands, `sync` | The room incarnation the client believes it is talking to. |
| `d` | commands | The command payload (any JSON). |
| `create` | `join` | `true`: create the room if it does not exist. |
| `options` | `join` | Room settings for creation (any JSON). |
| `data` | `join` | App-defined join data, such as a requested role or an invite code. |
| `se`, `seq` | `sync` | The stream epoch and position the client has processed. |

Examples: [`client-join.json`](protocol-golden/client-join.json),
[`client-command.json`](protocol-golden/client-command.json),
[`client-sync.json`](protocol-golden/client-sync.json).

## 4. Messages from server to client (the inbox)

Every inbox message is a JSON object with a type `t` and the room `room` (`"type/id"`).
Types starting with `$` belong to the framework; every other `t` is an app event.

### 4.1 Events

```json
{"t":"moved","room":"chess/abc123","ri":"c1a0","se":"7f30","seq":42,"d":{"from":"e2","to":"e4"}}
```

| Field | Meaning |
|---|---|
| `ri` | **Room incarnation.** Created with the room, stable for its lifetime. It changes only when a room is destroyed and another is created under the same id. |
| `se` | **Stream epoch.** Changes whenever the room is restarted on the server. |
| `seq` | Position in **this player's** event stream for this room, within the epoch. It counts every event the player is entitled to (public, private, presence), with no gaps. All of a player's tabs see the same numbers. |
| `d` | Event payload. Absent when the event has none. |

`$presence` is an ordinary sequenced event about another member (or about yourself, when your
role changes):

```json
{"t":"$presence","room":"chess/abc123","ri":"c1a0","se":"7f30","seq":44,"d":{"player":"bob","status":"reconnecting","role":"black"}}
```

`status` is `online`, `reconnecting` or `left`. A player you have not seen before with status
`online` has just joined. `left` means they are no longer a member.

### 4.2 Snapshots

```json
{"t":"$snapshot","room":"chess/abc123","ri":"c1a0","se":"7f30","at":41,"d":{"value":3},
 "members":[{"player":"alice","role":"white","status":"online"},{"player":"bob","status":"reconnecting"}],
 "you":{"player":"alice","role":"white","status":"online"}}
```

A snapshot is a **baseline**: the state as this player may see it (`d`), the member list, and
`at`, the `seq` it corresponds to. A snapshot does **not** consume a sequence number: the next
event after it has `seq = at + 1`.

### 4.3 Replies

```json
{"t":"$reply","room":"chess/abc123","cmd":"0b6e","status":"ok","ri":"c1a0","d":{"accepted":true}}
```

| Field | Meaning |
|---|---|
| `cmd` | The command id of the request. |
| `status` | `ok`: done and committed. `rejected`: refused, nothing changed. `error`: could not be processed, nothing changed. |
| `code` | Machine-readable detail (table below). |
| `reason` | Human-readable text, when there is one. |
| `d` | The handler's return value (commands only, when it returns one). |

Replies are not part of the `seq` stream. A reply and the events caused by the same command
may arrive in either order.

| `status` | `code` | Meaning |
|---|---|---|
| ok | `created`, `joined` | Join succeeded (and created the room). A snapshot was sent just before. |
| ok | `in-sync` | Sync: the client is up to date. |
| ok | `resynced` | Sync: the client was behind; a snapshot was sent just before. |
| ok | `left` | Leave succeeded. |
| rejected | `rejected` or an app-defined code | A handler refused the command. |
| rejected | `create-denied`, `join-rejected`, `room-full` | Join refused. |
| rejected | `forbidden` | The player's role may not send this command. |
| rejected | `bad-payload` | The payload could not be read. |
| rejected | `rate-limited`, `too-many-requests`, `too-many-rooms` | A limit was hit. Try again later. |
| error | `room-not-found` | The room does not exist (any more). |
| error | `unknown-room-type` | No such room type. |
| error | `not-member` | The player is not a member (any more). |
| error | `not-joined` | This connection has not joined the room on this gateway. Join again. |
| error | `stale-room` | `ri` does not match: the command was meant for an earlier room with the same id. |
| error | `unknown-command` | The room has no such command. |
| error | `handler-error` | The handler failed; the state was rolled back. |
| error | `not-committed` | The change could not be saved and was rolled back. Safe to retry. |
| error | `unavailable` | The room could not be reached. The outcome is unknown; retry with the same `cmd`. |

### 4.4 Closure notices

```json
{"t":"$closed","room":"chess/abc123","ri":"c1a0","d":{"reason":"kicked","message":"bye"}}
```

`reason`: `closed` (the room was closed), `kicked`, `removed` (no longer a member), or `lost`
(the room's state was lost on the server). Not sequenced. After it, this connection is no longer
in the room.

## 5. What a client must do

**Joining.** Publish `sys/join`. On `ok`, the snapshot has already arrived. Remember `ri`.

**Processing the inbox, per room:**

1. A snapshot replaces local state. Set `epoch = se`, `position = at`. Drop buffered events with
   the same `se` and `seq <= at`; apply the rest in order.
2. An event with the current `se`:
   - `seq <= position`: duplicate, drop.
   - `seq == position + 1`: apply, advance.
   - otherwise: a gap. Buffer the event and wait briefly (about 250 ms) for the missing ones; if
     they do not arrive, publish `sys/sync`.
3. An event with a different `se`, or before any snapshot: buffer it and wait for a snapshot (publish
   `sys/sync` if none arrives).
4. A message with a different `ri`, a `$closed`, or a `room-not-found` / `not-member` reply:
   the room is gone for this client.

**Idle sync.** If a joined room has been silent for the sync interval (default 15 s), publish
`sys/sync` with `ri`, `se` and `seq`. The server answers `in-sync` or sends a snapshot. This is
what catches a lost final event.

**Reconnecting.** After the connection is re-established, publish `sys/join` again for every
room, and re-send every request that has no reply yet, **with its original `cmd`**.

## 6. Requests and retries

- Every request without `noreply` gets exactly one of `ok`, `rejected`, `error`.
- `ok` means the command was committed under the room's persistence mode.
- The server remembers the outcome of each `cmd` per player (at least the last 256, and
  everything from the last 10 minutes). A retry with the same `cmd` returns the recorded outcome
  and does not run the command again.
- If no reply arrives in time, the outcome is **unknown**, not "rejected". Sync to learn the state.
- Only successful commands are recorded. A retried rejected command is simply evaluated again.

## 7. Guarantees

1. **Ordered and duplicate-free.** Within a room, a client that follows section 5 sees its
   events in `seq` order, never twice, public and private together.
2. **Eventual state convergence.** Every attached client converges to the room's current state
   within about one sync interval, even if events were lost.
3. **Not guaranteed: delivery of every individual event.** Lost events are repaired by a
   snapshot, which carries their effects. Apps that need every message put it in room state.
