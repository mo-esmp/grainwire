# GrainWire — Implementation Plan

Date: 2026-11-15
Design: [../specs/2026-11-15-grainwire-design.md](../specs/2026-11-15-grainwire-design.md) (approved v2)
Section references like "§7.8" point into that design.

## How this plan is shaped

- **Spike first, then a walking skeleton.** The riskiest unknowns are library APIs
  (MQTTnet 5 interceptors and WebSocket hosting on .NET 10, Orleans observers from a client,
  deactivation control). A short spike settles them. Then a thin end-to-end slice (browser →
  MQTT → grain → browser) runs before any feature goes deep.
- **Each phase ends green.** Each one has a "done when" that is checked by tests, not by
  reading code.
- **Protocol first.** `docs/protocol.md` is written in phase 1 and updated in the same
  change whenever the wire format moves, so the TypeScript client and the future .NET client
  have one source of truth.
- **Pure logic is kept separate from transport,** so it is unit-testable without a cluster:
  the room pipeline, the presence state machine, and the client ordering engine.

## Phase 0: Spike and repository setup

**0.1 Technology spike (throwaway, ≤2 days).** Confirm and write down in `docs/adr/0001-library-apis.md`:
- MQTTnet 5 hosted in ASP.NET Core (.NET 10) over WebSockets at `/mqtt`, reachable from MQTT.js in a browser.
- MQTTnet interceptors can: reject CONNECT with a reason (and refuse Will messages); reject
  SUBSCRIBE per topic with a reason string; swallow a PUBLISH (no routing) and reject
  retained ones; inject a message to one client's topic.
- Per-client outgoing queue limit and the slow-consumer behavior in MQTTnet 5.
- Orleans: non-reentrant grain with `DelayDeactivation` (or the current equivalent),
  non-interleaving grain timers, `[OneWay]` calls to a grain observer created in **both** a
  silo-hosted and an `IClusterClient`-hosted service, deep copier for `TState`, and
  `GrainType` per `[RoomType]` with `IRoomGrain` resolution.
- Exact package versions (Orleans, MQTTnet, MQTT.js) pinned.

**0.2 Repository.**
- Solution `GrainWire.sln`: `src/GrainWire.Abstractions`, `src/GrainWire.Server`,
  `clients/ts` (npm workspace), `samples/Chess/{Server,Web}`, `tests/*`, `bench/`.
- MIT `LICENSE`, `README` stub, `Directory.Build.props` (nullable, warnings-as-errors,
  deterministic builds, SourceLink), `.editorconfig`.
- CI (GitHub Actions): build + test .NET and TS on PRs; pack NuGet/npm artifacts (no publish yet).
- **Owner action:** reserve the GrainWire NuGet package IDs (prefix
  reservation if wanted) and the npm scope before the first public preview.

**Done when:** the spike ADR answers every point above, and CI builds an empty solution and TS workspace.

## Phase 1: Protocol and abstractions

- `docs/protocol.md`: topics (§7.5), envelope fields `t/room/ri/se/seq/at/cmd/d`, the
  sys commands `join/leave/sync` with payloads and every reply/error code (`ok`, `rejected`,
  `error`, `not-committed`, `stale-room`, `room-not-found`, `not-member`, `rate-limited`,
  `in-sync`, `closed`, `kicked`, `lost`), MQTT session rules (§7.4), and policy (§7.15).
- `GrainWire.Abstractions`: `PlayerId`, `ConnectionId` (+`Generation`), `RoomAddress`,
  `RoomIncarnation`, `StreamEpoch`, result types (`JoinResult`, `CreateResult`),
  `[RoomType]`, `[OnMessage]`, `IConnectionAuthenticator`, `IRoomSerializer` (+ System.Text.Json default),
  `IRoomGrain` (internal-facing grain interface), `IGatewayObserver`, room options.
- Envelope serialization tests: round-trip and golden JSON files. The TS client tests reuse
  the same golden files.

**Done when:** the protocol doc is reviewed against the design, and golden-file tests pass.

