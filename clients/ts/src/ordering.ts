import type { EventEnvelope, SnapshotEnvelope } from "./protocol.js";

export interface OrderingOptions {
  /** How long to wait for a missing event before asking the server for a snapshot. */
  gapWaitMs: number;
  /** A room that has been silent this long is checked against the server. */
  syncIntervalMs: number;
  /** Minimum time between two sync requests while the client is still behind. */
  syncRetryMs: number;
  /** Events held while waiting for a missing one. */
  maxBuffered: number;
}

export const defaultOrderingOptions: OrderingOptions = {
  gapWaitMs: 250,
  syncIntervalMs: 15_000,
  syncRetryMs: 2_000,
  maxBuffered: 2_000,
};

export type OrderingAction =
  | { kind: "snapshot"; snapshot: SnapshotEnvelope }
  | { kind: "event"; event: EventEnvelope }
  | { kind: "sync" };

export interface StreamPosition {
  ri: string | undefined;
  se: string | undefined;
  seq: number;
}

/** Which snapshot the server sent just before a reply, as that reply names it. Fields the reply lacks are undefined. */
export interface SentSnapshot {
  ri?: string | undefined;
  se?: string | undefined;
  at?: number | undefined;
}

/**
 * Turns what arrives from the network (possibly late, twice, out of order, or not at all) into what the app may see:
 * a snapshot, then that player's events in order, each exactly once. It does no I/O and owns no timers: callers pass
 * in the current time and act on the returned actions. That keeps it testable against any network behaviour.
 */
export class OrderingEngine {
  private static readonly maxRetired = 16;
  private retired: { ri: string; se: string }[] = [];
  private refused: SnapshotEnvelope | undefined;
  private ri: string | undefined;
  private se: string | undefined;
  private position = 0;
  private hasBaseline = false;
  private buffer: EventEnvelope[] = [];
  private waitingSince: number | undefined;
  private lastSyncAt = Number.NEGATIVE_INFINITY;
  private lastActivityAt: number;

  constructor(
    private readonly options: OrderingOptions,
    now: number,
  ) {
    this.lastActivityAt = now;
  }

  /** What the client has processed so far; sent with a sync request. */
  get current(): StreamPosition {
    return { ri: this.ri, se: this.se, seq: this.position };
  }

  get ready(): boolean {
    return this.hasBaseline;
  }

  /** Call when a snapshot should have arrived (after a successful join) but may have been lost. */
  expectBaseline(now: number): void {
    if (!this.hasBaseline) this.waitingSince ??= now;
  }

  receiveSnapshot(snapshot: SnapshotEnvelope, now: number): OrderingAction[] {
    this.lastActivityAt = now;
    this.refused = undefined;
    if (this.hasBaseline) {
      if (snapshot.ri === this.ri && snapshot.se === this.se) {
        // An older baseline of the stream we are already following would move the app backwards.
        if (snapshot.at < this.position) return [];
      } else if (this.retired.some((left) => left.ri === snapshot.ri && left.se === snapshot.se)) {
        // A stream the client has left does not come back, so this snapshot was on its way for a long time. Following
        // it would show the app an old state, and events it already has a second time. It is kept for the one case in
        // which the server says otherwise (see confirmResynced).
        this.refused = snapshot;
        return [];
      }
    }

    return this.accept(snapshot, now);
  }

  receiveEvent(event: EventEnvelope, now: number): OrderingAction[] {
    this.lastActivityAt = now;

    // Before any snapshot, or from another epoch or incarnation: it can only be placed once a snapshot arrives.
    if (!this.hasBaseline || event.ri !== this.ri || event.se !== this.se) {
      this.hold(event);
      this.waitingSince ??= now;
      return [];
    }

    if (event.seq <= this.position) return [];

    if (event.seq === this.position + 1) {
      this.position = event.seq;
      const actions: OrderingAction[] = [{ kind: "event", event }];
      this.drain(actions);
      this.updateWaiting(now);
      return actions;
    }

    // A gap. Hold the event and give the missing ones a moment to arrive.
    this.hold(event);
    this.waitingSince ??= now;
    return [];
  }

