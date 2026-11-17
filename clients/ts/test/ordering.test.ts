import fc from "fast-check";
import { describe, expect, it } from "vitest";
import { OrderingEngine } from "../src/ordering.js";
import type { OrderingAction, OrderingOptions } from "../src/ordering.js";
import type { EventEnvelope, SnapshotEnvelope } from "../src/protocol.js";

const options: OrderingOptions = { gapWaitMs: 250, syncIntervalMs: 15_000, syncRetryMs: 2_000, maxBuffered: 2_000 };
const room = "counter/r1";

function event(seq: number, se = "e1", ri = "i1"): EventEnvelope {
  return { t: "changed", room, ri, se, seq, d: { seq } };
}

function snapshot(at: number, se = "e1", ri = "i1"): SnapshotEnvelope {
  return { t: "$snapshot", room, ri, se, at, d: { at }, members: [], you: { player: "p", status: "online" } };
}

function seqs(actions: OrderingAction[]): (number | string)[] {
  return actions.map((a) => (a.kind === "event" ? a.event.seq : a.kind === "snapshot" ? `S${a.snapshot.at}` : "sync"));
}

describe("OrderingEngine", () => {
  it("delivers events in order after the snapshot and drops duplicates", () => {
    const engine = new OrderingEngine(options, 0);
    expect(seqs(engine.receiveSnapshot(snapshot(0), 0))).toEqual(["S0"]);
    expect(seqs(engine.receiveEvent(event(1), 1))).toEqual([1]);
    expect(seqs(engine.receiveEvent(event(1), 2))).toEqual([]);
    expect(seqs(engine.receiveEvent(event(2), 3))).toEqual([2]);
    expect(engine.current).toEqual({ ri: "i1", se: "e1", seq: 2 });
  });

  it("holds events that arrive before the snapshot and drops the ones the snapshot already covers", () => {
    const engine = new OrderingEngine(options, 0);
    expect(engine.receiveEvent(event(41), 0)).toEqual([]);
    expect(engine.receiveEvent(event(42), 0)).toEqual([]);
    expect(engine.receiveEvent(event(43), 0)).toEqual([]);
    expect(seqs(engine.receiveSnapshot(snapshot(41), 1))).toEqual(["S41", 42, 43]);
  });

  it("puts reordered events back in order without asking the server", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(0), 0);
    expect(seqs(engine.receiveEvent(event(2), 10))).toEqual([]);
    expect(seqs(engine.receiveEvent(event(3), 20))).toEqual([]);
    expect(engine.tick(100)).toEqual([]); // still within the gap wait
    expect(seqs(engine.receiveEvent(event(1), 120))).toEqual([1, 2, 3]);
    expect(engine.tick(1_000)).toEqual([]);
  });

  it("asks for a sync when a gap does not close, and not more often than the retry interval", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(0), 0);
    engine.receiveEvent(event(2), 10);
    expect(engine.tick(200)).toEqual([]);
    expect(seqs(engine.tick(260))).toEqual(["sync"]);
    expect(engine.tick(300)).toEqual([]);
    expect(seqs(engine.tick(2_300))).toEqual(["sync"]);

    // The snapshot that answers the sync covers the hole.
    expect(seqs(engine.receiveSnapshot(snapshot(2), 2_400))).toEqual(["S2"]);
    expect(engine.tick(10_000)).toEqual([]);
    expect(seqs(engine.receiveEvent(event(3), 10_001))).toEqual([3]);
  });

  it("checks a silent room against the server (a lost final event)", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(41), 0);
    expect(engine.tick(14_999)).toEqual([]);
    expect(seqs(engine.tick(15_000))).toEqual(["sync"]);
    engine.confirmInSync(15_050);
    expect(engine.tick(20_000)).toEqual([]);
    expect(seqs(engine.tick(30_050))).toEqual(["sync"]);
  });

  it("asks for a sync when the first snapshot never arrives", () => {
    const engine = new OrderingEngine(options, 0);
    engine.expectBaseline(0);
    expect(engine.tick(100)).toEqual([]);
    expect(seqs(engine.tick(300))).toEqual(["sync"]);
    expect(engine.ready).toBe(false);
  });

  it("switches to a new stream epoch only through a snapshot", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(5), 0);
    expect(engine.receiveEvent(event(1, "e2"), 10)).toEqual([]);
    expect(engine.receiveEvent(event(2, "e2"), 11)).toEqual([]);
    expect(seqs(engine.tick(300))).toEqual(["sync"]);
    expect(seqs(engine.receiveSnapshot(snapshot(0, "e2"), 320))).toEqual(["S0", 1, 2]);

    // A late event of the old epoch is no longer deliverable.
    expect(engine.receiveEvent(event(6, "e1"), 330)).toEqual([]);
    expect(engine.current).toEqual({ ri: "i1", se: "e2", seq: 2 });
  });

  it("ignores an older snapshot of the stream it is already following", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(0), 0);
    engine.receiveEvent(event(1), 1);
    engine.receiveEvent(event(2), 2);
    expect(engine.receiveSnapshot(snapshot(1), 3)).toEqual([]);
    expect(engine.current.seq).toBe(2);
    expect(seqs(engine.receiveSnapshot(snapshot(2), 4))).toEqual(["S2"]);
  });

  it.each([
    ["e2", "i1"], // the room restarted: a new stream epoch
    ["e9", "i2"], // the room was closed and created again: a new incarnation
  ])("refuses a late snapshot of a stream it has left (%s, %s)", (se, ri) => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(0), 0);
    expect(seqs(engine.receiveEvent(event(1), 1))).toEqual([1]);
    expect(seqs(engine.receiveSnapshot(snapshot(0, se, ri), 10))).toEqual(["S0"]);
    expect(seqs(engine.receiveEvent(event(1, se, ri), 11))).toEqual([1]);

    // A snapshot of the old stream that was still on its way does not bring that stream back,
    expect(engine.receiveSnapshot(snapshot(0), 20)).toEqual([]);

    // so a late copy of an event the app already has cannot be delivered a second time.
    expect(engine.receiveEvent(event(1), 21)).toEqual([]);
    expect(engine.current).toEqual({ ri, se, seq: 1 });
    expect(seqs(engine.receiveEvent(event(2, se, ri), 22))).toEqual([2]);
  });

  it("returns to a stream it left when the server answers a sync with a snapshot of it", () => {
    // The client followed e2 and then took a late snapshot of e1, an epoch it had never seen, for a restart.
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(4, "e2"), 0);
    expect(seqs(engine.receiveSnapshot(snapshot(7, "e1"), 10))).toEqual(["S7"]);

    // An event of e2 shows that something is wrong. The server answers the sync with e2's snapshot and "resynced".
    expect(engine.receiveEvent(event(6, "e2"), 20)).toEqual([]);
    expect(seqs(engine.tick(300))).toEqual(["sync"]);
    expect(engine.receiveSnapshot(snapshot(5, "e2"), 320)).toEqual([]);
    expect(seqs(engine.confirmResynced(300, 321))).toEqual(["S5", 6]);
    expect(engine.current).toEqual({ ri: "i1", se: "e2", seq: 6 });

    // Now e1 is the stream that was left.
    expect(engine.receiveSnapshot(snapshot(7, "e1"), 330)).toEqual([]);
    expect(engine.tick(5_000)).toEqual([]);
  });

  it("stays away from a stream it left when the sync was answered with another snapshot", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(0), 0);
    engine.receiveSnapshot(snapshot(0, "e2"), 10);

    // Refused before the sync was sent: it cannot be the answer.
    expect(engine.receiveSnapshot(snapshot(0), 20)).toEqual([]);
    expect(engine.confirmResynced(100, 110)).toEqual([]);

    // The answer was a snapshot of the stream the client follows; a late one of the old stream came right after it.
    expect(seqs(engine.receiveSnapshot(snapshot(3, "e2"), 220))).toEqual(["S3"]);
    expect(engine.receiveSnapshot(snapshot(0), 225)).toEqual([]);
    expect(engine.confirmResynced(200, 230)).toEqual([]);
    expect(engine.current).toEqual({ ri: "i1", se: "e2", seq: 3 });
  });

  it("forgets held events of an older stream when the server says the client is in sync", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(3, "e2"), 0);
    engine.receiveEvent(event(9, "e1"), 10); // a late event of the epoch before
    expect(seqs(engine.tick(300))).toEqual(["sync"]);
    engine.confirmInSync(320);

    // The stale event is gone, so the client does not keep asking.
    expect(engine.tick(5_000)).toEqual([]);
    expect(engine.tick(10_000)).toEqual([]);
  });

  it("stops asking after a failover, however often the server answers in-sync", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(5, "e1"), 0);
    engine.receiveSnapshot(snapshot(2, "e2"), 10); // the room restarted
    engine.receiveEvent(event(6, "e1"), 20); // a late event of the old epoch

    // One minute of ticks, every sync answered with "in-sync".
    let syncs = 0;
    for (let now = 100; now <= 60_000; now += 100) {
      if (engine.tick(now).length > 0) {
        syncs++;
        engine.confirmInSync(now + 10);
      }
    }

    // One sync to find out about the held event, and then only the regular check of a silent room (every 15 s).
    expect(syncs).toBeLessThanOrEqual(4);
    expect(engine.current).toEqual({ ri: "i1", se: "e2", seq: 2 });
  });

  it("keeps later events of the current stream when the server says the client is in sync", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(3), 0);
    engine.receiveEvent(event(5), 10); // sent after the server answered the sync
    engine.confirmInSync(20);
    expect(seqs(engine.receiveEvent(event(4), 30))).toEqual([4, 5]);
    expect(engine.tick(5_000)).toEqual([]);
  });
});

