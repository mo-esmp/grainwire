import { spawn, spawnSync } from "node:child_process";
import type { ChildProcess } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync } from "node:fs";
import { createServer } from "node:net";
import { fileURLToPath } from "node:url";
import { afterAll, afterEach, beforeAll, describe, expect, it } from "vitest";
import { connect, ConnectError, NotConnectedError, OutcomeUnknownError, RejectedError, RoomClosedError, RoomError } from "../src/index.js";
import type { ConnectOptions, GrainWireClient, PresenceData, Room } from "../src/index.js";

// These tests run the real client against a real GrainWire server (tests/GrainWire.TestHost), which also offers
// HTTP endpoints to lose messages and cut connections on purpose.

interface CounterView {
  value: number;
  viewer: string;
  log: string[];
}

const repoRoot = fileURLToPath(new URL("../../../", import.meta.url));
const hostProject = `${repoRoot}tests/GrainWire.TestHost`;
const hostDll = `${hostProject}/bin/Debug/net10.0/GrainWire.TestHost.dll`;

let server: ChildProcess;
let port: number;
const opened: GrainWireClient[] = [];

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const probe = createServer();
    probe.once("error", reject);
    probe.listen(0, "127.0.0.1", () => {
      const address = probe.address();
      probe.close(() => resolve(typeof address === "object" && address ? address.port : 0));
    });
  });
}

async function control(path: string): Promise<void> {
  const response = await fetch(`http://127.0.0.1:${port}/control/${path}`, { method: "POST" });
  if (!response.ok) throw new Error(`control/${path} failed: ${response.status}`);
}

function newId(): string {
  return randomBytes(16).toString("hex");
}

async function open(player: string, overrides: Partial<ConnectOptions> = {}): Promise<GrainWireClient> {
  const client = await connect({
    url: `ws://127.0.0.1:${port}/mqtt`,
    username: player,
    clientId: newId(),
    reconnectDelayMs: 100,
    maxReconnectDelayMs: 500,
    ...overrides,
  });
  opened.push(client);
  return client;
}

async function until(condition: () => boolean, what: string, timeoutMs = 10_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() > deadline) throw new Error(`Timed out waiting for: ${what}`);
    await new Promise((r) => setTimeout(r, 20));
  }
}

/** Records what a room shows the app, to check ordering. */
function watch(room: Room<CounterView>): { values: number[]; seqs: number[]; snapshots: number[]; presence: PresenceData[] } {
  const seen = { values: [] as number[], seqs: [] as number[], snapshots: [] as number[], presence: [] as PresenceData[] };
  room.onSnapshot((view) => {
    seen.snapshots.push(view.value);
    seen.values.push(view.value);
  });
  room.on<{ value: number }>("changed", (data, meta) => {
    seen.values.push(data.value);
    seen.seqs.push(meta.seq);
  });
  room.onPresence((p) => seen.presence.push(p));
  return seen;
}

beforeAll(async () => {
  if (!existsSync(hostDll)) {
    const build = spawnSync("dotnet", ["build", hostProject, "-v", "q"], { stdio: "inherit" });
    if (build.status !== 0) throw new Error("Could not build tests/GrainWire.TestHost");
  }

  port = await freePort();
  server = spawn("dotnet", [hostDll, "--port", String(port)], { stdio: ["ignore", "ignore", "inherit"] });
  const deadline = Date.now() + 90_000;
  for (;;) {
    try {
      if ((await fetch(`http://127.0.0.1:${port}/health`)).ok) break;
    } catch {
      // not up yet
    }

    if (Date.now() > deadline) throw new Error("The test server did not start.");
    await new Promise((r) => setTimeout(r, 250));
  }
}, 120_000);

afterEach(async () => {
  await Promise.all(opened.splice(0).map((client) => client.close().catch(() => undefined)));
});

afterAll(() => {
  server?.kill();
});

