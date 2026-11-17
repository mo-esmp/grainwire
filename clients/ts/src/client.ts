// The default import works in both of MQTT.js's builds; its browser bundle has no named exports.
import mqtt from "mqtt";
import type { IClientOptions, MqttClient } from "mqtt";
import { ConnectError, NotConnectedError, OutcomeUnknownError, RejectedError, RoomClosedError, RoomError } from "./errors.js";
import { defaultOrderingOptions, OrderingEngine } from "./ordering.js";
import type { OrderingAction } from "./ordering.js";
import { inboxTopic, isClosed, isReply, isSnapshot, Types } from "./protocol.js";
import type { ClosedEnvelope, ClosedReason, EventEnvelope, Member, PresenceData, ReplyEnvelope, SnapshotEnvelope } from "./protocol.js";

export type ConnectionState = "connected" | "reconnecting" | "disconnected";

export interface ConnectOptions {
  /** The gateway's WebSocket endpoint, for example `wss://example.com/mqtt`. */
  url: string;
  /** Access token, sent as the MQTT password. A function is called again before every reconnect. */
  token?: string | (() => string | Promise<string>);
  /** MQTT user name. What it means is up to the server's authenticator. */
  username?: string;
  /** Defaults to a random id kept per browser tab, so a reconnect is recognised as the same tab. */
  clientId?: string;
  /** A room that has been silent this long is checked against the server. Default 15 s. */
  syncIntervalMs?: number;
  /** How long to wait for a missing event before asking for a snapshot. Default 250 ms. */
  gapWaitMs?: number;
  /** How long `request()` waits for a reply before failing with OutcomeUnknownError. Default 15 s. */
  requestTimeoutMs?: number;
  /** A request without a reply is re-sent this often, with the same command id. Default 3 s. */
  retryIntervalMs?: number;
  /** First reconnect delay; it doubles up to `maxReconnectDelayMs`. Default 500 ms. */
  reconnectDelayMs?: number;
  /** Default 15 s. */
  maxReconnectDelayMs?: number;
  /** How long the first connection may take. Default 10 s. */
  connectTimeoutMs?: number;
  /** Extra options passed to MQTT.js. */
  mqtt?: Partial<IClientOptions>;
}

export interface JoinOptions {
  /** Create the room if it does not exist. Default false. */
  create?: boolean;
  /** Room settings, used only when the room is created. */
  options?: unknown;
  /** App-defined join data, such as a requested role or an invite code. */
  data?: unknown;
}

export interface RequestOptions {
  timeoutMs?: number;
}

export interface SnapshotInfo {
  members: readonly Member[];
  you: Member;
}

export interface ClosedInfo {
  reason: ClosedReason;
  message?: string;
}

export interface EventMeta {
  seq: number;
}

type Unsubscribe = () => void;
type RequestKind = "join" | "sync" | "leave" | "command";

interface Pending {
  cmd: string;
  kind: RequestKind;
  roomKey: string;
  topic: string;
  payload: string;
  deadline: number;
  lastSentAt: number;
  resolve: (reply: ReplyEnvelope) => void;
  reject: (error: Error) => void;
}

interface Settings {
  syncIntervalMs: number;
  gapWaitMs: number;
  requestTimeoutMs: number;
  retryIntervalMs: number;
  reconnectDelayMs: number;
  maxReconnectDelayMs: number;
}

/** Connects to a GrainWire gateway. Resolves once the connection is up and the inbox is subscribed. */
export function connect(options: ConnectOptions): Promise<GrainWireClient> {
  return GrainWireClient.open(options);
}

export class GrainWireClient {
  /** The MQTT client id of this tab. */
  readonly clientId: string;

  private readonly settings: Settings;
  private readonly rooms = new Map<string, Room<unknown>>();
  private readonly joining = new Map<string, Promise<Room<unknown>>>();
  private readonly pending = new Map<string, Pending>();
  private readonly listeners = new Set<(state: ConnectionState) => void>();
  private mqtt!: MqttClient;
  private ticker: ReturnType<typeof setInterval> | undefined;
  private reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  private reconnectDelay: number;
  private closedByUser = false;
  private _state: ConnectionState = "disconnected";
  private _playerId: string | undefined;

