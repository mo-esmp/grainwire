import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Room } from "../src/client.js";
import type { GrainWireClient } from "../src/client.js";
import type { ReplyEnvelope, SnapshotEnvelope } from "../src/protocol.js";

const key = "counter/r1";
const settings = { gapWaitMs: 250, syncIntervalMs: 15_000, reconnectDelayMs: 500, maxReconnectDelayMs: 15_000 };

const ok: ReplyEnvelope = { t: "$reply", room: key, status: "ok", code: "joined" };
const unavailable: ReplyEnvelope = { t: "$reply", room: key, status: "error", code: "unavailable" };
const snapshot: SnapshotEnvelope = { t: "$snapshot", room: key, ri: "i1", se: "e1", at: 0, d: { value: 0 }, members: [], you: { player: "alice", status: "online" } };

/** A Room on a connection that stays up, with a server whose answers to join the test decides. */
function roomWith(answerJoin: () => ReplyEnvelope | Promise<ReplyEnvelope>): { room: Room; joins: number[]; others: string[] } {
  const joins: number[] = [];
  const others: string[] = [];
  const client = {
    _connected: true,
    _forget(): void {},
    _resendCommands(): void {},
    send(): void {},
    request(kind: string): Promise<ReplyEnvelope> {
      if (kind === "join") {
        joins.push(Date.now());
        return Promise.resolve(answerJoin());
      }

      others.push(kind);
      return Promise.resolve({ t: "$reply", room: key, status: "ok", code: "in-sync" });
    },
  };
  const room = new Room(client as unknown as GrainWireClient, "counter", "r1", undefined, settings);

  // Joined and following the room, then the connection dropped and came back.
  room._joined(Date.now());
  room._receive(snapshot, Date.now());
  room._disconnected();
  return { room, joins, others };
}

/** Lets time pass the way the client does it: a tick every 100 ms. */
async function run(room: Room, ms: number): Promise<void> {
  for (let elapsed = 0; elapsed < ms; elapsed += 100) {
    await vi.advanceTimersByTimeAsync(100);
    room._tick(Date.now());
  }
}

describe("Room rejoin", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(1_000_000);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("tries again by itself when the room cannot be reached after a reconnect", async () => {
    const answers = [unavailable, unavailable, unavailable];
    const { room, joins, others } = roomWith(() => answers.shift() ?? ok);

    await room._rejoin(); // what the client does right after the reconnect
    expect(joins.length).toBe(1);

    // The connection never drops again; the room has to come back on its own.
    await run(room, 20_000);
    expect(joins.length).toBe(4);
    expect(room.closed).toBe(false);

    // Attached again: the room follows its stream (the missing snapshot is asked for), and joins stop.
    expect(others).toContain("sync");
    await run(room, 40_000);
    expect(joins.length).toBe(4);
  });

  it("waits longer between attempts while the room stays unreachable", async () => {
    const { room, joins } = roomWith(() => unavailable);
    await room._rejoin();
    await run(room, 60_000);

    // 0.5 s, 1 s, 2 s, 4 s, 8 s, then every 15 s (each with up to 25% jitter): a handful of attempts, not hundreds.
    expect(joins.length).toBeGreaterThanOrEqual(6);
    expect(joins.length).toBeLessThanOrEqual(10);
    const gaps = joins.slice(1).map((at, i) => at - joins[i]!);
    expect(gaps[0]).toBeLessThan(gaps[3]!);
    expect(Math.max(...gaps)).toBeLessThanOrEqual(15_000 * 1.25 + 100);
  });

  it("tries again when a join gets no reply at all", async () => {
    let calls = 0;
    const { room, joins } = roomWith(() => {
      if (++calls === 1) throw new Error("no reply in time");
      return ok;
    });

    await room._rejoin();
    await run(room, 2_000);
    expect(joins.length).toBe(2);
  });

  it("does not retry a room that is gone", async () => {
    const closed: string[] = [];
    const { room, joins } = roomWith(() => ({ t: "$reply", room: key, status: "error", code: "room-not-found" }));
    room.onClosed((info) => closed.push(info.reason));

    await room._rejoin();
    await run(room, 30_000);
    expect(joins.length).toBe(1);
    expect(closed).toEqual(["lost"]);
  });
});
