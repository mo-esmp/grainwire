# Benchmarks

GrainWire v1 is designed for rooms of **up to about 1,000 members**. A member count alone is not
a capacity promise, so this page states the workloads, the machine, and what was measured.
Run it yourself:

```bash
dotnet run -c Release --project bench/GrainWire.Bench -- run --rooms 5000 --members 1000 --seconds 30
```

The benchmark starts one Orleans silo and two standalone gateways as separate processes, and
drives them with real MQTT clients over WebSockets.

## Results (2026-11-15)

**Machine:** Windows 11 (ARM64) in a virtual machine on Apple Silicon, 10 logical cores, 31 GB
RAM, .NET 10.0.15. The silo, both gateways and the load generator ran on this one machine, over
loopback. Rooms were persistent, saved with Orleans' in-memory storage provider. One run; treat
the numbers as an order of magnitude, not a guarantee.

### Many small rooms

5,000 rooms of 2 players (the two players of a room on different gateways), one command per room
every 5 seconds, for 30 seconds. Every command is saved, answered, and broadcast to both players.

| Measure | Result |
|---|---|
| Setup: 10,000 connections, 5,000 rooms created and joined | 5.2 s |
| Throughput | 997 commands/s, 0 failed |
| Request to reply | p50 5.0 ms, p95 14.8 ms, p99 33.6 ms, max 63 ms |
| Command to event at both players | p50 5.0 ms, p95 15.2 ms, p99 34.8 ms, max 64 ms |
| Memory, silo | about 10 KB per room |
| Memory, gateways | about 33 KB per connection |

### One big room

1,000 members, half on each gateway. 5 events per second room-wide, 512-byte events, 50 KB
snapshots, for 30 seconds. Every event is saved and delivered to all 1,000 members.

| Measure | Result | Starting target |
|---|---|---|
| Command to event at every member | p50 10.5 ms, **p95 23.0 ms**, p99 27.3 ms, max 53 ms | p95 < 250 ms |
| Deliveries | 145,000 of 145,000 (100%) | |
| Request to reply | p50 2.4 ms, p95 16.9 ms, p99 23.4 ms | |
| 1,000 members join (each gets a 50 KB snapshot) | 1.3 s | |
| Presence messages from those joins (about 500,000) all delivered | 5.1 s after the last join | |
| A gateway is killed: its 500 members reconnect to the other gateway and have a fresh snapshot | **0.7 s** | < 30 s |
| Memory, silo | about 6 MB for the room | |
| Memory, gateways | about 38 KB per connection | |

Memory figures are the growth of the managed heap after a full garbage collection.

## What the numbers say

- **Both starting targets were met with a wide margin** on this machine, so the default limits
  in `GrainWireGatewayOptions` and `RoomOptions` were left as designed. Nothing needed tuning.
- **Joining is the expensive moment in a big room.** Every join tells every earlier member, so
  filling a room of *n* members costs about *n²/2* presence messages. At 1,000 members that is
  half a million messages and about five seconds of catching up on this machine. This is the
  main reason rooms much larger than 1,000 members are outside v1.
- **Delivery in a big room is one message per member.** The cost of an event grows with the
  number of members, as the design accepts for this room size.

## What was not measured

- Real networks: latency, packet loss, TLS, and a load balancer in front of the gateways.
- A durable storage provider. Saving to a database adds its write latency to every command.
- More than one silo. (The functional tests cover failover across silos; this benchmark does not
  measure it.)
- Slow clients. A client that cannot keep up is disconnected once 1,000 messages are waiting
  for it, then reconnects and gets a snapshot.
- Long runs. Thirty seconds does not show slow leaks.