  private constructor(private readonly options: ConnectOptions) {
    this.clientId = options.clientId ?? defaultClientId();
    this.settings = {
      syncIntervalMs: options.syncIntervalMs ?? defaultOrderingOptions.syncIntervalMs,
      gapWaitMs: options.gapWaitMs ?? defaultOrderingOptions.gapWaitMs,
      requestTimeoutMs: options.requestTimeoutMs ?? 15_000,
      retryIntervalMs: options.retryIntervalMs ?? 3_000,
      reconnectDelayMs: options.reconnectDelayMs ?? 500,
      maxReconnectDelayMs: options.maxReconnectDelayMs ?? 15_000,
    };
    this.reconnectDelay = this.settings.reconnectDelayMs;
  }

  /** @internal */
  static async open(options: ConnectOptions): Promise<GrainWireClient> {
    const client = new GrainWireClient(options);
    await client.start();
    return client;
  }

  get state(): ConnectionState {
    return this._state;
  }

  /** The player id the server derived from the credentials. */
  get playerId(): string | undefined {
    return this._playerId;
  }

  /** Called whenever the connection state changes. Returns a function that removes the listener. */
  onStateChange(listener: (state: ConnectionState) => void): Unsubscribe {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /**
   * Joins a room (creating it when `create` is set). Resolves once the first snapshot has been processed, so the
   * returned Room has its `view`, `members` and `you`. Joining a room this client is already in returns the same Room.
   *
   * Rejects with OutcomeUnknownError when no reply arrived in time, or the server accepted the join but no snapshot
   * followed. The player may then be a member of the room; joining again is safe.
   */
  async join<TView = unknown>(type: string, id: string, options: JoinOptions = {}): Promise<Room<TView>> {
    const key = `${type}/${id}`;

    // A second call while the first is still joining waits for the same outcome, so it never gets a half-joined room.
    const inFlight = this.joining.get(key);
    if (inFlight) return inFlight as Promise<Room<TView>>;
    const existing = this.rooms.get(key);
    if (existing) return existing as Room<TView>;
    if (this._state !== "connected") throw new NotConnectedError();

    const joining = this.joinNew<TView>(key, type, id, options);
    this.joining.set(key, joining as Promise<Room<unknown>>);
    try {
      return await joining;
    } finally {
      this.joining.delete(key);
    }
  }

  private async joinNew<TView>(key: string, type: string, id: string, options: JoinOptions): Promise<Room<TView>> {
    const room = new Room<TView>(this, type, id, options.data, this.settings);
    this.rooms.set(key, room as Room<unknown>);
    try {
      const reply = await this.request("join", key, "sys/join", { create: options.create === true, options: options.options, data: options.data });
      interpret(reply);
      room._joined(Date.now());

      // The reply and the snapshot can arrive in either order, and the snapshot can be lost. If it is, the room asks
      // for one with a sync (see OrderingEngine.expectBaseline); this waits for whichever arrives.
      await room._whenReady(this.settings.requestTimeoutMs, reply.cmd ?? "");
      return room;
    } catch (error) {
      if (this.rooms.get(key) === (room as Room<unknown>)) this.rooms.delete(key);
      const outcomeUnknown = error instanceof OutcomeUnknownError && !room.closed;
      room._close("removed", undefined, false);

      // The server may have this connection in the room all the same: the reply was lost, or no snapshot followed
      // it. Stop the room's events from coming here. A gateway that never saw the join answers "left" as well.
      if (outcomeUnknown && this._connected) void this.request("leave", key, "sys/leave", {}, 5_000).catch(() => undefined);
      throw error;
    }
  }

  /** Closes the connection. Rooms stop receiving events; requests in flight fail. */
  async close(): Promise<void> {
    this.closedByUser = true;
    if (this.ticker) clearInterval(this.ticker);
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    for (const pending of [...this.pending.values()]) this.settle(pending, undefined, new NotConnectedError());
    this.setState("disconnected");
    await this.mqtt.endAsync(true).catch(() => undefined);
  }

  // ---------------------------------------------------------------- used by Room

  /** @internal */
  get _connected(): boolean {
    return this._state === "connected";
  }

  /** @internal */
  _forget(room: Room<unknown>): void {
    if (this.rooms.get(room.key) === room) this.rooms.delete(room.key);
    // Commands in flight keep waiting for their own reply: the notice that closed the room can overtake the reply
    // to the very command that closed it. Only the room's housekeeping requests are abandoned.
    for (const pending of [...this.pending.values()]) {
      if (pending.roomKey === room.key && (pending.kind === "sync" || pending.kind === "join")) {
        this.settle(pending, undefined, new RoomClosedError(room.closedReason ?? "left"));
      }
    }
  }

  /** @internal Sends a request and resolves with its reply, whatever its status. */
  request(kind: RequestKind, roomKey: string, path: string, body: Record<string, unknown>, timeoutMs?: number): Promise<ReplyEnvelope> {
    if (this._state !== "connected" && kind !== "join") return Promise.reject(new NotConnectedError());
    const cmd = newId();
    const now = Date.now();
    return new Promise<ReplyEnvelope>((resolve, reject) => {
      const pending: Pending = {
        cmd,
        kind,
        roomKey,
        topic: `rooms/${roomKey}/${path}`,
        payload: JSON.stringify({ ...body, cmd }),
        deadline: now + (timeoutMs ?? this.settings.requestTimeoutMs),
        lastSentAt: now,
        resolve,
        reject,
      };
      this.pending.set(cmd, pending);
      this.publish(pending);
    });
  }

  /** @internal Fire-and-forget. */
  send(roomKey: string, path: string, body: Record<string, unknown>): void {
    if (this._state !== "connected") throw new NotConnectedError();
    this.mqtt.publish(`rooms/${roomKey}/${path}`, JSON.stringify({ ...body, cmd: newId(), noreply: true }), { qos: 1 });
  }

  /**
   * @internal Sends the commands that still wait for a reply again, under their original command ids: all of them, or
   * only those of one room. Commands of a room that is not joined at the moment stay back (see waitsForRoom).
   */
  _resendCommands(roomKey?: string): void {
    if (this._state !== "connected") return;
    for (const pending of this.pending.values()) {
      if (pending.kind !== "command" || (roomKey !== undefined && pending.roomKey !== roomKey)) continue;
      if (!this.waitsForRoom(pending)) this.publish(pending);
    }
  }

  /**
   * True for a command whose room is not joined on the server just now, as far as the client knows. Sending it would
   * only bring back "not-joined"; the room sends it once it is back. A command of a room that has closed has no room
   * to wait for and is sent, so that its own reply can still arrive.
   */
  private waitsForRoom(pending: Pending): boolean {
    if (pending.kind !== "command") return false;
    const room = this.rooms.get(pending.roomKey);
    return room !== undefined && !room._attached;
  }

  // ---------------------------------------------------------------- connection

  private async start(): Promise<void> {
    const password = await this.resolveToken();
    await new Promise<void>((resolve, reject) => {
      let settled = false;
      const timeout = setTimeout(() => fail(new ConnectError("Timed out while connecting.", undefined)), this.options.connectTimeoutMs ?? 10_000);
      const fail = (error: Error): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timeout);
        this.closedByUser = true;
        this.mqtt.end(true);
        reject(error);
      };
      const ready = (): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timeout);
        resolve();
      };