// ---------------------------------------------------------------- randomized network behaviour

type Packet = { kind: "event"; event: EventEnvelope } | { kind: "snapshot"; snapshot: SnapshotEnvelope } | { kind: "resynced"; id: number; askedAt: number };

interface Step {
  /** 0 = the room restarts, otherwise an event. */
  op: number;
  /** The packet is lost. */
  drop: boolean;
  /** A copy of the packet arrives after it. */
  duplicate: boolean;
  /** How long the packet takes, in milliseconds. */
  delay: number;
  /** How much longer than a few milliseconds the copy takes. Mostly 0. */
  late: number;
}

/**
 * Simulates a room that produces events (and sometimes restarts into a new epoch) and a network that may drop,
 * duplicate and delay anything. Sync requests are answered with a snapshot and then "resynced", which travel the same
 * network.
 */
function simulate(steps: Step[]): {
  delivered: { se: string; seq: number }[];
  baselines: { se: string; at: number; index: number }[];
  finalClient: { se: string | undefined; seq: number };
  finalServer: { se: string; seq: number };
} {
  let now = 0;
  let epoch = 1;
  let position = 0;
  let syncs = 0;
  let current: Step = { op: 1, drop: false, duplicate: false, delay: 0, late: 0 };
  const engine = new OrderingEngine(options, now);
  const inFlight: { at: number; packet: Packet }[] = [];
  const delivered: { se: string; seq: number }[] = [];
  const baselines: { se: string; at: number; index: number }[] = [];

  const transmit = (packet: Packet, step: Step, delay: number): void => {
    if (step.drop) return;
    inFlight.push({ at: now + delay, packet });
    if (step.duplicate) inFlight.push({ at: now + delay + 7 + step.late, packet });
  };

  // The server answers a sync with a snapshot and then "resynced". Until the network calms down the answer may take
  // seconds, or a copy of its snapshot may arrive seconds later.
  const answerSync = (reliable: boolean): void => {
    const answer: Packet = { kind: "snapshot", snapshot: snapshot(position, `e${epoch}`) };
    const delay = reliable ? 1 : current.duplicate ? 30 : 30 + current.late;
    inFlight.push({ at: now + delay, packet: answer });
    inFlight.push({ at: now + delay, packet: { kind: "resynced", id: ++syncs, askedAt: now } });
    if (!reliable && current.duplicate) inFlight.push({ at: now + delay + 7 + current.late, packet: answer });
  };

  const apply = (actions: OrderingAction[], reliable: boolean): void => {
    for (const action of actions) {
      if (action.kind === "event") delivered.push({ se: action.event.se, seq: action.event.seq });
      else if (action.kind === "snapshot") baselines.push({ se: action.snapshot.se, at: action.snapshot.at, index: delivered.length });
      else answerSync(reliable);
    }
  };

  const arrive = (packet: Packet): OrderingAction[] => {
    if (packet.kind === "event") return engine.receiveEvent(packet.event, now);
    if (packet.kind === "snapshot") return engine.receiveSnapshot(packet.snapshot, now);

    // Like the client, the simulation waits for one answer at a time, and not for longer than five seconds.
    return packet.id === syncs && now - packet.askedAt < 5_000 ? engine.confirmResynced(packet.askedAt, now) : [];
  };

  const advance = (to: number, reliable: boolean): void => {
    while (now < to) {
      now += 10;
      inFlight.sort((a, b) => a.at - b.at);
      while (inFlight.length > 0 && inFlight[0]!.at <= now) {
        apply(arrive(inFlight.shift()!.packet), reliable);
      }

      apply(engine.tick(now), reliable);
    }
  };

  // The join: its snapshot may be lost like anything else.
  engine.expectBaseline(now);
  transmit({ kind: "snapshot", snapshot: snapshot(0, "e1") }, steps[0] ?? current, 5);

  for (const step of steps) {
    current = step;
    if (step.op === 0) {
      // The room restarts: new epoch, positions start over. Clients learn of it through a later event or sync.
      epoch++;
      position = 0;
    } else {
      position++;
      transmit({ kind: "event", event: event(position, `e${epoch}`) }, step, step.delay);
    }

    advance(now + 20, false);
  }

  // The network calms down; give the engine time to notice anything it missed (idle sync included).
  advance(now + 40_000, true);
  return { delivered, baselines, finalClient: { se: engine.current.se, seq: engine.current.seq }, finalServer: { se: `e${epoch}`, seq: position } };
}

