# Step 0: what an idle connection costs today

Measured on 19 November 2026, before anything of MqttWire is built, as section 6 of
[the GrainWire-on-MqttWire design](specs/2026-11-19-grainwire-on-mqttwire-design.md) asks.
The plan was [docs/plans/2026-11-19-step-0-idle-memory-plan.md](plans/2026-11-19-step-0-idle-memory-plan.md).

## 1. The result in three lines

- **Today**, an idle connection that has joined one room of two costs the gateways **about 34,000 bytes** of live managed memory (34,039 and 34,078 bytes in two runs).
- **The floor**, a bare ASP.NET Core WebSocket endpoint with the same idle connections, costs **about 11,900 bytes** (11,917, 11,921 and 11,915 bytes in three runs).
- **The floor is 35% of today's figure.**

## 2. The 70% rule

The design says that if the floor alone is more than about 70% of today's figure, the approach cannot deliver much,
and the owner decides whether to continue. **At 35%, the measurement is well on the side of continuing.** About
22,000 bytes per connection are above the floor. About half of them, 11,500 bytes, are MQTTnet's and the second
Connections layer's, which MqttWire replaces; GrainWire's own objects are at least 2,700; the rest have no owner that
their type names tell (section 5).

## 3. The targets

| Target | Set to | In bytes | Today |
|---|---|---|---|
| MqttWire alone, idle | floor + 1 KB, as proposed | about 12,900 | (no MqttWire yet) |
| A joined connection in GrainWire, states 2 and 3 | floor + 3 KB, the owner's decision (proposed: floor + 2 KB) | about 15,000 | 34,039 (state 2), 35,429 to 35,722 (state 3) |

(1 KB is 1,024 bytes.) Meeting the GrainWire target would take about 56% off today's cost in state 2.

**Decided on 2026-11-20:** after reading this report, the owner set the GrainWire target to **floor + 3 KB** instead of
the proposed floor + 2 KB. The MqttWire target stays at floor + 1 KB, as proposed.

**What the attribution says about them** (section 5 has the details):

- **What goes away is large, and part of it is traced.** MQTTnet holds about **9,000 bytes** per connection: its own
  objects (about 3,800 bytes) and two byte arrays per connection, one of 4 KB and one of 1 KB, that the heap dump shows
  are held by MQTTnet's buffer writers (5,152 bytes). ASP.NET Core's second Connections layer, the WebSocket transport
  that MQTTnet sits on, with two pipes more than the floor, adds about **2,500 bytes**. Together that is about 11,500
  bytes; MqttWire replaces both. A further 8,000 bytes or so above the floor are strings, tasks, locks and
  cancellation sources whose owner the type names do not tell; some of them are MQTTnet's too.
  **"MqttWire alone, floor + 1 KB" looks achievable but demanding:** MqttWire has to hold all its per-connection state
  in about 1 KB and, like the floor, hold no buffer while a connection is idle.
- **GrainWire's own share is the tight part.** Counting every type whose name mentions GrainWire (its connection
  object, its rate limiter, the per-connection inbound channel and its async state, its dictionaries, one room's
  share of the per-room tables), GrainWire costs at least **2,700 bytes** per joined connection today. It is a lower
  bound: the dictionary of three claims and the player's strings do not mention GrainWire in their type names. Because
  the GrainWire target of floor + 3 KB includes MqttWire's 1 KB, GrainWire itself has about 2 KB left. **GrainWire has
  to cut its per-connection state by at least 650 bytes, about a quarter, to meet it.** (Against the proposed
  floor + 2 KB, which left GrainWire about 1 KB, the cut would have had to be well over half.) Likely places: the per-connection bounded channel and its pending async operations, and the two `ConcurrentDictionary`s
  (the connection's rooms, the room's connections) whose bucket arrays alone take 320 bytes each.
- **Each further room adds about 1,400 bytes per connection** (five rooms per connection: 39,717 bytes in state 2,
  against 34,039 bytes with one). The target is stated for one room of two per pair, so this is not part of it, but
  it is GrainWire's own cost and grows with use.