      this.mqtt = mqtt.connect(this.options.url, {
        ...this.options.mqtt,
        clientId: this.clientId,
        username: this.options.username ?? "token",
        password,
        protocolVersion: 5,
        clean: true,
        properties: { sessionExpiryInterval: 0 },
        reconnectPeriod: 0, // reconnecting is done here, so the token can be refreshed and the delay can back off
        resubscribe: false,
        queueQoSZero: false,
        connectTimeout: this.options.connectTimeoutMs ?? 10_000,
      });

      this.mqtt.on("connect", (connack) => {
        const player = connack.properties?.userProperties?.["gw-player"];
        this._playerId = Array.isArray(player) ? player[0] : player;
        this.mqtt.subscribe(inboxTopic(this.clientId), { qos: 0 }, (error, granted) => {
          if (error || !granted || granted.some((g) => g.qos > 2)) {
            fail(new ConnectError("The server refused the inbox subscription.", undefined));
            if (settled) this.mqtt.end(true);
            return;
          }

          this.reconnectDelay = this.settings.reconnectDelayMs;
          this.setState("connected");
          ready();
          void this.resume();
        });
      });

      this.mqtt.on("message", (_topic, payload) => this.receive(payload));

      this.mqtt.on("error", (error) => {
        // Before the first successful connect, an error (a refused CONNACK, usually) is final.
        const code = (error as { code?: unknown }).code;
        fail(new ConnectError(error.message, typeof code === "number" ? code : undefined));
      });

