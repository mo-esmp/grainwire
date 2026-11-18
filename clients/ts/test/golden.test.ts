import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { OrderingEngine, defaultOrderingOptions } from "../src/ordering.js";
import { isClosed, isReply, isSnapshot } from "../src/protocol.js";
import type { ClosedEnvelope, EventEnvelope, ReplyEnvelope, SnapshotEnvelope } from "../src/protocol.js";

// The same example messages the server's tests are checked against (docs/protocol-golden).
function golden<T>(name: string): T {
  const path = fileURLToPath(new URL(`../../../docs/protocol-golden/${name}`, import.meta.url));
  return JSON.parse(readFileSync(path, "utf8")) as T;
}

describe("the protocol's example messages", () => {
  it("are recognised by type", () => {
    expect(isSnapshot(golden<SnapshotEnvelope>("snapshot.json"))).toBe(true);
    expect(isReply(golden<ReplyEnvelope>("reply-ok.json"))).toBe(true);
    expect(isClosed(golden<ClosedEnvelope>("closed.json"))).toBe(true);
    expect(isSnapshot(golden<EventEnvelope>("event.json"))).toBe(false);
  });

  it("flow through the ordering engine: snapshot at 41, then events 42, 43, 44", () => {
    const engine = new OrderingEngine(defaultOrderingOptions, 0);
    const snapshot = golden<SnapshotEnvelope>("snapshot.json");
    expect(engine.receiveSnapshot(snapshot, 0).map((a) => a.kind)).toEqual(["snapshot"]);
    expect(snapshot.you).toEqual({ player: "alice", role: "white", status: "online" });
    expect(snapshot.members).toHaveLength(2);

    for (const name of ["event.json", "event-no-data.json", "presence.json"]) {
      const actions = engine.receiveEvent(golden<EventEnvelope>(name), 1);
      expect(actions.map((a) => a.kind)).toEqual(["event"]);
    }

    expect(engine.current).toEqual({ ri: "c1a0", se: "7f30", seq: 44 });
  });

  it("carry the fields the client relies on", () => {
    expect(golden<ReplyEnvelope>("reply-ok.json")).toMatchObject({ cmd: "0b6e", status: "ok", d: { accepted: true } });
    expect(golden<ReplyEnvelope>("reply-rejected.json")).toMatchObject({ status: "rejected", code: "rejected", reason: "illegal move" });
    expect(golden<ReplyEnvelope>("reply-error.json")).toMatchObject({ status: "error", code: "room-not-found" });
    expect(golden<ReplyEnvelope>("reply-resynced.json")).toMatchObject({ status: "ok", code: "resynced", ri: "c1a0", se: "7f30", at: 41 });
    expect(golden<ClosedEnvelope>("closed.json").d).toEqual({ reason: "kicked", message: "bye" });
    expect(golden<EventEnvelope>("presence.json").d).toEqual({ player: "bob", status: "reconnecting", role: "black" });
  });
});
