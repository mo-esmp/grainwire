# GrainWire — Real-time rooms on Orleans + MQTTnet — Design

Date: 2026-11-15
Status: **Approved v2** (2026-11-15). v1 was revised after a builder check and an external design review; see section 11.
Name: **GrainWire** (owner's decision; repo `grainwire`, NuGet `GrainWire.*`, npm `@grainwire/client`)

## 1. Intent

An open-source framework, shipped as NuGet packages plus an npm package, for building
real-time, room-based applications on .NET.

- **MQTTnet** owns client connections (MQTT 5 over WebSockets).
- **Orleans grains** own the data of each *room* (a chess match, a chat channel). A room's
  state lives in exactly one grain, is isolated from other rooms, and is changed one
  command at a time.

The framework exists so that an application developer writes **only room logic**, as a
grain class. They never deal with topic design, cross-server delivery, presence,
reconnection, or failure recovery. It is for **other developers**, not only the owner, so the
public API, its guarantees, documentation and samples are the product.

**Product claim (v1):** *authoritative room state* for turn-based and event-driven apps:
board and card games, turn-based games, chat rooms whose history lives in room state, and
collaborative tools. It is **not** a netcode engine for fast action games, and **not** a
durable message log (see section 3).

### What the developer experience should feel like

```csharp
[RoomType("chess")]
public class ChessGameGrain : RoomGrain<ChessState>
{
    protected override Task<JoinResult> OnJoin(PlayerId player, JoinData data) => ...;  // accept/reject, assign seat

    [OnMessage("move")]
    public async Task OnMove(PlayerId player, MoveDto move)
    {
        if (!State.IsLegal(player, move)) Reject("illegal move");  // ends the command, nothing changes
        State.Apply(move);
        Broadcast("moved", move);   // queued; released only after the command commits
    }                               // returning normally = success reply to the caller

    protected override object GetSnapshot(PlayerId viewer) => State.ViewFor(viewer);
}
```

```ts
const client = await connect({ url: "wss://example/mqtt", token });
const room = await client.join<ChessView>("chess", gameId);
room.onSnapshot(view => render(view));
room.on<MoveDto>("moved", move => applyMove(move));
room.onPresence(p => showStatus(p.player, p.status));   // "online" | "reconnecting" | "left"
room.onClosed(c => showClosed(c.reason));               // "closed" | "kicked" | "lost"
await room.request("move", { from: "e2", to: "e4" });  // resolves once committed; rejects on refusal
```

## 2. Goals

1. A developer builds a working multiplayer room by writing one grain class and a browser
   client with the TypeScript package. They need no knowledge of Orleans internals or MQTT
   topic design.
2. Works identically on one server or a multi-server Orleans cluster.
3. **Presence and reconnection are built in.** When a player drops, others see
   "reconnecting". When the player comes back, possibly on a different server, they get the full
   current room state automatically. Rooms also recover when a *server* fails while browsers
   stay connected.
4. **Trustworthy commands:** clients never see a change that was not committed. A retried
   command never runs twice. Every request ends in success, rejection, or an explicit "outcome
   unknown".
5. Hosting is a switch: the MQTT endpoint runs **inside the Orleans silo** (default) or in a
   **separate gateway process** that is an Orleans client, with no change to app code.
6. Extensible where real apps differ: authentication, message serialization, grain state
   persistence.
7. Chess ships as the reference sample (server + browser client) and is the first thing a
   new user reads.

## 3. Non-goals (v1)

- .NET client library. It is planned for later, so the protocol spec must be complete enough
  to build it from.
- External-broker mode (EMQX/Mosquitto/HiveMQ). Possible later as a community adapter.
- Matchmaking, lobbies, friend lists, accounts. The chess sample uses "create game → share link".
- **Fast action games** that need high-rate state sync (tick-based netcode).
- **Guaranteed delivery of every individual event**, and durable unbounded message logs. The
  guarantee is *eventual state convergence* (7.6). Apps that need history keep it in room
  state (bounded) or their own store.
- Rooms larger than ~1,000 members (7.17).
- Generating TypeScript types from C# DTOs.
- Officially supporting non-browser MQTT clients. They work if they follow the protocol, but
  are not tested in v1.

## 4. Anti-goals (failures even if it technically works)

- A developer must understand Orleans placement/observers or design MQTT topics to build a
  simple game.
- The sample requires extra infrastructure (a separate broker, Redis, etc.) to run locally.
- A client sees a move or message that the room later "forgets", or a retried command is
  applied twice.
- A connected player is stuck on stale state, or forfeits a game, because a *server* failed.
- A player can see another player's private state, act as another player, or publish
  directly to other clients bypassing the room grain.
- Only works in one specific deployment topology.

## 5. Constraints

- **Server:** .NET 10 (LTS), latest stable Orleans and MQTTnet 5.x (`MQTTnet.Server` +
  `MQTTnet.AspNetCore`) at time of build. ASP.NET Core hosts the WebSocket endpoint.
- **Client:** TypeScript, browser-first, built on MQTT.js (`mqtt` npm package), MQTT 5 over
  WebSockets. Ships ESM + type declarations.
- **Protocol:** MQTT 5 only. It is needed for reason codes/strings on rejected CONNECT,
  SUBSCRIBE and PUBLISH.
- **License:** MIT (owner to confirm). Sample dependencies must be MIT/BSD/Apache-compatible
  (e.g. no GPL chessboard UIs).

## 6. Approaches considered

| | Approach | Trade-offs |
|---|---|---|
| **A** | **MQTTnet broker embedded in each Orleans silo.** Inbound publish → in-process grain call. Room grain fans out to the nodes holding its members' connections. | One deployable, no extra infra, accurate presence from direct connect/disconnect events. Framework must build cross-node fan-out and leases; connections and compute scale together. |
| **B** | Same code as A, hosted in a **separate gateway process** that is an Orleans client. | Connections and game logic scale independently, and a silo restart doesn't drop WebSockets. Nearly free if A's gateway component depends only on `IGrainFactory`. |
| C | **External broker**; silos are MQTTnet clients using MQTT 5 shared subscriptions. | Broker does fan-out/clustering at huge scale. Extra infra before "hello world"; weaker presence (last-will only); auth via broker plugins; MQTTnet reduced to a thin client. |

**Chosen: A, designed so B is a configuration switch** (owner's decision). A gives the
lowest barrier for new users, which is critical for an open-source package. It keeps presence
accurate and uses MQTTnet as a broker. C may come later as an adapter.

## 7. Design

### 7.1 Packages

| Package | Contents |
|---|---|
| `GrainWire.Abstractions` | `RoomGrain<TState>`, `IRoomGrain`, `PlayerId`, attributes, result types, `IConnectionAuthenticator`, `IRoomSerializer`. Referenced by grain projects. |
| `GrainWire.Server` | The **gateway component** (MQTTnet server, routing, connection tracking, leases, limits), silo extensions, hosting extensions for both modes. |
| `@grainwire/client` (npm) | Browser client. |
| `samples/Chess` | Server (single project, mode A) + Vite/TypeScript web client. |

### 7.2 The hosting switch

The gateway component depends only on `IGrainFactory`/`IClusterClient`, never on silo
internals. That makes both modes the same code:

```csharp
// Mode A — co-hosted (default; what the sample uses)
builder.UseOrleans(silo => silo.AddGrainWire());
builder.Services.AddGrainWireGateway(o => o.Path = "/mqtt");
app.MapGrainWire();

// Mode B — standalone gateway
builder.UseOrleansClient(client => client.AddGrainWireClient());
builder.Services.AddGrainWireGateway(o => o.Path = "/mqtt");
app.MapGrainWire();
```

Each gateway process has a unique `GatewayId` for its lifetime.

### 7.3 Identity model

Three separate concepts:

- **Player** (`PlayerId`): who the person is. It comes only from authentication (7.15).
- **Connection** (`ConnectionId` = MQTT client id + `Generation`): one browser tab's
  connection. The TS client generates a random client id per tab and keeps it in
  `sessionStorage`, so a tab's reconnects reuse it. The gateway gives each new connection with
  that client id a higher `Generation`. Any notification about a connection carries its
  generation, and **stale generations are ignored**, so a delayed disconnect can never remove a
  newer connection.
- **Membership:** a player belongs to a room (with a role and app data). Each membership has a
  set of **attached connections**: the tabs that have joined this room. Presence derives from
  it. A player is *online* while ≥1 connection is attached, *reconnecting* when none are
  attached (grace running), and *left* after the grace period.

### 7.4 MQTT session settings

| Setting | Value | Why |
|---|---|---|
| Client id | random per tab, reused across that tab's reconnects | per-connection inbox and generation tracking |
| Clean Start / Session Expiry | `true` / `0` | no broker session state; recovery is the framework's job, so moving between independent gateways works |
| Client → server QoS | 1 | commands carry ids and are deduplicated (7.8), so redelivery is safe |
| Server → client QoS | 0 | ordering and recovery come from `seq` + sync (7.6); QoS 1 would double traffic for 1,000-member rooms without adding a guarantee across reconnects |
| Retained messages | rejected | rooms, not topics, hold state |
| Will messages | rejected at CONNECT | presence comes from the framework |
| Offline queuing | none | a `request()` issued while disconnected fails fast with `not-connected`; requests in flight at disconnect are resent after reconnect with the same command id |

### 7.5 Protocol (topics and envelope)

Rooms are addressed by `{type}/{id}`. `type` maps to an Orleans grain type via
`[RoomType("chess")]`; `id` is a string key.

| Topic | Direction | Purpose |
|---|---|---|
| `clients/{clientId}/inbox` | server → client | **Everything** this connection receives from any room: snapshots, events, presence, replies, closure notices. A connection may subscribe only to its own inbox, with an exact topic. |
| `rooms/{type}/{id}/cmd/{name}` | client → server | App commands to the room grain. Never delivered to other MQTT clients. |
| `rooms/{type}/{id}/sys/{name}` | client → server | Framework commands: `join`, `leave`, `sync`. |

Envelope (JSON by default, via `IRoomSerializer`):

```json
{ "t": "moved", "room": "chess/abc123", "ri": "c1a…", "se": "7f3…", "seq": 42, "cmd": "optional-command-id", "d": { ... } }
```

| Field | Meaning |
|---|---|
| `ri` | **Room incarnation.** Random id created when the room is created and persisted with it. It changes only if the room is destroyed and a new one is created with the same id. Commands carry the `ri` they target; a mismatch is rejected (`stale-room`). |
| `se` | **Stream epoch.** New random id on every grain activation. It is never persisted, so a recovered room can never reuse an old stream position. |
| `seq` | Per room, **per player**, within a stream epoch. +1 for every event delivered to that player: broadcast copies, private events, presence. The same `seq` goes to all of that player's attached connections. |
| `at` (snapshots only) | Snapshots **do not consume** a `seq`. A snapshot is a baseline "state as of `seq` N". So one tab rejoining never creates a false gap for the player's other tabs. |
| `cmd` | On commands: client-generated command id (UUID). On replies: echoes it. Replies are not part of the `seq` stream. |

A written protocol spec (`docs/protocol.md`) is a deliverable. It is what the future .NET
client and third-party clients are built from.

### 7.6 Delivery guarantees (stated publicly, exactly like this)

1. **Ordered and duplicate-free:** within a room, a client app sees each player's events in
   `seq` order, never twice, with public and private events ordered together.
2. **Eventual state convergence:** every attached client converges to the room's current
   state within a bounded time. Default ≈ sync interval (15 s) plus one round trip, even if
   the last event was lost and the room has gone quiet.
3. **Not guaranteed: delivery of every individual event.** If events are lost, the client
   recovers by snapshot, which reflects their *effects* but not each event. Apps that need every
   message (chat history) put it in room state and the snapshot.

**Mechanisms:**
- **Gap detection:** an event with `seq > last + 1` → client buffers it and sends `sys/sync`.
- **Idle sync:** if a joined room has been silent for the sync interval (default 15 s,
  configurable), the client sends `sys/sync { ri, se, seq }`. The grain answers `in-sync`
  (tiny), or sends a fresh snapshot, or `room-not-found`. This catches a lost final event
  (e.g. checkmate), a lost snapshot, and lost terminal notices (`closed`, `kicked`). Cost:
  ~67 small messages/s for a full 1,000-member room, which is acceptable.
- **Epoch change:** an event or snapshot with a new `se` → client discards buffered events
  from the old epoch and waits for the snapshot. A new `ri` or `room-not-found` → room is gone
  (`onClosed("lost")`).

### 7.7 Join and leave

Client sends a request to `rooms/chess/abc123/sys/join` with
`{ "create": bool, "options": {...}, "data": {...} }`. `options` holds room creation settings
such as time control. `data` holds app-defined join data such as a requested role or an
invite code.

- **Rooms exist only once created.** If the room doesn't exist and `create` is false →
  `room-not-found`. If `create` is true → `OnCreate(creator, claims, options)` decides
  (default: allow, subject to the creation rate limit in 7.15).
- **New member:** `OnJoin(player, data)` accepts (with role) or rejects. This is committed (7.10).
- **Existing member** (rejoin, or another tab): `OnRejoin(player, data)` runs (default: allow)
  so apps can enforce bans or revoked invites. Seat/role is kept.
- On success the connection is **attached** and receives a snapshot (`GetSnapshot(viewer)`,
  per viewer, so private state stays private) at the player's current `seq`.
- `sys/leave` detaches that connection only. Leaving the room for good (giving up the seat)
  is an app command or happens through the grace period.

### 7.8 Command pipeline (commit, then publish)

Every command, and every framework operation that changes state (7.10), goes through one
pipeline inside the non-reentrant room grain:

1. **Validate:** `ri` matches, the player is a member, this connection is attached, and the
   app's permission check passes. The **grain** is the authority. The gateway's own checks
   are only a prefilter, because they can be stale (e.g. right after a kick).
2. **Deduplicate:** if this command id was already processed for this player → return the
   recorded outcome and do nothing else.
3. **Protect state:** in-memory rooms take a copy of the committed state first (Orleans deep
   copier). Rooms can opt out (`Rollback = None`) when their state is large and their
   handlers validate before mutating. Persistent rooms instead restore by reloading from storage.
4. **Run the handler.** `Broadcast`, `SendTo`, `Kick` etc. are **queued in an outbox**, not
   sent.
5. **Outcome:**
   - Handler calls `Reject(reason)` (which ends the handler) or throws → restore state, drop
     the outbox, reply `rejected` / `error` (exception details logged, not sent).
   - Handler returns → in persistent rooms, write state + framework metadata + the dedup
     record. If the write fails → reload, drop the outbox, reply `error: not-committed`.
6. **Commit succeeded:** record the outcome for dedup, **then** release the outbox and the
   success reply (`ok`, with the handler's return value if it has one).

**Request contract (public):**
- Every `request()` gets an explicit `ok`, `rejected`, or `error`, including handlers that
  return plain `Task`.
- `ok` means *committed under the room's persistence mode*.
- The TS client retries a request (reconnect, or no reply within a retry interval) with the
  **same command id**. The grain returns the recorded outcome instead of running it again.
- Dedup records are kept per player for the last 256 command ids or 10 minutes, whichever is
  larger. They are persisted with the room in persistent mode.
- If the overall timeout expires, `request()` fails with **`OutcomeUnknown`**, never with
  "rejected".
- `send()` is fire-and-forget: it carries a command id (so QoS 1 redelivery is
  deduplicated) and a `noreply` flag.

**Crash windows, stated plainly:** a crash after commit but before publishing loses those
events. Clients converge through sync (7.6), and a retried request gets `ok` from the dedup
record. In-memory rooms lose everything on a crash (7.10).

### 7.9 Fan-out, gateway leases and room reattachment

- **Fan-out:** after a commit, the room makes **one one-way call per gateway** that holds
  attached connections. Each call carries the payload once plus `(player, seq)` entries. The
  gateway publishes one copy to each attached connection's inbox. One-way calls can be lost,
  and the guarantees in 7.6 cover that.
- **Leases:** every gateway renews its attachment with each room it has connections in
  (`Renew(gatewayId, observerRef, [connectionId+generation…])`, default every **10 s**). The
  renewal carries the gateway's full current connection list for that room. This one mechanism
  handles three problems:
  1. **Observer renewal:** Orleans observers are not durable; the reference is refreshed on
     every renewal.
  2. **Dead-gateway detection:** a room that misses a gateway's renewals for **25 s**
     (configurable) detaches all of its connections. Their players go to *reconnecting*, and
     the normal grace period follows.
  3. **Room recovery while browsers stay connected:** after a room's silo dies and the room
     reactivates elsewhere, it knows no gateways. The next renewal (≤10 s), or any client
     sync or command through that gateway, reattaches the connections. Each one gets a
     snapshot in the new stream epoch. No WebSocket needs to drop.
- **Fairness after reactivation:** right after reactivation every member starts as
  *reconnecting* with a **fresh** grace period. This is longer than one lease interval, so
  connected players are restored before anyone forfeits.
- `Renew` answered with `room-not-found` → the gateway sends `closed: lost` to those
  connections.
- In mode A, the gateway's own silo dying takes its WebSockets with it. Clients reconnect to
  another node and rejoin; leases on the rooms expire for the dead gateway.

### 7.10 Room lifecycle and persistence

- **A room stays active while it has members** (online or reconnecting); the framework
  delays Orleans idle deactivation. Once empty it deactivates normally.
- **In-memory (default):** if the silo holding the room fails, the room is lost. Renewals,
  syncs and rejoins get `room-not-found`, and clients see `onClosed("lost")`. This is
  documented as the cost of the default.
- **Persistent (opt-in):** the developer configures an Orleans storage provider for the room
  type. **These operations commit** (pipeline 7.8, all-or-nothing with their outbox):
  create, new-member join, role/seat changes, leave-for-good, kick, close, grace-period expiry
  (`OnPlayerLeft` and anything it changes), app scheduled callbacks, and app commands.
- **Durable:** `ri`, `TState`, member list (roles, app data), dedup records, scheduled
  deadlines.
- **Not durable, by design:** `se`/`seq` (a new epoch per activation), presence, attached
  connections, gateway locations, grace timers. After reactivation, presence restarts fresh so
  nobody forfeits because of a server crash.
- **Durable deadlines:** `Schedule(name, dueAt)` / `CancelSchedule(name)` are stored in room
  metadata and re-armed on activation; overdue ones fire immediately. This covers cases like a
  chess clock flag. Callbacks run through the pipeline. Grain timers alone would not survive
  deactivation or a silo failure. A room with no members is inactive, so its deadlines fire
  on its next activation (documented).

### 7.11 Concurrency rules

- `RoomGrain` is non-reentrant. At startup the framework **refuses** subclasses marked
  `[Reentrant]`, or with `[AlwaysInterleave]`/`[MayInterleave]`/read-only interleaving on room
  methods. The one-command-at-a-time guarantee depends on this.
- Framework timers (grace, deadlines, lease checks) are registered non-interleaving, so they
  run as turns like commands do.

### 7.12 Server API (RoomGrain base)

Hooks: `OnCreate`, `OnJoin`, `OnRejoin`, `OnPresenceChanged`, `OnPlayerLeft`,
`GetSnapshot(viewer)`, plus `[OnMessage("name")]` handlers (return `Task` or `Task<T>`).

Operations (queued in the outbox, released on commit): `Broadcast(name, data)`,
`SendTo(player, name, data)`, `Kick(player, reason)`, `CloseRoom(reason)`. Plus
`Reject(reason)`, `Schedule`/`CancelSchedule`, and read-only `Members` (with roles and presence).

Per-room-type options: grace period (default 30 s), max members (default 1,000), max payload,
rollback mode, persistence (storage provider name).

### 7.13 TypeScript client

- Connect with token; subscribe to own inbox; automatic reconnect with backoff; re-join
  attached rooms after reconnect; resend in-flight requests with the same command ids.
- Client-side ordering per 7.6: buffer events before the snapshot, drop `seq` ≤ `at`/last,
  gap → sync, idle → sync, new `se` → reset, new `ri`/not-found → closed.
- `client.join(type, id, { create, options, data })`, `room.request()` with
  `OutcomeUnknown`, `room.send()`, `room.on/onSnapshot/onPresence/onClosed`, `room.leave()`.
- Typed API via generics, no code generation. Connection-state events (`connected`,
  `reconnecting`, `disconnected`) for UI.

### 7.14 Chess sample

- Server: one ASP.NET Core project in mode A, **persistent** room type (in-memory storage
  provider locally, so it shows the API without extra infra). `ChessGameGrain`: first two
  joiners take white/black, later joiners spectate; legal-move validation via an
  open-source chess-rules library with a compatible license (sample-only dependency); clocks
  via `Schedule`; resign, game over, forfeit after the grace period.
- Game ids are random 128-bit values (unguessable links).
- Web client: Vite + TypeScript, an MIT-compatible board UI, "create game → share link",
  shows opponent *reconnecting* and handles `OutcomeUnknown` by re-syncing.

### 7.15 Security and limits

- **Identity** comes only from the authenticated connection (`IConnectionAuthenticator`
  validates the CONNECT token, returns `PlayerId` + claims). The box ships a
  **development-only** authenticator: it accepts the username as the player id, logs a warning,
  and refuses to run in Production unless explicitly allowed. A JWT example is in the docs/sample.
- **MQTT policy** (enforced in MQTTnet interceptors):
  - Subscribe: only the exact own `clients/{clientId}/inbox`. Wildcards, `$share/…` and
    everything else are rejected.
  - Publish: only `rooms/+/+/cmd/+` and `rooms/+/+/sys/+`. Retained publishes are rejected.
  - Will messages are rejected at CONNECT.
- **Authorization:** the grain re-checks membership and permission on every command (7.8);
  `OnRejoin` re-checks access on every rejoin; `OnCreate` receives claims.
- **Limits** (all configurable; these defaults are a starting point, to be tuned by the
  benchmark):

  | Limit | Default | When exceeded |
  |---|---|---|
  | Payload size | 64 KB | publish rejected |
  | Connections per player | 5 | new CONNECT rejected |
  | Rooms attached per connection | 20 | join rejected |
  | Command rate per connection | 20/s, burst 40 | command rejected `rate-limited` |
  | Pending requests per connection | 32 | request rejected |
  | Outgoing queue per connection | 1,000 messages | slow consumer disconnected (it reconnects and gets a snapshot) |
  | Room creations per player | 10/min | create rejected |
  | Members per room | 1,000 | join rejected |

### 7.16 Testing

- Unit tests: dispatch, outbox/commit/rollback, dedup, `seq`/`at` stamping, presence state
  machine, generations, leases, deadlines.
- Integration tests with an Orleans test cluster of **2+ silos**, both hosting modes, and real
  MQTT-over-WebSocket clients: cross-node broadcast, reconnect to a *different* node,
  grace-period expiry, authorization and MQTT-policy rejections, limits.
- **Failure tests that must pass before the public API is called stable:**
  1. Save fails after a state change → no client sees the change; later commands see the
     restored state.
  2. Success reply lost, request retried → applied exactly once, `ok` returned.
  3. Final event lost, room goes quiet → client converges within the sync bound.
  4. Room's silo fails while gateway and browser stay connected → routing restored and
     snapshot received, nobody forfeits.
  5. Two tabs reconnect while an old disconnect arrives late → membership, presence and
     `seq` are all correct.
- TypeScript client tests for buffering, dedup, gap/idle sync, epoch change, retries.

### 7.17 Scale target and benchmark (owner's decision: rooms up to ~1,000 members)

- v1 is designed for rooms of **up to ~1,000 members**; larger is a non-goal.
- Member count alone is not a capacity promise. The repo includes a benchmark with
  **defined workloads**, and the docs publish its results (latency, memory, resync time) for
  the reference hardware:
  - *Big room:* 1,000 members across 2 gateways, 5 events/s room-wide, 512-byte events,
    50 KB snapshots, persistent storage on. Measure p50/p95 delivery latency, time for all
    clients to resync after a gateway is killed, and memory per room and per connection.
    Starting targets: p95 < 250 ms; full resync < 30 s.
  - *Many small rooms:* 5,000 chess rooms × 2 players, 1 move every 5 s per room.
- If the defaults in 7.15 don't survive the benchmark, they are tuned before 1.0.

## 8. Decided by the owner

- Reusable, open-source framework for other developers (NuGet + npm).
- MQTT handles connections; one Orleans grain per room owns the data.
- Presence: opponent sees "reconnecting"; returning player gets full state automatically.
- v1 clients: browser via TypeScript. .NET client later.
- Hosting: embedded broker in silo (A), with standalone gateway (B) as a switch.
- Scale: rooms up to ~1,000 members.
- Name: GrainWire. The API keeps "room" as the word for the concept.

## 9. Proposed by Claude (owner to check)

- MIT license; .NET 10; MQTT 5 only.
- Product claim narrowed to authoritative room state. Fast action games and
  every-event delivery/durable logs are out of v1.
- Explicit join/create requests with `OnCreate`/`OnJoin`/`OnRejoin` hooks.
- Per-connection inbox; per-player `seq`; snapshots as baselines (`at`); `ri` + `se` ids.
- Commit-then-publish pipeline with rollback (deep-copy for in-memory rooms, opt-out
  available), command-id dedup, and `OutcomeUnknown` on timeout.
- Idle sync every 15 s for eventual convergence, rather than acknowledged per-event delivery.
- Gateway leases (10 s renew / 25 s expiry) for observer renewal, dead-gateway detection and
  room reattachment.
- In-memory rooms by default; persistence opt-in; presence never persisted; durable
  deadlines via `Schedule`.
- QoS 1 inbound, QoS 0 outbound, clean sessions, no retained/will messages.
- Default limits in 7.15; benchmark workloads and starting targets in 7.17.
- Chess sample uses persistent rooms and unguessable game ids.
- Dev-only authenticator in the box; JWT as an example, not a package.

## 10. Left to the builder

- Exact project layout, internal class names, options-binding details.
- Choice of chess-rules library and board UI (license constraint applies).
- Internal data structures (dedup store, lease tables, outbox), within the contracts above.
- How the gateway batches renewals and fan-out publishes internally.
- CI, packaging, versioning, and the benchmark harness.

## 11. Review history

- **v1 → builder check** (simulated builder reading the doc): led to explicit join/create,
  per-player ordered streams, room lifecycle rules, and the scale target.
- **v1 → external design review** (shared by the owner): all seven findings adopted.
  Commit-then-publish and rollback; an explicit request/retry contract with dedup and
  `OutcomeUnknown`; idle sync for lost final events and three explicitly separated guarantees;
  gateway leases for room reattachment (moved out of "left to the builder"); connection-level
  membership with generations and snapshot baselines; durability of lifecycle operations and
  deadlines, with stream epochs so `seq` is never reused; MQTT policy and basic limits.
  Also adopted: an MQTT settings table, benchmark workloads instead of a bare member count,
  concurrency rules, a narrowed product claim, and the five failure tests.
