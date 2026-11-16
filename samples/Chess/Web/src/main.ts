import { connect, NotConnectedError, OutcomeUnknownError, RejectedError } from "@grainwire/client";
import type { ConnectionState, GrainWireClient, Room } from "@grainwire/client";
import { parseFen, renderBoard } from "./board";
import type { Color } from "./board";

// The whole browser side of the chess sample. It keeps a copy of the game it gets from the server's snapshot,
// applies the events that follow, and draws it. Everything about connections, ordering and catching up is
// done by @grainwire/client.

interface GameView {
  fen: string;
  turn: Color;
  status: "waiting" | "playing" | "finished";
  white: string | null;
  black: string | null;
  moves: string[];
  lastFrom: string | null;
  lastTo: string | null;
  whiteMs: number;
  blackMs: number;
  initialMs: number;
  turnStartedAt: string | null;
  result: Color | "draw" | null;
  reason: string | null;
  serverNow: string;
}

interface Moved {
  from: string;
  to: string;
  san: string;
  fen: string;
  turn: Color;
  check: boolean;
  whiteMs: number;
  blackMs: number;
  at: string;
}

const app = document.querySelector<HTMLDivElement>("#app")!;
let client: GrainWireClient | undefined;
let room: Room<GameView> | undefined;
let game: GameView | undefined;
let clockOffset = 0; // server time minus this computer's time
let selected: string | undefined;
let notice = "";
let closedMessage = "";
let check = false;
let clockTimer: ReturnType<typeof setInterval> | undefined;

// ---------------------------------------------------------------- who you are

function playerName(): string {
  let name = localStorage.getItem("chess.name");
  if (!name) {
    name = `guest-${Math.random().toString(36).slice(2, 6)}`;
    localStorage.setItem("chess.name", name);
  }

  return name;
}

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
}

// ---------------------------------------------------------------- pages

function route(): void {
  const match = /^#\/g\/([A-Za-z0-9_-]+)(?:\/new\/(\d+))?$/.exec(location.hash);
  if (match) void openGame(match[1]!, match[2] ? Number(match[2]) : undefined);
  else showHome();
}

function showHome(): void {
  void leaveGame();
  app.innerHTML = `
    <main class="home">
      <h1>GrainWire Chess</h1>
      <p class="lead">A sample for GrainWire. Each game lives in one Orleans grain on the server; moves travel over MQTT.</p>
      <label>Your name <input id="name" maxlength="24" autocomplete="nickname" value="${escapeHtml(playerName())}" /></label>
      <label>Minutes per player
        <select id="minutes"><option>1</option><option>3</option><option>5</option><option selected>10</option><option>30</option></select>
      </label>
      <button id="create" class="primary">Create a game</button>
      <p class="hint">You get a link to send to your opponent. Anyone else who opens it watches the game.</p>
    </main>`;
  const name = app.querySelector<HTMLInputElement>("#name")!;
  name.addEventListener("change", () => {
    const value = name.value.trim();
    if (value) localStorage.setItem("chess.name", value);
  });
  app.querySelector("#create")!.addEventListener("click", () => {
    const value = name.value.trim();
    if (value) localStorage.setItem("chess.name", value);
    const id = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, "0")).join("");
    location.hash = `#/g/${id}/new/${app.querySelector<HTMLSelectElement>("#minutes")!.value}`;
  });
}