## 4. Today's gateway

MQTTnet 5.2.0.1603, two standalone gateways and one Orleans silo, three claims per connection. Per connection is the
slope between 3,000 and 12,000 connections. "Fixed" is the line's value at no connections.

**One room of two per pair, first run** (with the heap dumps):

| State | Live managed bytes per connection | Fixed (MB) | Working set per connection |
|---|---|---|---|
| 1. Connected and subscribed | 31,901 | 10.1 | 39,273 |
| 2. Joined | 34,039 | 11.5 | 33,600 |
| 3. Active, then idle | 35,722 | 11.4 | 46,251 |
| 2 again, after churn | 34,531 | 11.5 | 45,256 |

- Kept after activity, per connection (state 3 − state 2): 1,683 bytes
- After churn (state 4), live managed bytes above the baseline: 4.7 MB and 18.5 MB
- Silo, per room in state 2: 9,579 bytes

**One room of two per pair, second run:**

| State | Live managed bytes per connection | Fixed (MB) | Working set per connection |
|---|---|---|---|
| 1. Connected and subscribed | 31,933 | 9.7 | 39,292 |
| 2. Joined | 34,078 | 10.8 | 34,660 |
| 3. Active, then idle | 35,429 | 12.2 | 37,365 |
| 2 again, after churn | 34,360 | 11.3 | 38,165 |

- Kept after activity, per connection: 1,350 bytes
- After churn, above the baseline: 4.2 MB and 14.2 MB
- Silo, per room in state 2: 9,618 bytes

**Five rooms per connection:**

| State | Live managed bytes per connection | Fixed (MB) | Working set per connection |
|---|---|---|---|
| 1. Connected and subscribed | 31,944 | 9.8 | 39,969 |
| 2. Joined | 39,717 | 11.7 | 46,531 |
| 3. Active, then idle | 40,981 | 13.2 | 57,747 |
| 2 again, after churn | 40,921 | 12.1 | 35,166 |

- Kept after activity, per connection: 1,264 bytes
- After churn, above the baseline: 10.8 MB and 40.7 MB
- Silo, per room in state 2: 9,428 bytes

**Two things to know about today's gateway:**

- **After activity, live managed bytes are 1.3 to 1.7 KB per connection higher** than before it (state 3 against
  state 2; 1,350 bytes in the run without heap dumps, 1,683 in the run with them, where the dump ran inside the
  gateway between the two samples). What holds them is not shown. The design also asks for "what the buffer pool
  keeps after activity" as a separate figure; this benchmark does not measure it separately, only the whole heap.
  In the five-room run the activity used only the first of each connection's rooms.
- **After churn, memory does not fully come back.** 25 seconds after everyone has gone, the gateways hold 14 to
  18 MB more at 12,000 connections and 4 to 5 MB more at 3,000: about 1.2 to 1.6 KB per departed connection, and
  about 3.5 KB with five rooms each. When everyone comes back, state 2 is 282 and 492 bytes per connection above the
  first time with one room, and 1,204 bytes with five. One churn cycle cannot show whether it would pile up over many.
  What holds it is not shown: per-connection or per-room state kept for longer than 25 seconds, or buffers kept by a
  pool after the activity, are both possible; the residue per departed connection is close to what activity left
  behind. A heap dump in state 4 would tell.

## 5. Who owns the bytes

Two heap dumps of the first gateway in state 2, at 1,500 and 6,000 connections on that gateway (half of 3,000 and
12,000), and two of the floor, at 3,000 and 12,000. Per connection is the slope between them.

The figures below were read from the dump files themselves, object by object, with the graph classes that ship in
`dotnet-gcdump` (a one-off tool, not part of the benchmark). Their totals equal the dumps' own totals to the byte,
and nothing was sampled. The gateway comes to **34,283 bytes** per connection, against 34,039 from `/stats`; the
floor to **11,884 bytes**, against 11,915.

