import type { ClosedReason } from "./protocol.js";

/** The room refused the request. Nothing changed. */
export class RejectedError extends Error {
  constructor(
    readonly code: string,
    readonly reason: string | undefined,
  ) {
    super(reason ?? code);
    this.name = "RejectedError";
  }
}

/** The request could not be processed. Nothing changed. */
export class RoomError extends Error {
  constructor(
    readonly code: string,
    readonly reason: string | undefined,
  ) {
    super(reason ?? code);
    this.name = "RoomError";
  }
}

/**
 * No reply arrived in time. The command may or may not have been applied: this is not a rejection.
 * The room's next snapshot or events will show what happened.
 */
export class OutcomeUnknownError extends Error {
  constructor(readonly commandId: string) {
    super("No reply arrived in time; the outcome of the request is unknown.");
    this.name = "OutcomeUnknownError";
  }
}

/** The request was not sent because there is no connection right now. */
export class NotConnectedError extends Error {
  constructor() {
    super("Not connected.");
    this.name = "NotConnectedError";
  }
}

/** The room is closed for this client. */
export class RoomClosedError extends Error {
  constructor(readonly reason: ClosedReason | "left") {
    super(`The room is closed (${reason}).`);
    this.name = "RoomClosedError";
  }
}

/** The server refused the connection. */
export class ConnectError extends Error {
  constructor(
    message: string,
    readonly reasonCode: number | undefined,
  ) {
    super(message);
    this.name = "ConnectError";
  }
}
