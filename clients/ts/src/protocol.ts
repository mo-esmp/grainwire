// Shapes of the messages on the wire. See docs/protocol.md.

export type PresenceStatus = "online" | "reconnecting" | "left";

export type ClosedReason = "closed" | "kicked" | "removed" | "lost";

export interface Member {
  player: string;
  role?: string;
  status: PresenceStatus;
}

/** A sequenced event: an app event, or `$presence`. */
export interface EventEnvelope {
  t: string;
  room: string;
  ri: string;
  se: string;
  seq: number;
  d?: unknown;
}

export interface SnapshotEnvelope {
  t: "$snapshot";
  room: string;
  ri: string;
  se: string;
  at: number;
  d: unknown;
  members: Member[];
  you: Member;
}

export interface ReplyEnvelope {
  t: "$reply";
  room: string;
  cmd?: string;
  status: "ok" | "rejected" | "error";
  code?: string;
  reason?: string;
  ri?: string;
  /** With `at`: which snapshot the server sent just before this reply (the snapshot's own `se` and `at`). */
  se?: string;
  at?: number;
  d?: unknown;
}

export interface ClosedEnvelope {
  t: "$closed";
  room: string;
  ri?: string;
  d: { reason: ClosedReason; message?: string };
}

export interface PresenceData {
  player: string;
  status: PresenceStatus;
  role?: string;
}

export type InboxEnvelope = EventEnvelope | SnapshotEnvelope | ReplyEnvelope | ClosedEnvelope;

export const Types = {
  snapshot: "$snapshot",
  presence: "$presence",
  reply: "$reply",
  closed: "$closed",
} as const;

export function inboxTopic(clientId: string): string {
  return `clients/${clientId}/inbox`;
}

export function isSnapshot(envelope: { t: string }): envelope is SnapshotEnvelope {
  return envelope.t === Types.snapshot;
}

export function isReply(envelope: { t: string }): envelope is ReplyEnvelope {
  return envelope.t === Types.reply;
}

export function isClosed(envelope: { t: string }): envelope is ClosedEnvelope {
  return envelope.t === Types.closed;
}