      this.mqtt.on("close", () => {
        if (this.closedByUser) return;
        if (!settled) return; // still in the first attempt; the timeout or the error handler decides
        this.setState("reconnecting");
        this.scheduleReconnect();
      });
    });

    this.ticker = setInterval(() => this.tick(Date.now()), 100);
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer || this.closedByUser) return;
    const delay = this.reconnectDelay * (0.75 + Math.random() * 0.5);
    this.reconnectDelay = Math.min(this.reconnectDelay * 2, this.settings.maxReconnectDelayMs);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = undefined;
      void this.reconnect();
    }, delay);
  }

  private async reconnect(): Promise<void> {
    if (this.closedByUser) return;
    try {
      this.mqtt.options.password = await this.resolveToken();
      this.mqtt.reconnect();
    } catch {
      this.scheduleReconnect();
    }
  }

  private async resolveToken(): Promise<string | undefined> {
    const token = this.options.token;
    return typeof token === "function" ? await token() : token;
  }

  /**
   * After a reconnect: join every room again, then re-send what has no reply yet, with the original command ids. A
   * room that could not be joined yet keeps its commands until it is back.
   */
  private async resume(): Promise<void> {
    const rooms = [...this.rooms.values()].filter((room) => room._needsRejoin);
    await Promise.allSettled(rooms.map((room) => room._rejoin()));
    this._resendCommands();
  }

  private setState(state: ConnectionState): void {
    if (this._state === state) return;
    this._state = state;
    if (state !== "connected") {
      for (const room of this.rooms.values()) room._disconnected();
    }

    for (const listener of [...this.listeners]) listener(state);
  }

  // ---------------------------------------------------------------- messages

  private publish(pending: Pending): void {
    pending.lastSentAt = Date.now();
    if (this._state !== "connected") return; // it stays pending and is sent after the reconnect
    this.mqtt.publish(pending.topic, pending.payload, { qos: 1 });
  }

  private receive(payload: Uint8Array): void {
    let envelope: { t?: unknown; room?: unknown };
    try {
      envelope = JSON.parse(new TextDecoder().decode(payload)) as { t?: unknown; room?: unknown };
    } catch {
      return;
    }

    if (typeof envelope.t !== "string" || typeof envelope.room !== "string") return;
    const message = envelope as { t: string; room: string };
    if (isReply(message)) {
      this.receiveReply(message);
      return;
    }

    this.rooms.get(message.room)?._receive(message as EventEnvelope | SnapshotEnvelope | ClosedEnvelope, Date.now());
  }

  private receiveReply(reply: ReplyEnvelope): void {
    const pending = reply.cmd === undefined ? undefined : this.pending.get(reply.cmd);
    if (!pending) return;

    if (reply.status === "error" && pending.kind === "command") {
      // The room could not be reached: the outcome is unknown, so keep the request and let the retry timer re-send it.
      if (reply.code === "unavailable") return;

      // This gateway does not know the connection is in the room (it reconnected elsewhere): join again, then re-send.
      if (reply.code === "not-joined") {
        const room = this.rooms.get(pending.roomKey);
        if (room) {
          // If the join fails for now, the room tries again later and re-sends its commands once it is back.
          void room._notJoined().then(() => {
            if (room._attached && this.pending.has(pending.cmd)) this.publish(pending);
          });
          return;
        }
      }
    }

    this.settle(pending, reply, undefined);
  }

  private settle(pending: Pending, reply: ReplyEnvelope | undefined, error: Error | undefined): void {
    if (!this.pending.delete(pending.cmd)) return;
    if (reply) pending.resolve(reply);
    else pending.reject(error ?? new OutcomeUnknownError(pending.cmd));
  }

  private tick(now: number): void {
    for (const pending of [...this.pending.values()]) {
      if (now >= pending.deadline) this.settle(pending, undefined, new OutcomeUnknownError(pending.cmd));
      else if (this._state === "connected" && now - pending.lastSentAt >= this.settings.retryIntervalMs && !this.waitsForRoom(pending)) this.publish(pending);
    }

    if (this._state !== "connected") return;
    for (const room of [...this.rooms.values()]) room._tick(now);
  }
}