  /** The server answered a sync with "in-sync": this stream is the current one and nothing up to `seq` is missing. */
  confirmInSync(now: number): void {
    this.lastActivityAt = now;

    // Anything held from another epoch or incarnation is stale, because the server just confirmed which stream is
    // current. Keeping it would make the client ask for a sync again and again. Later events of this stream were sent
    // after the server answered, so they stay: the ones before them arrive, or the next sync brings a snapshot.
    this.buffer = this.buffer.filter((e) => e.ri === this.ri && e.se === this.se && e.seq > this.position);
    this.updateWaiting(now);
  }

  /**
   * The server answered a sync with "resynced": just before, it sent a snapshot of the stream that is current, and the
   * reply says which one (`sent`). If that very snapshot was refused because it belongs to a stream the client had
   * left, the client was wrong about which of the two streams is over (it had followed a late snapshot of an epoch it
   * had never seen), and the snapshot is applied after all.
   */
  confirmResynced(sent: SentSnapshot, now: number): OrderingAction[] {
    // That a refused snapshot arrived after the sync was sent does not make it the answer: the answer can be lost, and
    // a late snapshot of the stream that really is over can arrive in its place. So it has to be the snapshot the reply
    // names, and a reply that names none (an older server) overrides nothing. Only the last snapshot to arrive is kept.
    const refused = this.refused;
    if (!refused || refused.ri !== sent.ri || refused.se !== sent.se || refused.at !== sent.at) return [];
    return this.accept(refused, now);
  }

  /** Call regularly. Returns a sync action when the client should check itself against the server. */
  tick(now: number): OrderingAction[] {
    const behind = this.waitingSince !== undefined && now - this.waitingSince >= this.options.gapWaitMs && now - this.lastSyncAt >= this.options.syncRetryMs;
    const idle = this.hasBaseline && now - this.lastActivityAt >= this.options.syncIntervalMs && now - this.lastSyncAt >= this.options.syncIntervalMs;
    if (!behind && !idle) return [];
    this.lastSyncAt = now;
    return [{ kind: "sync" }];
  }

  private accept(snapshot: SnapshotEnvelope, now: number): OrderingAction[] {
    if (this.hasBaseline && (snapshot.ri !== this.ri || snapshot.se !== this.se)) {
      // The stream the client leaves here is over. Only the last few are remembered: a snapshot is not on its way for
      // as long as it takes a room to restart that often.
      this.retired.push({ ri: this.ri!, se: this.se! });
      if (this.retired.length > OrderingEngine.maxRetired) this.retired.shift();
    }

    this.retired = this.retired.filter((left) => left.ri !== snapshot.ri || left.se !== snapshot.se);
    this.refused = undefined;
    this.ri = snapshot.ri;
    this.se = snapshot.se;
    this.position = snapshot.at;
    this.hasBaseline = true;
    this.buffer = this.buffer.filter((e) => e.ri === this.ri && e.se === this.se && e.seq > this.position);

    const actions: OrderingAction[] = [{ kind: "snapshot", snapshot }];
    this.drain(actions);
    this.updateWaiting(now);
    return actions;
  }

  private hold(event: EventEnvelope): void {
    if (this.buffer.some((e) => e.ri === event.ri && e.se === event.se && e.seq === event.seq)) return;
    this.buffer.push(event);
    if (this.buffer.length > this.options.maxBuffered) this.buffer.shift();
  }

  private drain(actions: OrderingAction[]): void {
    for (;;) {
      const index = this.buffer.findIndex((e) => e.ri === this.ri && e.se === this.se && e.seq === this.position + 1);
      if (index < 0) return;
      const [event] = this.buffer.splice(index, 1);
      this.position = event!.seq;
      actions.push({ kind: "event", event: event! });
    }
  }

  private updateWaiting(now: number): void {
    if (this.buffer.length === 0) this.waitingSince = undefined;
    else this.waitingSince ??= now;
  }
}