async function openGame(id: string, createWithMinutes: number | undefined): Promise<void> {
  await leaveGame();
  closedMessage = "";
  notice = "";
  app.innerHTML = `<main class="game"><p class="status">Connecting…</p></main>`;
  try {
    if (!client) {
      client = await connect({ url: `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/mqtt`, username: playerName(), token: "dev" });
      client.onStateChange(() => render());
    }

    room = await client.join<GameView>("chess", id, { create: createWithMinutes !== undefined, options: { minutes: createWithMinutes } });
  } catch (error) {
    app.innerHTML = `<main class="game"><p class="status">${escapeHtml(describe(error, "This game does not exist."))}</p><p><a href="#/">Create a new game</a></p></main>`;
    return;
  }

  // The link people share never carries the "create" part.
  if (createWithMinutes !== undefined) history.replaceState(null, "", `#/g/${id}`);

  room.onSnapshot((view) => {
    game = structuredClone(view);
    clockOffset = Date.parse(view.serverNow) - Date.now();
    check = false;
    selected = undefined;
    render();
  });
  room.on<{ white: string; black: string; at: string }>("started", (started) => {
    if (!game) return;
    Object.assign(game, { status: "playing", white: started.white, black: started.black, turnStartedAt: started.at });
    render();
  });
  room.on<Moved>("moved", (moved) => {
    if (!game) return;
    game.moves.push(moved.san);
    Object.assign(game, { fen: moved.fen, turn: moved.turn, lastFrom: moved.from, lastTo: moved.to, whiteMs: moved.whiteMs, blackMs: moved.blackMs, turnStartedAt: moved.at });
    check = moved.check;
    selected = undefined;
    render();
  });
  room.on<{ result: GameView["result"]; reason: string; whiteMs: number; blackMs: number }>("finished", (finished) => {
    if (!game) return;
    Object.assign(game, { status: "finished", result: finished.result, reason: finished.reason, whiteMs: finished.whiteMs, blackMs: finished.blackMs, turnStartedAt: null });
    render();
  });
  room.onPresence(() => render());
  room.onClosed((closed) => {
    closedMessage = closed.reason === "lost" ? "This game was lost on the server." : "This game was closed.";
    render();
  });

  clockTimer = setInterval(updateClocks, 250);
  render();
}

async function leaveGame(): Promise<void> {
  if (clockTimer) clearInterval(clockTimer);
  clockTimer = undefined;
  const leaving = room;
  room = undefined;
  game = undefined;
  selected = undefined;
  await leaving?.leave().catch(() => undefined);
}

// ---------------------------------------------------------------- drawing

function myRole(): string {
  return room?.you?.role ?? "spectator";
}

function mySeat(): Color | undefined {
  const role = myRole();
  return role === "white" || role === "black" ? role : undefined;
}

function canMove(): boolean {
  return !!game && !!room && !room.closed && game.status === "playing" && game.turn === mySeat() && client?.state === "connected";
}

function remaining(seat: Color): number {
  if (!game) return 0;
  const base = seat === "white" ? game.whiteMs : game.blackMs;
  if (game.status !== "playing" || game.turn !== seat || !game.turnStartedAt) return base;
  return Math.max(0, base - Math.max(0, Date.now() + clockOffset - Date.parse(game.turnStartedAt)));
}