## Phase 2: Walking skeleton (single silo, happy path)

The thinnest end-to-end slice:
- Gateway: MQTTnet over WebSockets, dev authenticator (§7.15), inbox-only subscribe policy,
  `cmd`/`sys` routing to `IRoomGrain`, inbox publish via injection, one `IGatewayObserver`.
- `RoomGrain<TState>` minimal: create/join/command/broadcast, per-player `seq`, snapshot
  with `at`; no rollback, persistence, leases or limits yet.
- TS client minimal: connect, inbox, `join`, `on`, `request` (no retries), `onSnapshot`.
- `samples/Echo` (temporary): a counter room plus a tiny HTML page.

**Done when:** an automated test starts the host, two MQTT.js clients (Node) join a room,
one sends `increment`, and both receive the event with correct `seq`.

## Phase 3: Room core, done properly (§7.7, §7.8, §7.11, §7.12)

Build the pipeline as a pure, testable component (`RoomEngine`) that the grain hosts:
- Membership model: players, roles, app data, attached connections with generations; the
  presence state machine (online → reconnecting → left) with grace timers.
- Hooks: `OnCreate`, `OnJoin`, `OnRejoin`, `OnPresenceChanged`, `OnPlayerLeft`, `GetSnapshot`.
- Command pipeline: validate (`ri`, membership, attached connection) → dedup (256 ids /
  10 min per player) → protect state (deep-copy; `Rollback = None` option) → handler with
  **outbox** → `Reject`/throw → restore and drop the outbox → commit → record → release.
- Explicit outcomes for every request (`ok` even for `Task` handlers); `noreply` for `send()`.
- `Kick`, `CloseRoom`, `Members`; `ri` creation; `se` per activation.
- Startup validation that refuses reentrant or interleaving room grains.
- Deactivation delayed while the room has members.

**Done when:** unit tests cover every pipeline branch, dedup returns the recorded outcome,
`Reject`/throw leaves state and outbox untouched, generation-stale detaches are ignored, and
grace expiry runs `OnPlayerLeft`.

## Phase 4: Gateway, done properly (§7.3, §7.4, §7.9 fan-out, §7.15)

- Connection registry: client id → (player, generation, attached rooms); generations
  incremented on reconnect; disconnect → `Detach(conn, generation)` to each attached room.
- Full MQTT policy: exact own-inbox subscribe only; publish allow-list; reject retained and
  Will messages; MQTT 5 reason strings.
- Fan-out handler: per-gateway one-way call → per-connection inbox publish.
- Limits from §7.15 (payload, connections per player, rooms per connection, command rate
  token bucket, pending requests, outgoing queue → disconnect, creations per player);
  max-members enforced in the grain.

**Done when:** integration tests on a **2-silo** test cluster prove cross-node broadcast,
reconnect to the other node with a correct snapshot, every policy rejection, and every limit.

## Phase 5: Leases and recovery (§7.9)

- Gateway lease loop: `Renew(gatewayId, observerRef, connections[])` every 10 s per room
  (batched internally); handle `room-not-found` → `closed: lost`.
- Room side: lease table, expiry at 25 s → detach that gateway's connections; reattach on
  renewal or any gateway call; snapshot to every reattached connection in the new `se`;
  fresh grace for all members after reactivation.

**Done when:** failure test **#4** (the room's silo is killed while the gateway and client stay
connected → routing restored, snapshot received, no forfeit) and gateway-kill expiry tests pass.

## Phase 6: Persistence and durable deadlines (§7.10)

- Opt-in storage per room type through Orleans storage providers; persisted record:
  `ri`, `TState`, members, dedup records, deadlines.
- Every committing operation from §7.10 goes through the pipeline; a write failure →
  reload, drop the outbox, `error: not-committed`.
- `Schedule`/`CancelSchedule`: stored, re-armed on activation, overdue ones fire immediately.

**Done when:** failure tests **#1** (save fails → nothing observed, state restored) and **#2**
(lost reply plus retry → applied once) pass, as do reactivation tests for members, dedup and
deadlines.