describe("OrderingEngine under an unreliable network", () => {
  it("never delivers an event twice or out of order, and always converges", () => {
    fc.assert(
      fc.property(
        fc.array(
          fc.record({
            op: fc.integer({ min: 0, max: 14 }), // 0 = the room restarts, otherwise an event
            drop: fc.integer({ min: 0, max: 3 }).map((n) => n === 0),
            duplicate: fc.boolean(),
            delay: fc.integer({ min: 0, max: 400 }),
            // One step in four is late by up to six seconds.
            late: fc.tuple(fc.integer({ min: 0, max: 3 }), fc.integer({ min: 1, max: 6_000 })).map(([n, ms]) => (n === 0 ? ms : 0)),
          }),
          { minLength: 1, maxLength: 80 },
        ),
        (steps) => {
          const { delivered, baselines, finalClient, finalServer } = simulate(steps);

          // Between two baselines, events are consecutive, starting right after the baseline.
          for (let b = 0; b < baselines.length; b++) {
            const from = baselines[b]!.index;
            const to = b + 1 < baselines.length ? baselines[b + 1]!.index : delivered.length;
            let expected = baselines[b]!.at + 1;
            for (let i = from; i < to; i++) {
              expect(delivered[i]!.se).toBe(baselines[b]!.se);
              expect(delivered[i]!.seq).toBe(expected);
              expected++;
            }
          }

          // Across baselines too: no event reaches the app a second time.
          expect(new Set(delivered.map((d) => `${d.se}:${d.seq}`)).size).toBe(delivered.length);

          // Nothing is delivered before the first baseline.
          if (baselines.length > 0) expect(baselines[0]!.index).toBe(0);
          else expect(delivered).toEqual([]);

          // Eventual convergence: the client ends where the room is.
          expect(finalClient).toEqual(finalServer);
        },
      ),
      { numRuns: 300 },
    );
  });
});
