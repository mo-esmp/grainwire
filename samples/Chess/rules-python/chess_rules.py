"""The rules of the chess room, as one function: decide(request) -> decision.

The function keeps nothing between calls. The GrainWire room grain stores the state, sends it back with every
request, and applies the decision only after the room has committed it. So this module never reads the clock
(it uses request["now"]), never sends anything itself (it returns effects), and can be called twice safely.

The messages match samples/Chess/Chess.Server, so both chess clients work against either server.
The rules of chess come from python-chess (https://python-chess.readthedocs.io).
"""

from __future__ import annotations

import re
from datetime import datetime, timedelta, timezone
from typing import Any

import chess

WHITE = "white"
BLACK = "black"
SPECTATOR = "spectator"
WAITING = "waiting"
PLAYING = "playing"
FINISHED = "finished"
FLAG = "flag"

PROMOTIONS = {"q": chess.QUEEN, "r": chess.ROOK, "b": chess.BISHOP, "n": chess.KNIGHT}


class Rejected(Exception):
    """Refuses the input. The grain sends the reason (and code) to the player and changes nothing."""

    def __init__(self, reason: str, code: str | None = None):
        super().__init__(reason)
        self.reason = reason
        self.code = code


class Turn:
    """Collects what one call decides: the changed state, the joining player's role, and the effects."""

    def __init__(self, state: dict[str, Any], now: datetime):
        self.state = state
        self.now = now
        self.role: str | None = None
        self.effects: list[dict[str, Any]] = []

    def broadcast(self, name: str, data: dict[str, Any]) -> None:
        self.effects.append({"kind": "broadcast", "name": name, "data": data})

    def schedule(self, name: str, at: datetime) -> None:
        self.effects.append({"kind": "schedule", "name": name, "at": iso(at)})

    def cancel_schedule(self, name: str) -> None:
        self.effects.append({"kind": "cancelSchedule", "name": name})

    def decision(self) -> dict[str, Any]:
        # Chess has no hidden information: everyone gets the same view ("*").
        return {
            "state": self.state,
            "role": self.role,
            "views": {"*": view(self.state, self.now)},
            "effects": self.effects,
        }


def decide(request: dict[str, Any]) -> dict[str, Any]:
    """Handles one room operation. See the README for the request and decision formats."""
    now = parse_time(request["now"])
    state = request.get("state") or new_game()
    turn = Turn(state, now)
    inp = request["input"]
    kind = inp["kind"]
    try:
        if kind == "create":
            on_create(turn, inp.get("data") or {})
        elif kind == "join":
            on_join(turn, inp["player"])
        elif kind == "command" and inp["name"] == "move":
            on_move(turn, inp["player"], inp.get("data") or {})
        elif kind == "command" and inp["name"] == "resign":
            on_resign(turn, inp["player"])
        elif kind == "command":
            raise Rejected(f"Unknown command '{inp['name']}'", "unknown-command")
        elif kind == "deadline":
            on_deadline(turn, inp["name"])
        elif kind == "left":
            on_left(turn, inp["player"])
        # "rejoin" and "presence": nothing to decide. A returning player keeps their seat.
    except Rejected as rejected:
        return {"reject": {"reason": rejected.reason, "code": rejected.code}}
    return turn.decision()


def new_game() -> dict[str, Any]:
    return {
        "fen": chess.STARTING_FEN,
        "turn": WHITE,
        "check": False,
        "status": WAITING,
        "white": None,
        "black": None,
        "moves": [],
        "lastFrom": None,
        "lastTo": None,
        "whiteMs": 0,
        "blackMs": 0,
        "initialMs": 0,
        "turnStartedAt": None,
        "result": None,
        "reason": None,
    }


def on_create(turn: Turn, options: dict[str, Any]) -> None:
    minutes = min(max(int(options.get("minutes") or 10), 1), 180)
    s = turn.state
    s["initialMs"] = s["whiteMs"] = s["blackMs"] = minutes * 60_000


def on_join(turn: Turn, player: str) -> None:
    s = turn.state
    if s["white"] is None:
        s["white"] = player
        turn.role = WHITE
    elif s["black"] is None:
        s["black"] = player
        s["status"] = PLAYING
        s["turnStartedAt"] = iso(turn.now)
        turn.schedule(FLAG, turn.now + timedelta(milliseconds=s["whiteMs"]))
        turn.broadcast("started", {"white": s["white"], "black": s["black"], "at": iso(turn.now)})
        turn.role = BLACK
    else:
        turn.role = SPECTATOR