/**
 * A room this client has joined. Events arrive in order, each exactly once; after a reconnect or a lost message the
 * room catches up through a fresh snapshot.
 */
export class Room<TView = unknown> {
  readonly key: string;

  private readonly engine: OrderingEngine;
  private readonly eventHandlers = new Map<string, Set<(data: unknown, meta: EventMeta) => void>>();
  private readonly anyHandlers = new Set<(name: string, data: unknown, meta: EventMeta) => void>();
  private readonly snapshotHandlers = new Set<(view: TView, info: SnapshotInfo) => void>();
  private readonly presenceHandlers = new Set<(presence: PresenceData) => void>();
  private readonly closedHandlers = new Set<(info: ClosedInfo) => void>();
  private _view: TView | undefined;
  private _members: Member[] = [];
  private _you: Member | undefined;
  private _closedReason: ClosedReason | "left" | undefined;
  private attached = false;
  private joinedOnce = false;
  private syncing = false;
  private rejoining: Promise<void> | undefined;
  private rejoinDelay: number;
  private nextRejoinAt: number | undefined;
  private readonly readyWaiters = new Set<{ resolve: () => void; reject: (error: Error) => void }>();

  /** @internal */
  constructor(
    private readonly client: GrainWireClient,
    readonly type: string,
    readonly id: string,
    private readonly joinData: unknown,
    private readonly settings: { gapWaitMs: number; syncIntervalMs: number; reconnectDelayMs: number; maxReconnectDelayMs: number },
  ) {
    this.key = `${type}/${id}`;
    this.rejoinDelay = settings.reconnectDelayMs;
    this.engine = new OrderingEngine({ ...defaultOrderingOptions, gapWaitMs: settings.gapWaitMs, syncIntervalMs: settings.syncIntervalMs }, Date.now());
  }

  /** The latest snapshot of the room as this player may see it. Events are not applied to it: that is the app's job. */
  get view(): TView | undefined {
    return this._view;
  }

  /** The members of the room, kept up to date from presence events. */
  get members(): readonly Member[] {
    return this._members;
  }

  /** This player's own membership (role, status). */
  get you(): Member | undefined {
    return this._you;
  }

  get closed(): boolean {
    return this._closedReason !== undefined;
  }

  get closedReason(): ClosedReason | "left" | undefined {
    return this._closedReason;
  }

  /** Called with every snapshot: after joining, after a reconnect, and whenever the room had to catch up. */
  onSnapshot(handler: (view: TView, info: SnapshotInfo) => void): Unsubscribe {
    this.snapshotHandlers.add(handler);
    if (this._view !== undefined && this._you) handler(this._view, { members: this._members, you: this._you });
    return () => this.snapshotHandlers.delete(handler);
  }

