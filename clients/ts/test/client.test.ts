import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { connect } from "../src/client.js";
import type { ConnectOptions, GrainWireClient, Room } from "../src/client.js";
import { OutcomeUnknownError, RejectedError } from "../src/errors.js";
import type { EventEnvelope, ReplyEnvelope, SnapshotEnvelope } from "../src/protocol.js";

const key = "counter/r1";

interface Counter {
  value: number;
}

function snapshotAt(at: number, value: number, se = "e1"): SnapshotEnvelope {
  return { t: "$snapshot", room: key, ri: "i1", se, at, d: { value }, members: [], you: { player: "alice", status: "online" } };
}

function changed(seq: number, value: number, se = "e1"): EventEnvelope {
  return { t: "changed", room: key, ri: "i1", se, seq, d: { value } };
}

const snapshot = snapshotAt(0, 0);

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

function reply(to: Published, answer: Pick<ReplyEnvelope, "status" | "code" | "d" | "ri" | "se" | "at">): Promise<void> {
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

/** A connected client. Closed again after the test. */
async function connected(options: Partial<ConnectOptions> = {}): Promise<GrainWireClient> {
  const connecting = connect({ url: "ws://gateway.test/mqtt", clientId: "c1", ...options });
  await vi.advanceTimersByTimeAsync(0);
  gateway.handlers.get("connect")?.({ properties: { userProperties: { "gw-player": "alice" } } });
  client = await connecting;
  return client;
}

/** A connected client that has joined the room and has its first snapshot. */
async function joined(first: SnapshotEnvelope = snapshot): Promise<{ client: GrainWireClient; room: Room<Counter> }> {
  const client = await connected();
  const joining = client.join<Counter>("counter", "r1");
  await deliver(first);
  await reply(await untilSent("sys/join", 1), { status: "ok", code: "joined" });
  return { client, room: await joining };
}

/** Records what a room shows the app, in the order the app sees it: "S5" is a snapshot with value 5, "E6#2" an event. */
function watch(room: Room<Counter>): string[] {
  const seen: string[] = [];
  room.onSnapshot((view) => seen.push(`S${view.value}`));
  room.on<Counter>("changed", (data, meta) => seen.push(`E${data.value}#${meta.seq}`));
  return seen;
}

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

describe("a room whose server restarted", () => {
  it("does not show the app a late snapshot of an epoch the room has left", async () => {
    const { room } = await joined();
    const seen = watch(room);
    await deliver(changed(1, 1));

    // The room restarted on the server, and the client follows the new epoch.
    await deliver(snapshotAt(0, 10, "e2"));
    await deliver(changed(1, 11, "e2"));

    // What was still on its way from the old epoch: its snapshot, and an event the app already has.
    await deliver(snapshot);
    await deliver(changed(1, 1));
    await deliver(changed(2, 12, "e2"));
    expect(seen).toEqual(["S0", "E1#1", "S10", "E11#1", "E12#2"]);
    expect(room.view).toEqual({ value: 10 });
  });

  it("returns from a late snapshot of an older epoch when the server answers its sync", async () => {
    // The client joins in epoch e2. A snapshot of e1, which it never saw, arrives late and looks like a restart.
    const { room } = await joined(snapshotAt(4, 4, "e2"));
    const seen = watch(room);
    await deliver(snapshotAt(7, 70, "e1"));

    // The server answers a sync with the snapshot of e2, the epoch that is current, and says in its reply which
    // snapshot that was. The room follows e2 again.
    const syncing = room.sync();
    const sync = await untilSent("sys/sync", 1);
    await deliver(snapshotAt(5, 5, "e2"));
    await reply(sync, { status: "ok", code: "resynced", ri: "i1", se: "e2", at: 5 });
    await syncing;
    await deliver(changed(6, 6, "e2"));
    expect(seen).toEqual(["S4", "S70", "S5", "E6#6"]);
  });

  it("stays on its epoch when a sync's own snapshot is lost and a late one of an older epoch arrives in its place", async () => {
    const { room } = await joined();
    const seen = watch(room);
    await deliver(changed(1, 1));
    await deliver(snapshotAt(0, 10, "e2"));
    await deliver(changed(1, 11, "e2"));

    // The server answers a sync with a snapshot of e2 (at 3), which is lost. What arrives between the request and
    // the reply is a snapshot of e1 that was still on its way.
    const syncing = room.sync();
    const sync = await untilSent("sys/sync", 1);
    await deliver(snapshot);
    await reply(sync, { status: "ok", code: "resynced", ri: "i1", se: "e2", at: 3 });
    await syncing;

    // The room is still on e2: nothing of e1 is shown a second time.
    await deliver(changed(1, 1));
    expect(seen).toEqual(["S0", "E1#1", "S10", "E11#1"]);
    expect(room.view).toEqual({ value: 10 });
  });
});

describe("a join that fails", () => {
  it("leaves the room again when the server accepted it and only the reply was lost", async () => {
    const client = await connected({ requestTimeoutMs: 1_000 });
    const failed = expect(client.join("counter", "r1")).rejects.toBeInstanceOf(OutcomeUnknownError);
    await untilSent("sys/join", 1);

    // The snapshot shows that the server has this connection in the room. The reply never comes.
    await deliver(snapshot);
    await vi.advanceTimersByTimeAsync(1_100);
    await failed;
    expect(sent("sys/leave").length).toBe(1);
  });

  it("leaves the room again when no snapshot follows the reply", async () => {
    const client = await connected({ requestTimeoutMs: 1_000 });
    const failed = expect(client.join("counter", "r1")).rejects.toBeInstanceOf(OutcomeUnknownError);
    await reply(await untilSent("sys/join", 1), { status: "ok", code: "joined" });
    await vi.advanceTimersByTimeAsync(1_100);
    await failed;
    expect(sent("sys/leave").length).toBe(1);
  });

  it("sends its cleanup again while no reply to it arrives", async () => {
    const client = await connected({ requestTimeoutMs: 1_000 });
    const failed = expect(client.join("counter", "r1")).rejects.toBeInstanceOf(OutcomeUnknownError);
    await untilSent("sys/join", 1);
    await vi.advanceTimersByTimeAsync(1_100);
    await failed;

    const first = await untilSent("sys/leave", 1);
    const second = await untilSent("sys/leave", 2);
    expect(second.cmd).toBe(first.cmd);
  });

  it("does not send its cleanup again once the app has joined the room anew", async () => {
    const client = await connected({ requestTimeoutMs: 1_000 });
    const failed = expect(client.join("counter", "r1")).rejects.toBeInstanceOf(OutcomeUnknownError);
    await untilSent("sys/join", 1);
    await vi.advanceTimersByTimeAsync(1_100);
    await failed;
    expect(sent("sys/leave").length).toBe(1);

    // The server did the cleanup, but its reply is lost too. The app joins again at once, and this time it works.
    const before = sent("sys/join").length;
    const joining = client.join<Counter>("counter", "r1");
    await deliver(snapshot);
    await reply(await untilSent("sys/join", before + 1), { status: "ok", code: "joined" });
    const room = await joining;
    const seen = watch(room);

    // A second copy of the old cleanup would take the new membership with it. None is sent,
    await vi.advanceTimersByTimeAsync(6_000);
    expect(sent("sys/leave").length).toBe(1);

    // and the room goes on as joined.
    await deliver(changed(1, 1));
    expect(seen).toEqual(["S0", "E1#1"]);
  });

  it("has nothing to leave when the server refused it", async () => {
    const client = await connected();
    const failed = expect(client.join("counter", "r1")).rejects.toBeInstanceOf(RejectedError);
    await reply(await untilSent("sys/join", 1), { status: "rejected", code: "room-full" });
    await failed;
    await vi.advanceTimersByTimeAsync(1_000);
    expect(sent("sys/leave").length).toBe(0);
  });
});

describe("a command answered with not-joined", () => {
  it("waits while the join it needs cannot be done yet, and goes out again once the room is back", async () => {
    const opened = await joined();

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

  it("is not sent again by its retry timer while the room is still waiting to join", async () => {
    const opened = await joined();

    const adding = opened.room.request<number>("add", { amount: 5 });
    void adding.catch(() => undefined);
    const first = await untilSent("cmd/add", 1);
    await reply(first, { status: "error", code: "not-joined" });

    // The join keeps failing for longer than the retry timer of the command (3 s): 0.5 s, 1 s, 2 s, then 4 s.
    for (let attempt = 2; attempt <= 5; attempt++) {
      await reply(await untilSent("sys/join", attempt), { status: "error", code: "unavailable" });
    }

    const last = await untilSent("sys/join", 6);
    expect(last.at - first.at).toBeGreaterThan(3_000);
    expect(sent("cmd/add").length).toBe(1);

    // Back in the room: now it goes out, under its original id.
    await deliver(snapshot);
    await reply(last, { status: "ok", code: "joined" });
    expect(sent("cmd/add").length).toBe(2);
    expect(sent("cmd/add")[1]!.cmd).toBe(first.cmd);
    await reply(sent("cmd/add")[1]!, { status: "ok", d: 5 });
    expect(await adding).toBe(5);
  });
});

describe("a command that waits for its reply across a reconnect", () => {
  /** The connection drops and comes back; the client then joins its rooms again. */
  async function reconnected(): Promise<void> {
    gateway.handlers.get("close")?.();
    await vi.advanceTimersByTimeAsync(1_000);
    gateway.handlers.get("connect")?.({ properties: { userProperties: { "gw-player": "alice" } } });
    await vi.advanceTimersByTimeAsync(0);
  }

  it("goes out again once its room is joined again", async () => {
    const opened = await joined();

    const adding = opened.room.request<number>("add", { amount: 5 });
    void adding.catch(() => undefined);
    const first = await untilSent("cmd/add", 1);

    await reconnected();
    const rejoin = await untilSent("sys/join", 2);
    expect(sent("cmd/add").length).toBe(1);
    await deliver(snapshot);
    await reply(rejoin, { status: "ok", code: "joined" });

    expect(sent("cmd/add").length).toBe(2);
    expect(sent("cmd/add")[1]!.cmd).toBe(first.cmd);
    await reply(sent("cmd/add")[1]!, { status: "ok", d: 5 });
    expect(await adding).toBe(5);
  });

  it("waits for a room that could not be joined again yet", async () => {
    const opened = await joined();

    const adding = opened.room.request<number>("add", { amount: 5 });
    void adding.catch(() => undefined);
    const first = await untilSent("cmd/add", 1);

    // The join after the reconnect fails for a passing reason: the room is not joined, so the command stays back.
    await reconnected();
    await reply(await untilSent("sys/join", 2), { status: "error", code: "unavailable" });
    expect(sent("cmd/add").length).toBe(1);

    // The room tries again, and takes its command with it.
    const rejoin = await untilSent("sys/join", 3);
    expect(sent("cmd/add").length).toBe(1);
    await deliver(snapshot);
    await reply(rejoin, { status: "ok", code: "joined" });
    expect(sent("cmd/add").length).toBe(2);
    expect(sent("cmd/add")[1]!.cmd).toBe(first.cmd);
    await reply(sent("cmd/add")[1]!, { status: "ok", d: 5 });
    expect(await adding).toBe(5);
  });
});
