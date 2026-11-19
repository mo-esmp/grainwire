"""Tests for the chess rules. They play the part of the grain: keep the state and send it back each time.

    python -m unittest
"""

from __future__ import annotations

import unittest
from datetime import datetime, timedelta, timezone

from chess_rules import decide, iso, parse_time

START = datetime(2026, 11, 19, 10, 0, 0, tzinfo=timezone.utc)


class Room:
    """A stand-in for the bridge grain: it stores the state and applies a decision only when it is not a rejection."""

    def __init__(self) -> None:
        self.state = None
        self.now = START
        self.views = {}

    def send(self, kind: str, **fields) -> dict:
        decision = decide({
            "roomType": "chess",
            "roomId": "g1",
            "now": iso(self.now),
            "state": self.state,
            "members": [],
            "input": {"kind": kind, **fields},
        })
        if "reject" not in decision:
            self.state = decision["state"]
            self.views = decision["views"]
        return decision

    def move(self, player: str, origin, target, **extra) -> dict:
        return self.send("command", name="move", player=player, data={"from": origin, "to": target, **extra})

    def advance(self, seconds: float) -> None:
        self.now += timedelta(seconds=seconds)


def started_room(minutes: int = 5) -> Room:
    room = Room()
    room.send("create", player="alice", data={"minutes": minutes})
    room.send("join", player="alice", isCreator=True)
    room.send("join", player="bob", isCreator=False)
    return room


def effects(decision: dict, kind: str) -> list[dict]:
    return [e for e in decision["effects"] if e["kind"] == kind]


class SeatsTests(unittest.TestCase):
    def test_first_two_players_sit_and_the_rest_watch(self):
        room = Room()
        room.send("create", player="alice", data={"minutes": 5})
        self.assertEqual(room.send("join", player="alice", isCreator=True)["role"], "white")
        second = room.send("join", player="bob", isCreator=False)
        self.assertEqual(second["role"], "black")
        self.assertEqual(room.send("join", player="carol", isCreator=False)["role"], "spectator")

        self.assertEqual(room.state["status"], "playing")
        self.assertEqual(effects(second, "broadcast")[0]["name"], "started")
        flag = effects(second, "schedule")[0]
        self.assertEqual(flag["name"], "flag")
        self.assertEqual(parse_time(flag["at"]), START + timedelta(minutes=5))

    def test_clock_minutes_are_clamped(self):
        room = Room()
        room.send("create", player="alice", data={"minutes": 999})
        self.assertEqual(room.state["initialMs"], 180 * 60_000)

    def test_rejoin_changes_nothing(self):
        room = started_room()
        before = dict(room.state)
        decision = room.send("rejoin", player="alice")
        self.assertEqual(decision["effects"], [])
        self.assertEqual(room.state, before)