  /** Called for every app event with this name, in order. */
  on<TData = unknown>(name: string, handler: (data: TData, meta: EventMeta) => void): Unsubscribe {
    let handlers = this.eventHandlers.get(name);
    if (!handlers) this.eventHandlers.set(name, (handlers = new Set()));
    const wrapped = handler as (data: unknown, meta: EventMeta) => void;
    handlers.add(wrapped);
    return () => handlers.delete(wrapped);
  }

  /** Called for every app event, in order. */
  onAny(handler: (name: string, data: unknown, meta: EventMeta) => void): Unsubscribe {
    this.anyHandlers.add(handler);
    return () => this.anyHandlers.delete(handler);
  }

  /** Called when a member joins, drops ("reconnecting"), comes back ("online"), leaves, or changes role. */
  onPresence(handler: (presence: PresenceData) => void): Unsubscribe {
    this.presenceHandlers.add(handler);
    return () => this.presenceHandlers.delete(handler);
  }

  /** Called once when the room ends for this client: closed, kicked, removed, or lost on the server. */
  onClosed(handler: (info: ClosedInfo) => void): Unsubscribe {
    this.closedHandlers.add(handler);
    return () => this.closedHandlers.delete(handler);
  }

  /**
   * Sends a command and waits for the outcome. Resolves with the handler's return value once the command has
   * committed. Rejects with RejectedError when the room refused it, RoomError when it could not be processed, and
   * OutcomeUnknownError when no reply arrived in time (it is re-sent with the same command id until then, so it
   * cannot run twice).
   */
  async request<TResult = unknown>(name: string, data?: unknown, options: RequestOptions = {}): Promise<TResult> {
    this.ensureOpen();
    const reply = await this.client.request("command", this.key, `cmd/${name}`, { ri: this.engine.current.ri, d: data }, options.timeoutMs);
    return this.interpret(reply) as TResult;
  }

  /** Sends a command without waiting for a reply. */
  send(name: string, data?: unknown): void {
    this.ensureOpen();
    this.client.send(this.key, `cmd/${name}`, { ri: this.engine.current.ri, d: data });
  }

  /** Checks this room against the server now, instead of waiting for the next automatic check. */
  async sync(): Promise<void> {
    this.ensureOpen();
    await this.runSync();
  }

  /** Stops following the room on this connection. The player stays a member until the server's grace period ends. */
  async leave(): Promise<void> {
    if (this.closed) return;
    this._close("left", undefined, false);
    if (this.client._connected) await this.client.request("leave", this.key, "sys/leave", {}, 5_000).catch(() => undefined);
  }

  // ---------------------------------------------------------------- driven by the client

  /** @internal True once the server has accepted a join, also while the first snapshot is still on its way. */
  get _needsRejoin(): boolean {
    return !this.closed && !this.attached && this.joinedOnce;
  }

  /** @internal True while this connection is joined to the room on the server, as far as the client knows. */
  get _attached(): boolean {
    return this.attached;
  }

  /** @internal */
  _joined(now: number): void {
    this.attached = true;
    this.joinedOnce = true;
    this.rejoinDelay = this.settings.reconnectDelayMs;
    this.nextRejoinAt = undefined;
    this.engine.expectBaseline(now);
  }

  /**
   * @internal Resolves once the first snapshot has been processed. Rejects with RoomClosedError when the room closes
   * first, and with OutcomeUnknownError when no snapshot arrives within `timeoutMs`.
   */
  _whenReady(timeoutMs: number, commandId: string): Promise<void> {
    if (this.engine.ready) return Promise.resolve();
    if (this._closedReason !== undefined) return Promise.reject(new RoomClosedError(this._closedReason));
    return new Promise<void>((resolve, reject) => {
      const waiter = {
        resolve: (): void => {
          clearTimeout(timer);
          resolve();
        },
        reject: (error: Error): void => {
          clearTimeout(timer);
          reject(error);
        },
      };
      const timer = setTimeout(() => {
        this.readyWaiters.delete(waiter);
        reject(new OutcomeUnknownError(commandId));
      }, timeoutMs);
      this.readyWaiters.add(waiter);
    });
  }