**By the namespace of each type**, as the benchmark groups them:

| Owner | Bytes per connection |
|---|---|
| MQTTnet | 1,752 |
| ASP.NET Core Connections layer and pipes | 2,176 |
| Kestrel, ASP.NET Core, WebSocket, sockets | 5,096 |
| GrainWire | 384 |
| Orleans | 16 |
| Other (strings, arrays, runtime, tasks) | 24,859 |
| **Total** | **34,283** |

The benchmark itself prints this table from `dotnet-gcdump report`, whose text gives, for strings and arrays, the
size of one object rather than the average; its rows for those are approximate (it printed 26,310 for "Other" and
224 for GrainWire), and it now prints how far its rows miss the dumps' total.

A type is counted under its own namespace, so the strings, arrays, tasks and collections that belong to a library's
objects are under "Other". **A closer look at "Other"**, with the floor for comparison:

- **One 4 KB and one 1 KB byte array per connection: 5,152 bytes, held by MQTTnet.** At 6,000 connections there are
  6,152 arrays of 4,120 bytes and 6,024 of 1,048 bytes. Following the references in the dump: every 4 KB array of a
  connection is held by an `MQTTnet.Formatter.MqttBufferWriter` (reached from the connection's
  `MqttPacketFormatterAdapter` or its `MqttV5PacketEncoder`), and every 1 KB array by the `MqttBufferWriter` of an
  `MqttV5PropertiesWriter`. The floor, on the same Kestrel, has about 30 arrays of 4 KB in all, at either count.
- **Types that mention MQTTnet anywhere in their name** (its own objects, plus the collections and async state built
  on them) come to about 3,800 bytes; with the two arrays, **MQTTnet holds about 9,000 bytes per connection**.
- **Types that mention GrainWire anywhere in their name** come to about 2,700 bytes: the connection's
  `ConcurrentDictionary` of its rooms (`GatewayConnection.Rooms`), the room's `ConcurrentDictionary` of attached
  connections (`RoomAttachments.Connections`; a room of two has one on each gateway, so here one per connection),
  320-byte bucket arrays each; the per-connection `BoundedChannel<InboundMessage>` with its waiting read and write
  and the loop that reads it; the connection object, its token bucket and its delivery batches; and the rest of one
  room's share of the per-room tables.
  This is a lower bound: the dictionary of three claims and the player's strings do not mention GrainWire.
- **The second Connections layer**: types that mention the Connections layer or pipes come to about 3,900 bytes on
  the gateway against about 1,400 on the floor, about 2,500 more: four pipes per connection against two, plus
  `HttpConnectionContext`, the WebSocket transport and their async state.
- **The rest** is what any ASP.NET Core WebSocket costs (it is in the floor too), plus items whose owner the type name
  does not tell: strings (1,349 bytes per connection, against 516 on the floor: client ids, topics, the three claims),
  about 8 cancellation token sources per connection, locks, tasks and `Activity` objects.

Caveats: `dotnet-gcdump` on .NET 10 warns that it may miss objects on large heaps; the dumps' totals agree with
`/stats` within 1%, so little was lost. Only the two byte arrays were traced to their holders; the other groupings
are by type name.

## 6. The floor

A bare ASP.NET Core WebSocket endpoint: sub-protocol `mqtt`, keep-alive off, each connection waiting in a zero-byte
read. 3,000 and 12,000 idle connections.

**Without TLS, first run:**

| State | Live managed bytes per connection | Fixed (MB) | Working set per connection |
|---|---|---|---|
| Connected | 11,917 | 1.2 | 14,047 |
| Connected again, after churn | 11,983 | 1.3 | 10,462 |

- After churn, live managed bytes above the baseline: 0.5 MB and 1.4 MB

**Without TLS, second run:**

| State | Live managed bytes per connection | Fixed (MB) | Working set per connection |
|---|---|---|---|
| Connected | 11,921 | 1.1 | 14,518 |
| Connected again, after churn | 11,957 | 1.3 | 10,043 |

