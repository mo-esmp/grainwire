# ADR 0002: What changed between the design and the first implementation

Date: 2026-11-15
Status: Accepted

The [design](../specs/2026-11-15-grainwire-design.md) (approved v2) was implemented as written,
with the differences below. Each one came from something learned while building or testing.
Section numbers refer to the design.

## Differences that affect behaviour

| # | Design said | What was built | Why |
|---|---|---|---|
| 1 | Will messages are rejected at CONNECT (§7.4, §7.15). | A Will is treated like any other publish from that client: only room command topics, never routed to another client. | MQTTnet does not expose the Will when a connection is validated ([ADR 0001](0001-library-apis.md)). The security property is the same: a Will cannot reach another client. |
| 2 | One one-way call per gateway per commit; order was taken for granted (§7.9). | Each commit sends **one numbered batch** per gateway. The gateway puts a room's batches back in order and skips a missing one after 250 ms. | A test showed Orleans delivering two one-way calls to an observer out of order. Without this, clients would have re-synced constantly under load. |
| 3 | A lease renewal carries the gateway's full list, implying the room drops what is not listed (§7.9). | A renewal only adds. Gateways **retry disconnect notices** that could not be delivered. | Dropping unlisted connections races with a join that is in flight, and would detach a connection that had just attached. |
| 4 | A slow consumer is disconnected at the queue limit (§7.15). | MQTTnet drops messages at the limit; the gateway disconnects the client on the first drop. | MQTTnet has no "disconnect on overflow" setting. The effect is the same. |
| 5 | De-duplication records the outcome of every command (§7.8). | Only **successful** commands are recorded. | A rejected or failed command changed nothing, so re-evaluating a retry is safe, and rejections then cost no storage write. |
| 6 | `OnPlayerLeft` for members who leave (§7.12). | `OnPlayerLeft` runs only when a grace period runs out, not for `Kick` or `RemoveMember`. | The handler that removed the member already knows; calling a hook from inside another operation's commit would complicate the pipeline. |
| 7 | "JSON by default, via `IRoomSerializer`" (§7.5). | The envelope is always JSON in v1. `IRoomSerializer` covers app payloads and must produce JSON. | A pluggable binary envelope is a bigger feature than v1 needs. Framework payloads (`$presence`, `$closed`) use a fixed format so a custom serializer cannot break clients. |
| 8 | A reply follows its command's events implicitly. | A reply and the events of the same command may arrive in either order, and a `$closed` notice can overtake the reply to the command that closed the room. The client handles both. | Replies travel back on the call; events travel through the observer. |

| 9 | Epochs are opaque; nothing ranks them (§7.6). | Every batch a room sends to a gateway carries an **epoch order**. A gateway replaces the epoch it follows only with a higher-ranked one, and tells the room the newest epoch it has seen whenever it attaches connections; the room then ranks itself above that. Clients still treat `se` as opaque. | A late batch from an old activation could reach a gateway that had never seen that epoch, and was taken for a new epoch. The order starts from the silo's clock; the exchange with the gateway corrects it when clocks differ between silos. |
| 10 | Limits are checked when a request arrives (§7.15). | Connections per player are counted when a connection is established, and one that goes over the limit is disconnected at once. Room creations take a slot before the room is asked and give it back if nothing was created. | Requests that arrive at the same moment could all pass a check that only read the count. |

| 11 | A gateway delivers each batch as it arrives (§7.9). | A gateway keeps at most 1,024 batches per room waiting to be published and **drops new batches beyond that**. Clients see a gap and catch up with a sync. | Batches that arrived faster than they could be published piled up in memory without a limit; the per-client MQTT queue limit sits after this point and could not help. |
| 12 | Disconnect notices that failed are retried with the next lease renewal (difference 3). | They are retried **on their own**: each attempt has five seconds, eight run at a time, and a renewal never waits for them. | Retrying them one after another before the renewals let a few unreachable rooms delay the renewals of healthy rooms past their lease timeout. |

## Differences in structure

- **`AddGrainWire()` for silos lives in `GrainWire.Abstractions`**, not `GrainWire.Server`
  (§7.1), so a silo-only host does not have to reference the MQTT broker.
  `GrainWire.Abstractions` references `Microsoft.Orleans.Runtime`, and passes Orleans' code
  generator on to projects that reference it.
- **The room pipeline is part of `RoomGrain<TState>`**, not a separate `RoomEngine` class (plan,
  phase 3). It is tested through the grain interface with a fake gateway on an in-process test
  cluster, which runs in seconds. The parts that are pure are separate and unit-tested: the batch
  sequencer, the wire format, and the client's ordering engine.
- **Connection generations are timestamps.** A newer connection of the same tab wins on any
  gateway; a disconnect removes only the exact generation it names. Clocks on different gateways
  would have to be off by more than the time a reconnect takes for this to go wrong.
- **Deadlines call a hook**, `OnDeadline(name)`, instead of taking a callback, so that they can
  be restored after a restart.
- **The build order differed from the plan.** The server phases (2 to 6 and 8) were built
  together, because the pipeline, leases and persistence share one class. Every phase's "done
  when" is covered by tests.

## Found by the benchmark

- Filling a room of *n* members sends about *n²/2* presence messages. At 1,000 members that is
  about five seconds of catching up on the test machine. See [benchmarks](../benchmarks.md).
- Both starting targets were met, so no default limit was changed.

## Not done, or not verified

- **Nothing has been published.** Reserving the package names and publishing are the owner's
  decisions.
- **The CI workflow has never run.** It was written but the branch was not pushed.
- **Failover tests stop servers gracefully.** Only the benchmark kills a process outright (a
  gateway). A silo killed without warning is detected by Orleans' membership protocol; that path
  was not timed.
- **The getting-started guide was verified with locally built packages**, and its web page
  imports the client from a CDN that will only have it after publishing. The same client code
  was run from Node and, in the chess sample, in a browser.
- **A browser's token is not re-checked** while its connection stays open.
- **Limits are per gateway process**, not global.
