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

  it("forgets held events when the server says the client is in sync", () => {
    const engine = new OrderingEngine(options, 0);
    engine.receiveSnapshot(snapshot(3), 0);
    engine.receiveEvent(event(9), 10); // cannot be right if the server's position is 3
    engine.confirmInSync(20);
    expect(engine.tick(5_000)).toEqual([]);
  });
});

// ---------------------------------------------------------------- randomized network behaviour

type Packet = { kind: "event"; event: EventEnvelope } | { kind: "snapshot"; snapshot: SnapshotEnvelope };

/**
 * Simulates a room that produces events (and sometimes restarts into a new epoch) and a network that may drop,
 * duplicate and delay anything. Sync requests are answered with a snapshot, which travels the same network.
 */
function simulate(steps: { op: number; drop: boolean; duplicate: boolean; delay: number }[]): {
  delivered: { se: string; seq: number }[];
  baselines: { se: string; at: number; index: number }[];
  finalClient: { se: string | undefined; seq: number };
  finalServer: { se: string; seq: number };
} {
  let now = 0;
  let epoch = 1;
  let position = 0;
  const engine = new OrderingEngine(options, now);
  const inFlight: { at: number; packet: Packet }[] = [];
  const delivered: { se: string; seq: number }[] = [];
  const baselines: { se: string; at: number; index: number }[] = [];

  const transmit = (packet: Packet, drop: boolean, duplicate: boolean, delay: number): void => {
    if (drop) return;
    inFlight.push({ at: now + delay, packet });
    if (duplicate) inFlight.push({ at: now + delay + 7, packet });
  };

  const apply = (actions: OrderingAction[], reliable: boolean): void => {
    for (const action of actions) {
      if (action.kind === "event") delivered.push({ se: action.event.se, seq: action.event.seq });
      else if (action.kind === "snapshot") baselines.push({ se: action.snapshot.se, at: action.snapshot.at, index: delivered.length });
      else transmit({ kind: "snapshot", snapshot: snapshot(position, `e${epoch}`) }, false, false, reliable ? 1 : 30);
    }
  };

  const advance = (to: number, reliable: boolean): void => {
    while (now < to) {
      now += 10;
      inFlight.sort((a, b) => a.at - b.at);
      while (inFlight.length > 0 && inFlight[0]!.at <= now) {
        const { packet } = inFlight.shift()!;
        apply(packet.kind === "event" ? engine.receiveEvent(packet.event, now) : engine.receiveSnapshot(packet.snapshot, now), reliable);
      }

      apply(engine.tick(now), reliable);
    }
  };

  // The join: its snapshot may be lost like anything else.
  engine.expectBaseline(now);
  transmit({ kind: "snapshot", snapshot: snapshot(0, "e1") }, steps[0]?.drop ?? false, false, 5);

  for (const step of steps) {
    if (step.op === 0) {
      // The room restarts: new epoch, positions start over. Clients learn of it through a later event or sync.
      epoch++;
      position = 0;
    } else {
      position++;
      transmit({ kind: "event", event: event(position, `e${epoch}`) }, step.drop, step.duplicate, step.delay);
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