function clockText(ms: number): string {
  const total = Math.ceil(ms / 1000);
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, "0")}`;
}

function updateClocks(): void {
  for (const seat of ["white", "black"] as const) {
    const element = app.querySelector<HTMLElement>(`[data-clock="${seat}"]`);
    if (element) element.textContent = clockText(remaining(seat));
  }
}

function seatCard(seat: Color): string {
  const player = seat === "white" ? game!.white : game!.black;
  const presence = player ? (room?.members.find((m) => m.player === player)?.status ?? "left") : undefined;
  const you = player !== null && player === room?.you?.player ? " (you)" : "";
  const badge =
    presence === "online" ? `<span class="presence online">online</span>`
    : presence === "reconnecting" ? `<span class="presence reconnecting">reconnecting…</span>`
    : presence === "left" ? `<span class="presence left">left</span>`
    : "";
  const active = game!.status === "playing" && game!.turn === seat ? " active" : "";
  return `
    <div class="seat ${seat}${active}" data-seat="${seat}">
      <span class="chip ${seat}" aria-hidden="true"></span>
      <span class="who">${player ? escapeHtml(player) + you : "Waiting for an opponent…"}</span>
      ${badge}
      <span class="clock" data-clock="${seat}">${clockText(remaining(seat))}</span>
    </div>`;
}

function statusText(): string {
  if (!game) return "";
  if (closedMessage) return closedMessage;
  const seat = mySeat();
  if (game.status === "waiting") return "Waiting for an opponent. Send them the invite link.";
  if (game.status === "finished") {
    const outcome = game.result === "draw" ? "Draw" : `${game.result === "white" ? "White" : "Black"} wins`;
    return `${outcome} (${game.reason}).`;
  }

  const mover = game.turn === "white" ? game.white : game.black;
  const turn = seat === game.turn ? "Your move" : `${mover ?? game.turn} to move`;
  return check ? `${turn}. Check!` : `${turn}.`;
}

function connectionText(state: ConnectionState | undefined): string {
  return state === "connected" ? "" : state === "reconnecting" ? "Connection lost. Reconnecting…" : "Offline";
}

function render(): void {
  if (!game || !room) return;
  const orientation: Color = mySeat() ?? "white";
  const top: Color = orientation === "white" ? "black" : "white";
  const moves = game.moves
    .reduce<string[]>((rows, san, index) => {
      if (index % 2 === 0) rows.push(`<li><span class="num">${index / 2 + 1}.</span> <span>${escapeHtml(san)}</span>`);
      else rows[rows.length - 1] += ` <span>${escapeHtml(san)}</span>`;
      return rows;
    }, [])
    .map((row) => `${row}</li>`)
    .join("");
  const offline = connectionText(client?.state);

  app.innerHTML = `
    <main class="game">
      <div class="top">
        <header>
          <a href="#/">← New game</a>
          <span class="role" id="role">${mySeat() ? `You play ${mySeat()}` : "You are watching"}</span>
          <button id="copy" type="button">Copy invite link</button>
        </header>
        ${offline ? `<p class="banner" id="banner" role="status">${offline}</p>` : ""}
      </div>
      <section class="table">
        ${seatCard(top)}
        <div id="board" class="board" role="group" aria-label="Chess board"></div>
        ${seatCard(orientation)}
      </section>
      <aside>
        <p class="status" id="status" role="status">${escapeHtml(statusText())}</p>
        ${notice ? `<p class="notice" id="notice" role="alert">${escapeHtml(notice)}</p>` : ""}
        <ol class="moves" id="moves">${moves}</ol>
        ${mySeat() && game.status === "playing" ? `<button id="resign" type="button">Resign</button>` : ""}
      </aside>
    </main>`;

  const board = app.querySelector<HTMLElement>("#board")!;
  renderBoard(board, parseFen(game.fen), {
    orientation,
    selected,
    lastMove: game.lastFrom && game.lastTo ? [game.lastFrom, game.lastTo] : undefined,
    interactive: canMove(),
  });
  board.addEventListener("click", (event) => {
    const square = (event.target as HTMLElement).closest<HTMLElement>("[data-square]")?.dataset.square;
    if (square) void onSquare(square);
  });
  app.querySelector("#copy")!.addEventListener("click", () => {
    void navigator.clipboard?.writeText(location.href).then(() => flash("Invite link copied."));
  });
  app.querySelector("#resign")?.addEventListener("click", () => void command("resign"));
}

function flash(message: string): void {
  notice = message;
  render();
  setTimeout(() => {
    if (notice === message) {
      notice = "";
      render();
    }
  }, 4000);
}

// ---------------------------------------------------------------- playing

async function onSquare(square: string): Promise<void> {
  if (!game || !canMove()) return;
  const position = parseFen(game.fen);
  const piece = position.get(square);
  const mine = piece?.color === mySeat();

  if (selected && selected !== square && !mine) {
    const from = selected;
    selected = undefined;
    const moving = position.get(from);
    const promotion = moving?.kind === "p" && (square.endsWith("8") || square.endsWith("1")) ? "q" : undefined;
    await command("move", { from, to: square, promotion });
    return;
  }

  selected = mine && selected !== square ? square : undefined;
  render();
}

async function command(name: string, data?: unknown): Promise<void> {
  if (!room) return;
  try {
    await room.request(name, data);
  } catch (error) {
    if (error instanceof OutcomeUnknownError) {
      // No answer in time: the move may or may not have been played. Ask the server for the current game.
      flash("No answer from the server. Checking the game…");
      await room.sync().catch(() => undefined);
    } else {
      flash(describe(error, "That did not work."));
    }
  }

  render();
}

function describe(error: unknown, fallback: string): string {
  if (error instanceof RejectedError) return error.reason ?? fallback;
  if (error instanceof NotConnectedError) return "You are offline. Reconnecting…";
  if (error instanceof Error && "code" in error && error.code === "room-not-found") return "This game does not exist.";
  return error instanceof Error ? error.message : fallback;
}

window.addEventListener("hashchange", route);
route();