## Phase 7: TypeScript client, done properly (§7.6, §7.13)

- Pure **ordering engine** (no I/O): snapshot baseline, buffering, drop ≤ last, gap → sync,
  new `se` → reset, new `ri`/not-found → closed. Property-based tests (fast-check) with random
  loss, duplication and reordering show that the app-visible stream is ordered and duplicate-free.
- Idle sync timer (15 s), reconnect with backoff, re-join, in-flight request resend with the
  same command id, overall timeout → `OutcomeUnknown`, `not-connected` fast-fail,
  `onClosed`, connection-state events. Stable per-tab client id in `sessionStorage`.
- Typed generic API; ESM + `.d.ts`; uses the phase 1 golden files.

**Done when:** the engine property tests pass, and integration tests against a real server
pass failure test **#3** (final event lost → converge within the bound) and **#5** (two tabs,
late stale disconnect → membership, presence and `seq` correct).

## Phase 8: Hosting mode B

- `AddGrainWireClient()` plus a gateway running under `IClusterClient`; the same gateway code.
- Run the whole integration and failure suite in **both modes** (test matrix).

**Done when:** all five failure tests and the phase 4 integration suite pass in mode A and mode B.

## Phase 9: Chess sample (§7.14)

- Server: `ChessGameGrain` (seats, spectators, legal moves via a permissively licensed
  rules library, clocks via `Schedule`, resign, game over, forfeit after grace); persistent
  room with an in-memory storage provider; random 128-bit game ids.
- Web: Vite + TS, MIT-compatible board UI, create/share link, presence badge
  ("reconnecting…"), handles `OutcomeUnknown` by syncing, and shows "game was lost".
- Remove `samples/Echo`, or keep it as the "hello world" in the docs.

**Done when:** a Playwright test plays a short game between two browser contexts,
including one player dropping offline and coming back mid-game.

## Phase 10: Benchmark and tuning (§7.17)

- `bench/`: load generator (Node MQTT.js or .NET MQTT clients) for the *big room* and
  *many small rooms* workloads; it measures p50/p95 latency, resync time after a gateway
  kill, and memory per room and per connection.
- Tune the §7.15 defaults; record results and reference hardware in `docs/benchmarks.md`.

**Done when:** both workloads run repeatably, and results are published (against the starting
targets p95 < 250 ms and full resync < 30 s), along with any default changes.

## Phase 11: Docs and preview release

- Docs: getting started (from zero to a running room in under 15 minutes), guarantees page
  (the three guarantees from §7.6 stated verbatim), room lifecycle and persistence, limits,
  security checklist (replacing the dev authenticator; a JWT example), hosting modes, protocol.
- Packaging: NuGet + npm `0.1.0-preview`, release notes, a CONTRIBUTING guide, and issue templates.
- **Owner action:** approve publishing.

**Done when:** a fresh machine can follow the getting-started guide to a working room using
only published packages.

## Order and parallelism

```
0 → 1 → 2 → 3 ─┬→ 5 → 6 ─┐
              └→ 4 ──────┼→ 8 → 9 → 10 → 11
          (2) → 7 ───────┘
```
- Phase 7's ordering engine can start right after phase 1; its integration tests need phases 4–6.
- Phases 3 and 4 can run in parallel once the skeleton exists; they touch separate projects.

## Risks to watch

| Risk | Mitigation |
|---|---|
| MQTTnet 5 can't reject SUBSCRIBE with a reason or limit queues per client as needed | Settled in the phase 0 spike. Fallback: custom handling in the interceptor and a gateway-side queue in front of injection. |
| Deep copy per command is too slow for big-state rooms | `Rollback = None` option exists; the benchmark measures it; the docs explain when to use it. |
| Lease renewals are too chatty with many rooms per gateway | Batch renewals per silo, or use jittered intervals; the benchmark covers many small rooms. |
| The 1,000-member target misses its latency goal | Tune fan-out batching first; otherwise lower the documented limit for 1.0 rather than weaken guarantees. |