  /** @internal */
  _disconnected(): void {
    this.attached = false;
    this.syncing = false;
    this.nextRejoinAt = undefined; // the reconnect joins again
  }

  /** @internal Joins again after a reconnect. The server keeps the seat and sends a fresh snapshot. */
  _rejoin(): Promise<void> {
    if (this.closed) return Promise.resolve();
    if (this.rejoining) return this.rejoining;

    // Cleared in a continuation, which always runs after the assignment below, even when the attempt fails at once.
    const attempt: Promise<void> = this.rejoinOnce().finally(() => {
      if (this.rejoining === attempt) this.rejoining = undefined;
    });
    this.rejoining = attempt;
    return attempt;
  }

  /**
   * @internal The gateway answered a command with "not-joined": it does not know this connection is in the room. Joins
   * again and resolves when that attempt is over. When a join is already waiting for its next try, nothing is started:
   * the room's commands are sent again once it is back.
   */
  _notJoined(): Promise<void> {
    if (!this.closed) {
      // Detached, so that a join which fails for a passing reason is tried again (see retryRejoinLater).
      this.attached = false;
      if (this.nextRejoinAt !== undefined) return Promise.resolve();
    }

    return this._rejoin();
  }

  private async rejoinOnce(): Promise<void> {
    try {
      const reply = await this.client.request("join", this.key, "sys/join", { create: false, data: this.joinData });
      if (reply.status === "ok") {
        this._joined(Date.now());
      } else if (reply.code === "room-not-found") {
        this._close("lost", undefined, true);
      } else if (reply.status === "rejected" || reply.code === "not-member") {
        this._close("removed", reply.reason, true);
      } else {
        // "unavailable" and the like: the room could not be reached just now (a silo is recovering, say).
        this.retryRejoinLater();
      }
    } catch {
      // No reply in time. If the connection is gone, the reconnect joins again; if it is up, the next tick does.
      this.retryRejoinLater();
    }
  }

  /** @internal */
  _receive(envelope: EventEnvelope | SnapshotEnvelope | ClosedEnvelope, now: number): void {
    if (this.closed) return;
    if (isClosed(envelope)) {
      this._close(envelope.d.reason, envelope.d.message, true);
      return;
    }

    const actions = isSnapshot(envelope) ? this.engine.receiveSnapshot(envelope, now) : this.engine.receiveEvent(envelope, now);
    this.apply(actions);
  }

  /** @internal */
  _tick(now: number): void {
    if (this.closed) return;
    if (!this.attached) {
      // A join that failed for a passing reason is tried again while the connection stays up. Without this the room
      // would stay detached, and silently stale, until the connection happened to drop.
      if (this.nextRejoinAt !== undefined && now >= this.nextRejoinAt) {
        this.nextRejoinAt = undefined;
        void this._rejoin().then(() => {
          // Commands that were waiting for the room go out again now, under their original ids.
          if (this.attached) this.client._resendCommands(this.key);
        });
      }

      return;
    }

    if (this.engine.tick(now).length > 0) void this.runSync();
  }

  /** @internal */
  _close(reason: ClosedReason | "left", message: string | undefined, notify: boolean): void {
    if (this.closed) return;
    this._closedReason = reason;
    this.attached = false;
    this.client._forget(this as Room<unknown>);
    for (const waiter of [...this.readyWaiters]) waiter.reject(new RoomClosedError(reason));
    this.readyWaiters.clear();
    if (notify && reason !== "left") {
      for (const handler of [...this.closedHandlers]) handler({ reason, message });
    }
  }

  /** Schedules another join, a little later each time (with jitter, so many clients do not all return at once). */
  private retryRejoinLater(): void {
    if (this.closed || this.attached) return;
    this.nextRejoinAt = Date.now() + this.rejoinDelay * (0.75 + Math.random() * 0.5);
    this.rejoinDelay = Math.min(this.rejoinDelay * 2, this.settings.maxReconnectDelayMs);
  }