- After churn, above the baseline: 0.4 MB and 1.1 MB

A third run, made to take the floor's heap dumps, gave 11,915 and 11,972 bytes.

**With TLS in Kestrel** (reported, not decisive):

| State | Live managed bytes per connection | Fixed (MB) | Working set per connection |
|---|---|---|---|
| Connected | 14,630 | 2.8 | 27,864 |
| Connected again, after churn | 14,620 | 3.2 | 26,115 |

- After churn, above the baseline: 2.0 MB and 2.2 MB

TLS adds about 2,700 bytes per connection. The floor's 11,900 bytes are Kestrel's HTTP/1.1 connection objects
(the connection, its request and response headers, about 1,700 bytes), the socket's receiver and connection, two
pipes, .NET's WebSocket object and the async state of the waits. After churn the floor holds 0.4 to 1.4 MB more
than before (2.0 to 2.2 MB with TLS), but it had no activity phase, so it cannot be compared with the gateway's
figure after churn.

## 7. How it was measured

- Machine: Microsoft Windows 10.0.26200, 10 logical cores, 31 GB RAM, .NET 10.0.15. A virtual machine; the load
  generator, the silo and the gateways all ran on it.
- Counts: 3,000 and 12,000 connections (factor 4), each count in fresh server processes. Rooms of two, the two
  players on different gateways; three claims per connection; 20 commands per connection in the activity phase.
- **Per connection is a slope** between the two counts, never bytes divided by connections. The deciding figure is live
  managed bytes after a full collection; each sample was taken only when two samples two seconds apart were within 1%.
  No sample had to be taken while the heap was still moving.
- Commands, each in `bench/GrainWire.Bench`:
  - `idle --gcdump artifacts/step-0 --out artifacts/step-0/idle.md` and again without dumps (`idle-2.md`)
  - `idle --rooms-per-connection 5 --out artifacts/step-0/idle-5-rooms.md`
  - `floor-run --out artifacts/step-0/floor.md` and again (`floor-2.md`); `floor-run --tls --out artifacts/step-0/floor-tls.md`
  - the floor's heap dumps: `dotnet-gcdump collect` on the floor process during a third `floor-run`, about a second
    after all connections of each count were open
  - the exact sizes and holders in section 5: a one-off reader of the four `.gcdump` files
- Agreement: the two gateway runs differ by 0.1% in state 2 and by 1% or less in every state's live managed bytes;
  the three floor runs by 0.05%. No third gateway run was needed (the rule was 10%).
- **Outbound ports.** Windows gives 16,384 outbound ports, and a closed connection keeps its port for about two
  minutes. The first attempt at these counts stopped with "Only 8,307 of 12,000 connections" when everyone came back
  after churn (that attempt's output was overwritten by the second). The benchmark now waits, before each mass
  connect, until enough ports are free (25 to 101 seconds each time it had to). State 4 is sampled before that wait,
  so no figure changes meaning.

## 8. What this does not show

- Real networks: everything ran over loopback on one machine.
- A load balancer or proxy in front of the gateways.
- More than one silo.
- Working set is reported for information only. It moves with the machine and the garbage collector, as the tables
  show, and does not decide anything.
- What holds the memory left after activity and after churn (section 4): no heap dump was taken in states 3 and 4.
- The separate figure for what the buffer pool keeps after activity (section 4).
- Who holds the strings, tasks, locks and cancellation sources above the floor (section 5): only the two byte arrays
  were traced.

## 9. The zero-byte read

The floor is consistent with an idle connection holding no buffer: all its byte arrays together grow by 147 bytes
per connection, which is one array of 149 bytes per connection (the small receive buffer that .NET's WebSocket keeps
for itself), and its 4 KB arrays stay at about 30 whether 3,000 or 12,000 connections are open. A 4 KB buffer per
connection would add about 4,100 bytes to the slope.
