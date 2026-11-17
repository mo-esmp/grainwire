import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { connect } from "../src/client.js";
import type { GrainWireClient, Room } from "../src/client.js";
import type { ReplyEnvelope, SnapshotEnvelope } from "../src/protocol.js";

const key = "counter/r1";
const snapshot: SnapshotEnvelope = { t: "$snapshot", room: key, ri: "i1", se: "e1", at: 0, d: { value: 0 }, members: [], you: { player: "alice", status: "online" } };

interface Published {
  topic: string;
  cmd: string;
  at: number;
}

/** A gateway the test scripts: it records what the client publishes, and the test decides what comes back. */
const gateway = vi.hoisted(() => {
  const handlers = new Map<string, (...args: unknown[]) => void>();
  const published: { topic: string; cmd: string; at: number }[] = [];
  const socket = {
    options: {},
    on(event: string, handler: (...args: unknown[]) => void): unknown {
      handlers.set(event, handler);
      return socket;
    },
    subscribe(_topic: string, _options: unknown, done: (error: Error | null, granted: { qos: number }[]) => void): void {
      done(null, [{ qos: 0 }]);
    },
    publish(topic: string, payload: string): void {
      published.push({ topic, cmd: (JSON.parse(payload) as { cmd: string }).cmd, at: Date.now() });
    },
    reconnect(): void {},
    end(): void {},
    endAsync: (): Promise<void> => Promise.resolve(),
  };
  return { handlers, published, socket };
});

vi.mock("mqtt", () => ({ default: { connect: () => gateway.socket } }));

/** What the client has published to topics ending in `path`, oldest first. */
function sent(path: string): Published[] {
  return gateway.published.filter((p) => p.topic.endsWith(`/${path}`));
}

/** Delivers a message to the client's inbox and lets the client act on it. */
async function deliver(envelope: object): Promise<void> {
  gateway.handlers.get("message")?.("inbox", new TextEncoder().encode(JSON.stringify(envelope)));
  await vi.advanceTimersByTimeAsync(0);
}

function reply(to: Published, answer: Pick<ReplyEnvelope, "status" | "code" | "d">): Promise<void> {
  return deliver({ t: "$reply", room: key, cmd: to.cmd, ...answer });
}

/** Lets time pass, 100 ms at a time (the client's tick), until the client has published `count` messages to `path`. */
async function untilSent(path: string, count: number): Promise<Published> {
  for (let elapsed = 0; sent(path).length < count; elapsed += 100) {
    if (elapsed >= 10_000) throw new Error(`The client did not publish ${path} number ${count} within 10 s.`);
    await vi.advanceTimersByTimeAsync(100);
  }

  return sent(path)[count - 1]!;
}

/** A connected client that has joined the room and has its first snapshot. */
async function joined(): Promise<{ client: GrainWireClient; room: Room }> {
  const connecting = connect({ url: "ws://gateway.test/mqtt", clientId: "c1" });
  await vi.advanceTimersByTimeAsync(0);
  gateway.handlers.get("connect")?.({ properties: { userProperties: { "gw-player": "alice" } } });
  const client = await connecting;

  const joining = client.join("counter", "r1");
  await deliver(snapshot);
  await reply(await untilSent("sys/join", 1), { status: "ok", code: "joined" });
  return { client, room: await joining };
}

describe("a command answered with not-joined", () => {
  let client: GrainWireClient | undefined;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(1_000_000);
    gateway.handlers.clear();
    gateway.published.length = 0;
  });

  afterEach(async () => {
    await client?.close();
    client = undefined;
    vi.useRealTimers();
  });

  it("waits while the join it needs cannot be done yet, and goes out again once the room is back", async () => {
    const opened = await joined();
    client = opened.client;

    let outcome: unknown = "pending";
    const adding = opened.room.request<number>("add", { amount: 5 });
    void adding.then((result) => (outcome = result), (error: unknown) => (outcome = error));
    const first = await untilSent("cmd/add", 1);
    await reply(first, { status: "error", code: "not-joined" });

    // The join the command needs fails for a passing reason, twice.
    await reply(await untilSent("sys/join", 2), { status: "error", code: "unavailable" });
    expect(sent("cmd/add").length).toBe(1);
    await reply(await untilSent("sys/join", 3), { status: "error", code: "unavailable" });

    // The room keeps trying, a little later each time, and the command is not sent to a room that is not joined.
    const last = await untilSent("sys/join", 4);
    expect(sent("cmd/add").length).toBe(1);
    expect(outcome).toBe("pending");

    // 0.5 s, then 1 s, each with up to 25% jitter and found by a tick that comes every 100 ms.
    const joins = sent("sys/join").map((join) => join.at);
    const afterFirst = joins[2]! - joins[1]!;
    const afterSecond = joins[3]! - joins[2]!;
    expect(afterFirst).toBeGreaterThanOrEqual(375);
    expect(afterFirst).toBeLessThanOrEqual(625 + 100);
    expect(afterSecond).toBeGreaterThanOrEqual(750);
    expect(afterSecond).toBeLessThanOrEqual(1_250 + 100);

    // Back in the room: the command goes out again at once (not when its retry timer comes round), under its original id.
    await deliver(snapshot);
    await reply(last, { status: "ok", code: "joined" });
    expect(sent("cmd/add").length).toBe(2);
    const second = sent("cmd/add")[1]!;
    expect(second.cmd).toBe(first.cmd);
    await reply(second, { status: "ok", d: 5 });
    expect(await adding).toBe(5);
    expect(sent("sys/join").length).toBe(4);
    expect(sent("cmd/add").length).toBe(2);
  });

  it("does not start a join before the one that is already waiting for its next try", async () => {
    const opened = await joined();
    client = opened.client;

    const adding = opened.room.request<number>("add", { amount: 5 });
    void adding.catch(() => undefined);
    await reply(await untilSent("cmd/add", 1), { status: "error", code: "not-joined" });
    await reply(await untilSent("sys/join", 2), { status: "error", code: "unavailable" });

    // A second command, sent while the room waits to join again, is told the same.
    const subtracting = opened.room.request<number>("subtract", { amount: 1 });
    void subtracting.catch(() => undefined);
    const subtract = await untilSent("cmd/subtract", 1);
    await reply(subtract, { status: "error", code: "not-joined" });
    expect(sent("sys/join").length).toBe(2);

    // Both commands go out again once the room is back, each under its original id.
    const rejoin = await untilSent("sys/join", 3);
    await deliver(snapshot);
    await reply(rejoin, { status: "ok", code: "joined" });
    expect(sent("cmd/add").length).toBe(2);
    expect(sent("cmd/subtract").length).toBe(2);
    await reply(sent("cmd/add")[1]!, { status: "ok", d: 5 });
    const again = sent("cmd/subtract")[1]!;
    expect(again.cmd).toBe(subtract.cmd);
    await reply(again, { status: "ok", d: 4 });
    expect(await adding).toBe(5);
    expect(await subtracting).toBe(4);
    expect(sent("sys/join").length).toBe(3);
  });
});