describe("GrainWire client against a real server", () => {
  it("joins, sends commands and receives events in order", async () => {
    const id = newId();
    const alice = await open("alice");
    const bob = await open("bob");
    expect(alice.playerId).toBe("alice");

    const aliceRoom = await alice.join<CounterView>("counter", id, { create: true, options: { start: 10 } });
    expect(aliceRoom.view).toMatchObject({ value: 10, viewer: "alice" });
    expect(aliceRoom.you).toMatchObject({ player: "alice", role: "owner", status: "online" });

    const bobRoom = await bob.join<CounterView>("counter", id, { data: { role: "spectator" } });
    expect(bobRoom.you?.role).toBe("spectator");
    expect(bobRoom.members.map((m) => m.player).sort()).toEqual(["alice", "bob"]);
    await until(() => aliceRoom.members.length === 2, "alice sees bob join");

    const seen = watch(bobRoom);
    for (let i = 1; i <= 20; i++) expect(await aliceRoom.request<number>("add", { amount: 1 })).toBe(10 + i);
    await until(() => seen.seqs.length === 20, "bob receives 20 events");
    expect(seen.seqs).toEqual(Array.from({ length: 20 }, (_, i) => i + 1));
    expect(seen.values.slice(1)).toEqual(Array.from({ length: 20 }, (_, i) => 11 + i));

    // Joining again returns the same room object.
    expect(await bob.join("counter", id)).toBe(bobRoom);
  });

  it("reports refusals and failures as different errors", async () => {
    const alice = await open("alice");
    await expect(alice.join("counter", newId())).rejects.toMatchObject({ name: "RoomError", code: "room-not-found" });
    await expect(alice.join("no-such-type", newId(), { create: true })).rejects.toBeInstanceOf(RoomError);
    await expect(alice.join("counter", newId(), { create: true, options: { deny: true } })).rejects.toMatchObject({ name: "RejectedError", code: "create-denied" });

    const room = await alice.join<CounterView>("counter", newId(), { create: true });
    const rejected = await room.request("add-then-reject", { amount: 5 }).catch((e: unknown) => e);
    expect(rejected).toBeInstanceOf(RejectedError);
    expect(rejected).toMatchObject({ code: "custom-code", reason: "nope" });
    await expect(room.request("add-then-throw", { amount: 5 })).rejects.toMatchObject({ name: "RoomError", code: "handler-error" });
    await expect(room.request("no-such-command")).rejects.toMatchObject({ code: "unknown-command" });
    expect(await room.request<number>("add", { amount: 1 })).toBe(1); // none of the above changed anything

    await expect(connect({ url: `ws://127.0.0.1:${port}/mqtt`, username: "x", clientId: "short" })).rejects.toBeInstanceOf(ConnectError);
  });

  it("follows presence: reconnecting, back online, and leaving", async () => {
    const id = newId();
    const alice = await open("alice");
    const bob = await open("bob");
    const aliceRoom = await alice.join<CounterView>("quick", id, { create: true });
    const seen = watch(aliceRoom);
    await bob.join("quick", id);
    await until(() => seen.presence.some((p) => p.player === "bob" && p.status === "online"), "bob online");

    await bob.close();
    await until(() => seen.presence.some((p) => p.player === "bob" && p.status === "reconnecting"), "bob reconnecting");
    expect(aliceRoom.members.find((m) => m.player === "bob")?.status).toBe("reconnecting");
    await until(() => seen.presence.some((p) => p.player === "bob" && p.status === "left"), "bob left");
    expect(aliceRoom.members.map((m) => m.player)).toEqual(["alice"]);
  });

  it("reconnects by itself, gets the state back, and continues without duplicates", async () => {
    const id = newId();
    const alice = await open("alice");
    const bob = await open("bob");
    const aliceRoom = await alice.join<CounterView>("counter", id, { create: true });
    const bobRoom = await bob.join<CounterView>("counter", id);
    const bobSees = watch(bobRoom);
    const aliceSees = watch(aliceRoom);
    const states: string[] = [];
    bob.onStateChange((s) => states.push(s));

    await aliceRoom.request("add", { amount: 1 });
    await control(`disconnect?clientId=${bob.clientId}`);
    await until(() => states.includes("reconnecting"), "bob notices the drop");
    await aliceRoom.request("add", { amount: 1 }); // bob misses this one
    await until(() => states.at(-1) === "connected", "bob reconnects");

    await until(() => bobSees.snapshots.includes(2), "bob gets a snapshot with the missed change");
    await until(() => aliceSees.presence.some((p) => p.player === "bob" && p.status === "online"), "alice sees bob back");
    await aliceRoom.request("add", { amount: 1 });
    await until(() => bobSees.values.at(-1) === 3, "bob receives the next event");

    // What the app saw never went backwards and never repeated an event.
    expect(bobSees.values).toEqual([...bobSees.values].sort((a, b) => a - b));
    expect(new Set(bobSees.seqs).size).toBe(bobSees.seqs.length);
    expect(aliceSees.presence.some((p) => p.status === "left")).toBe(false);
  });

  // Failure test #3 of the design.
  it("converges when the last event is lost and the room goes quiet", async () => {
    const id = newId();
    const alice = await open("alice");
    const bob = await open("bob", { syncIntervalMs: 800 });
    const aliceRoom = await alice.join<CounterView>("counter", id, { create: true });
    const bobRoom = await bob.join<CounterView>("counter", id);
    const seen = watch(bobRoom);

    await control(`drop?clientId=${bob.clientId}&type=changed&count=1`);
    await aliceRoom.request("add", { amount: 7 });

    await until(() => seen.snapshots.includes(7), "bob catches up through a sync", 6_000);
    expect(seen.seqs).toEqual([]); // the event itself was never delivered; its effect arrived with the snapshot
    await aliceRoom.request("add", { amount: 1 });
    await until(() => seen.values.at(-1) === 8, "events continue after the snapshot");
  });

  it("repairs a gap in the middle of the stream", async () => {
    const id = newId();
    const alice = await open("alice");
    const bob = await open("bob");
    const aliceRoom = await alice.join<CounterView>("counter", id, { create: true });
    const bobRoom = await bob.join<CounterView>("counter", id);
    const seen = watch(bobRoom);

    await aliceRoom.request("add", { amount: 1 });
    await control(`drop?clientId=${bob.clientId}&type=changed&count=1`);
    await aliceRoom.request("add", { amount: 1 }); // lost
    await aliceRoom.request("add", { amount: 1 }); // arrives, but cannot be placed yet
    await until(() => seen.values.at(-1) === 3, "bob ends on the right value");

    expect(seen.values).toEqual([...seen.values].sort((a, b) => a - b));
    expect(seen.snapshots.length).toBeGreaterThanOrEqual(2); // the first one, and the one that closed the gap
    await aliceRoom.request("add", { amount: 1 });
    await until(() => seen.values.at(-1) === 4, "and keeps following");
  });

  // Failure test #2 of the design.
  it("re-sends a request whose reply was lost, and the command still runs once", async () => {
    const alice = await open("alice", { retryIntervalMs: 300 });
    const room = await alice.join<CounterView>("pcounter", newId(), { create: true });
    await control(`drop?clientId=${alice.clientId}&type=$reply&count=1`);
    expect(await room.request<number>("add", { amount: 5 })).toBe(5);
    expect(await room.request<number>("add", { amount: 0 })).toBe(5);
  });

  it("reports an unknown outcome, not a rejection, when no reply ever arrives", async () => {
    const alice = await open("alice", { retryIntervalMs: 200, requestTimeoutMs: 1_000 });
    const room = await alice.join<CounterView>("counter", newId(), { create: true });
    await control(`drop?clientId=${alice.clientId}&type=$reply&count=1000`);
    await expect(room.request("add", { amount: 5 })).rejects.toBeInstanceOf(OutcomeUnknownError);
    await control(`drop?clientId=${alice.clientId}&type=$reply&count=0`);
    expect(await room.request<number>("add", { amount: 0 })).toBe(5); // it was applied, exactly once
  });

  it("finishes a request that was in flight when the connection dropped", async () => {
    const alice = await open("alice");
    const room = await alice.join<CounterView>("counter", newId(), { create: true });
    const inFlight = room.request<number>("add-async", { amount: 3 });
    await control(`drop?clientId=${alice.clientId}&type=$reply&count=1`);
    await control(`disconnect?clientId=${alice.clientId}`);
    expect(await inFlight).toBe(3);
    expect(await room.request<number>("add", { amount: 0 })).toBe(3);
  });

  it("fails fast instead of queueing while there is no connection", async () => {
    const alice = await open("alice", { reconnectDelayMs: 2_000, maxReconnectDelayMs: 2_000 });
    const room = await alice.join<CounterView>("counter", newId(), { create: true });
    await control(`disconnect?clientId=${alice.clientId}`);
    await until(() => alice.state === "reconnecting", "alice notices the drop");
    await expect(room.request("add", { amount: 1 })).rejects.toBeInstanceOf(NotConnectedError);
    expect(() => room.send("add", { amount: 1 })).toThrow(NotConnectedError);
    await until(() => alice.state === "connected", "alice reconnects", 8_000);
    expect(await room.request<number>("add", { amount: 1 })).toBe(1);
  });

  // Failure test #5 of the design.
  it("keeps a player with two tabs online while both tabs reconnect", async () => {
    const id = newId();
    const watcher = await open("watcher");
    const watcherRoom = await watcher.join<CounterView>("quick", id, { create: true });
    const watcherSees = watch(watcherRoom);
    const tab1 = await open("alice");
    const tab2 = await open("alice");
    const room1 = await tab1.join<CounterView>("quick", id);
    const room2 = await tab2.join<CounterView>("quick", id);
    const sees1 = watch(room1);
    const sees2 = watch(room2);

    await Promise.all([control(`disconnect?clientId=${tab1.clientId}`), control(`disconnect?clientId=${tab2.clientId}`)]);
    await until(() => tab1.state === "connected" && tab2.state === "connected", "both tabs reconnect");
    await new Promise((r) => setTimeout(r, 1_200)); // longer than this room type's grace period

    await watcherRoom.request("add", { amount: 4 });
    await until(() => sees1.values.at(-1) === 4 && sees2.values.at(-1) === 4, "both tabs receive the event");
    expect(sees1.seqs.at(-1)).toBe(sees2.seqs.at(-1));
    expect(watcherSees.presence.filter((p) => p.player === "alice").some((p) => p.status === "left")).toBe(false);
    expect(watcherRoom.members.find((m) => m.player === "alice")?.status).toBe("online");
  });

  it("tells the app when it is kicked, when the room closes, and when the room is lost", async () => {
    const id = newId();
    const alice = await open("alice");
    const bob = await open("bob");
    const aliceRoom = await alice.join<CounterView>("counter", id, { create: true });
    const bobRoom = await bob.join<CounterView>("counter", id);
    const bobClosed: unknown[] = [];
    bobRoom.onClosed((info) => bobClosed.push(info));

    await aliceRoom.request("kick", { player: "bob" });
    await until(() => bobClosed.length === 1, "bob is told");
    expect(bobClosed[0]).toEqual({ reason: "kicked", message: "bye" });
    expect(bobRoom.closed).toBe(true);
    await expect(bobRoom.request("add", { amount: 1 })).rejects.toBeInstanceOf(RoomClosedError);

    const aliceClosed: unknown[] = [];
    aliceRoom.onClosed((info) => aliceClosed.push(info));
    await aliceRoom.request("close");
    await until(() => aliceClosed.length === 1, "alice is told");
    expect(aliceClosed[0]).toEqual({ reason: "closed", message: "done" });

    // An in-memory room whose server-side state disappears.
    const lostRoom = await alice.join<CounterView>("counter", newId(), { create: true });
    const lost: unknown[] = [];
    lostRoom.onClosed((info) => lost.push(info));
    await lostRoom.request("deactivate");
    await until(() => lost.length === 1, "alice learns the room is lost");
    expect(lost[0]).toMatchObject({ reason: "lost" });
  });

  it("recovers a persistent room that restarted, without the app doing anything", async () => {
    const alice = await open("alice");
    const room = await alice.join<CounterView>("pcounter", newId(), { create: true });
    const seen = watch(room);
    await room.request("add", { amount: 9 });
    await room.request("deactivate");
    await until(() => seen.snapshots.filter((v) => v === 9).length >= 1, "a snapshot from the restarted room");
    expect(await room.request<number>("add", { amount: 1 })).toBe(10);
    await until(() => seen.values.at(-1) === 10, "events flow in the new epoch");
    expect(room.closed).toBe(false);
  });

  it("stops following a room after leave()", async () => {
    const id = newId();
    const alice = await open("alice");
    const bob = await open("bob");
    const aliceRoom = await alice.join<CounterView>("counter", id, { create: true });
    const bobRoom = await bob.join<CounterView>("counter", id);
    const seen = watch(bobRoom);
    await bobRoom.leave();
    await aliceRoom.request("add", { amount: 1 });
    await new Promise((r) => setTimeout(r, 300));
    expect(seen.seqs).toEqual([]);
    await expect(bobRoom.request("add", { amount: 1 })).rejects.toBeInstanceOf(RoomClosedError);
  });
});