def on_move(turn: Turn, player: str, request: dict[str, Any]) -> None:
    s = turn.state
    if s["status"] != PLAYING:
        raise Rejected("The game is not in progress", "not-playing")

    mover = seat_of(s, player)
    if s["turn"] != mover:
        raise Rejected("It's not your turn", "not-your-turn")

    board = chess.Board(s["fen"])
    move = find_move(board, request.get("from"), request.get("to"), request.get("promotion"))
    if move is None:
        raise Rejected("That move is not legal", "illegal-move")

    # The mover's clock stops, the opponent's starts.
    spent = elapsed_ms(parse_time(s["turnStartedAt"]), turn.now)
    clock = "whiteMs" if mover == WHITE else "blackMs"
    s[clock] = max(0, s[clock] - spent)

    san = board.san(move)
    board.push(move)
    s["fen"] = board.fen()
    s["turn"] = WHITE if board.turn == chess.WHITE else BLACK
    s["check"] = board.is_check()
    s["moves"].append(san)
    s["lastFrom"] = chess.square_name(move.from_square)
    s["lastTo"] = chess.square_name(move.to_square)
    s["turnStartedAt"] = iso(turn.now)

    turn.broadcast("moved", {
        "from": s["lastFrom"],
        "to": s["lastTo"],
        "san": san,
        "fen": s["fen"],
        "turn": s["turn"],
        "check": s["check"],
        "whiteMs": s["whiteMs"],
        "blackMs": s["blackMs"],
        "at": iso(turn.now),
    })

    ending = game_over(board, mover)
    if ending is not None:
        finish(turn, *ending)
    else:
        turn.schedule(FLAG, turn.now + timedelta(milliseconds=s["blackMs" if mover == WHITE else "whiteMs"]))


def on_resign(turn: Turn, player: str) -> None:
    if turn.state["status"] != PLAYING:
        raise Rejected("The game is not in progress", "not-playing")
    finish(turn, opponent(seat_of(turn.state, player)), "resignation")


def on_deadline(turn: Turn, name: str) -> None:
    s = turn.state
    if name != FLAG or s["status"] != PLAYING:
        return
    s["whiteMs" if s["turn"] == WHITE else "blackMs"] = 0
    finish(turn, opponent(s["turn"]), "timeout")


def on_left(turn: Turn, player: str) -> None:
    s = turn.state
    if s["status"] == PLAYING and player in (s["white"], s["black"]):
        finish(turn, opponent(seat_of(s, player)), "abandoned")


def finish(turn: Turn, result: str, reason: str) -> None:
    s = turn.state
    s["status"] = FINISHED
    s["result"] = result
    s["reason"] = reason
    s["turnStartedAt"] = None
    turn.cancel_schedule(FLAG)
    turn.broadcast("finished", {"result": result, "reason": reason, "whiteMs": s["whiteMs"], "blackMs": s["blackMs"]})


def find_move(board: chess.Board, origin: Any, target: Any, promotion: Any) -> chess.Move | None:
    """The legal move from origin to target, or None. A promotion with no piece given promotes to a queen."""
    try:
        from_square = chess.parse_square(origin)
        to_square = chess.parse_square(target)
    except (TypeError, ValueError):
        return None
    promote_to = PROMOTIONS.get(str(promotion or "q").lower(), chess.QUEEN)
    for move in board.legal_moves:
        if move.from_square == from_square and move.to_square == to_square and move.promotion in (None, promote_to):
            return move
    return None


def game_over(board: chess.Board, mover: str) -> tuple[str, str] | None:
    """How the game ended after the mover's move, as (result, reason), or None if it goes on."""
    if board.is_checkmate():
        return mover, "checkmate"
    if board.is_stalemate():
        return "draw", "stalemate"
    if board.is_insufficient_material():
        return "draw", "insufficient material"
    if board.is_fifty_moves():
        return "draw", "fifty-move rule"
    return None


def view(s: dict[str, Any], now: datetime) -> dict[str, Any]:
    """What players see in a snapshot. The grain refreshes serverNow each time it sends one."""
    return {**s, "serverNow": iso(now)}


def seat_of(s: dict[str, Any], player: str) -> str:
    return WHITE if player == s["white"] else BLACK


def opponent(seat: str) -> str:
    return BLACK if seat == WHITE else WHITE


def elapsed_ms(start: datetime, end: datetime) -> int:
    return int((end - start) / timedelta(milliseconds=1))


_FRACTION = re.compile(r"(\.\d{6})\d+")


def parse_time(text: str) -> datetime:
    """Reads an ISO 8601 time. .NET writes seven fraction digits; older Pythons accept at most six."""
    return datetime.fromisoformat(_FRACTION.sub(r"\1", text).replace("Z", "+00:00"))


def iso(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