class MoveTests(unittest.TestCase):
    def test_a_legal_move_is_played_and_charged_to_the_movers_clock(self):
        room = started_room(minutes=5)
        room.advance(3)
        decision = room.move("alice", "e2", "e4")

        self.assertNotIn("reject", decision)
        moved = effects(decision, "broadcast")[0]
        self.assertEqual(moved["name"], "moved")
        self.assertEqual(moved["data"]["san"], "e4")
        self.assertEqual(moved["data"]["turn"], "black")
        self.assertEqual(room.state["whiteMs"], 5 * 60_000 - 3_000)
        self.assertEqual(room.state["blackMs"], 5 * 60_000)
        # Black's flag now falls when black's whole clock has run.
        flag = effects(decision, "schedule")[0]
        self.assertEqual(parse_time(flag["at"]), room.now + timedelta(minutes=5))

    def test_an_illegal_move_is_rejected(self):
        room = started_room()
        self.assertEqual(room.move("alice", "e2", "e5")["reject"]["code"], "illegal-move")

    def test_a_garbled_move_is_rejected(self):
        room = started_room()
        self.assertEqual(room.move("alice", "zz", None)["reject"]["code"], "illegal-move")

    def test_moving_out_of_turn_is_rejected(self):
        room = started_room()
        self.assertEqual(room.move("bob", "e7", "e5")["reject"]["code"], "not-your-turn")

    def test_moving_before_the_game_starts_is_rejected(self):
        room = Room()
        room.send("create", player="alice", data={})
        room.send("join", player="alice", isCreator=True)
        self.assertEqual(room.move("alice", "e2", "e4")["reject"]["code"], "not-playing")

    def test_checkmate_ends_the_game(self):
        room = started_room()
        for player, origin, target in [("alice", "f2", "f3"), ("bob", "e7", "e5"), ("alice", "g2", "g4")]:
            room.move(player, origin, target)
        decision = room.move("bob", "d8", "h4")

        self.assertEqual(room.state["status"], "finished")
        self.assertEqual((room.state["result"], room.state["reason"]), ("black", "checkmate"))
        self.assertTrue(room.state["check"])
        self.assertEqual([e["name"] for e in effects(decision, "broadcast")], ["moved", "finished"])
        self.assertEqual(effects(decision, "cancelSchedule")[0]["name"], "flag")
        self.assertEqual(effects(decision, "schedule"), [])

    def test_a_pawn_promotes_to_a_queen_unless_told_otherwise(self):
        promotion_fen = "8/P6k/8/8/8/8/8/K7 w - - 0 1"

        room = started_room()
        room.state = {**room.state, "fen": promotion_fen}
        self.assertEqual(effects(room.move("alice", "a7", "a8"), "broadcast")[0]["data"]["san"], "a8=Q")

        room = started_room()
        room.state = {**room.state, "fen": promotion_fen}
        self.assertEqual(effects(room.move("alice", "a7", "a8", promotion="n"), "broadcast")[0]["data"]["san"], "a8=N")


class EndingTests(unittest.TestCase):
    def test_resigning_gives_the_game_to_the_opponent(self):
        room = started_room()
        room.send("command", name="resign", player="bob")
        self.assertEqual((room.state["result"], room.state["reason"]), ("white", "resignation"))

    def test_the_flag_falls_on_the_player_to_move(self):
        room = started_room()
        room.advance(300)
        room.send("deadline", name="flag")
        self.assertEqual((room.state["result"], room.state["reason"]), ("black", "timeout"))
        self.assertEqual(room.state["whiteMs"], 0)

    def test_a_late_flag_after_the_game_ended_does_nothing(self):
        room = started_room()
        room.send("command", name="resign", player="alice")
        decision = room.send("deadline", name="flag")
        self.assertEqual(decision["effects"], [])
        self.assertEqual(room.state["reason"], "resignation")

    def test_a_player_who_leaves_forfeits(self):
        room = started_room()
        room.send("left", player="alice", reason="grace-expired")
        self.assertEqual((room.state["result"], room.state["reason"]), ("black", "abandoned"))

    def test_a_spectator_leaving_changes_nothing(self):
        room = started_room()
        room.send("join", player="carol", isCreator=False)
        room.send("left", player="carol", reason="grace-expired")
        self.assertEqual(room.state["status"], "playing")


class ContractTests(unittest.TestCase):
    def test_everyone_gets_the_same_view_with_server_time(self):
        room = started_room()
        view = room.views["*"]
        self.assertEqual(view["fen"], room.state["fen"])
        self.assertEqual(parse_time(view["serverNow"]), room.now)

    def test_an_unknown_command_is_rejected(self):
        room = started_room()
        decision = room.send("command", name="castle-everything", player="alice")
        self.assertEqual(decision["reject"]["code"], "unknown-command")

    def test_reads_dotnet_timestamps(self):
        self.assertEqual(parse_time("2026-11-19T10:00:00.1234567+00:00"),
                         datetime(2026, 11, 19, 10, 0, 0, 123456, tzinfo=timezone.utc))


if __name__ == "__main__":
    unittest.main()