  private async runSync(): Promise<void> {
    if (this.syncing || this.closed || !this.attached) return;
    this.syncing = true;
    try {
      const snapshotsBefore = this.engine.snapshotsReceived;
      const position = this.engine.current;
      const reply = await this.client.request("sync", this.key, "sys/sync", { ri: position.ri, se: position.se, seq: this.engine.ready ? position.seq : -1 }, 5_000);
      if (reply.status === "ok") {
        if (reply.code === "in-sync") this.engine.confirmInSync(Date.now());
        else if (reply.code === "resynced") this.apply(this.engine.confirmResynced(snapshotsBefore, Date.now()));
      } else if (reply.code === "room-not-found") {
        this._close("lost", undefined, true);
      } else if (reply.code === "not-member") {
        this._close("removed", undefined, true);
      } else if (reply.code === "not-joined") {
        this.attached = false;
        void this._rejoin();
      }
    } catch {
      // No reply: the engine asks again.
    } finally {
      this.syncing = false;
    }
  }

  private apply(actions: OrderingAction[]): void {
    for (const action of actions) {
      if (action.kind === "snapshot") {
        const snapshot = action.snapshot;
        this._view = snapshot.d as TView;
        this._members = [...snapshot.members];
        this._you = snapshot.you;
        for (const waiter of [...this.readyWaiters]) waiter.resolve();
        this.readyWaiters.clear();
        for (const handler of [...this.snapshotHandlers]) handler(this._view, { members: this._members, you: this._you });
      } else if (action.kind === "event") {
        this.deliver(action.event);
      }
    }
  }

  private deliver(event: EventEnvelope): void {
    const meta: EventMeta = { seq: event.seq };
    if (event.t === Types.presence) {
      const presence = event.d as PresenceData;
      const others = this._members.filter((m) => m.player !== presence.player);
      this._members = presence.status === "left" ? others : [...others, { player: presence.player, role: presence.role, status: presence.status }];
      if (this._you && presence.player === this._you.player) this._you = { ...this._you, role: presence.role, status: presence.status };
      for (const handler of [...this.presenceHandlers]) handler(presence);
      return;
    }

    if (event.t.startsWith("$")) return;
    for (const handler of [...(this.eventHandlers.get(event.t) ?? [])]) handler(event.d, meta);
    for (const handler of [...this.anyHandlers]) handler(event.t, event.d, meta);
  }

  private interpret(reply: ReplyEnvelope): unknown {
    if (reply.status === "error") {
      if (reply.code === "room-not-found" || reply.code === "stale-room") this._close("lost", undefined, true);
      else if (reply.code === "not-member") this._close("removed", undefined, true);
    }

    return interpret(reply);
  }

  private ensureOpen(): void {
    if (this._closedReason !== undefined) throw new RoomClosedError(this._closedReason);
    if (!this.client._connected) throw new NotConnectedError();
  }
}

function interpret(reply: ReplyEnvelope): unknown {
  if (reply.status === "ok") return reply.d;
  if (reply.status === "rejected") throw new RejectedError(reply.code ?? "rejected", reply.reason);
  throw new RoomError(reply.code ?? "error", reply.reason);
}

function newId(): string {
  const crypto = globalThis.crypto;
  if (typeof crypto?.randomUUID === "function") return crypto.randomUUID().replaceAll("-", "");
  return randomHex(16);
}

function randomHex(bytes: number): string {
  const buffer = new Uint8Array(bytes);
  globalThis.crypto.getRandomValues(buffer);
  return Array.from(buffer, (b) => b.toString(16).padStart(2, "0")).join("");
}

/** One id per browser tab, so a reconnect is recognised as the same tab. Outside a browser: a new random id. */
function defaultClientId(): string {
  const key = "grainwire.clientId";
  try {
    const storage = typeof window === "undefined" ? undefined : window.sessionStorage;
    if (storage) {
      let id = storage.getItem(key);
      if (!id) {
        id = randomHex(16);
        storage.setItem(key, id);
      }

      return id;
    }
  } catch {
    // Storage is blocked: fall through.
  }

  return randomHex(16);
}
