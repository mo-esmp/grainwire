# Chess rules in Python

The rules of the chess sample, written in Python and run as a small HTTP service. The room grain in
[`Chess.BridgeServer`](../Chess.BridgeServer) calls it for every room operation and applies what it decides.

- [`chess_rules.py`](chess_rules.py): the game. One function, `decide(request) -> decision`. The rules of chess
  come from [python-chess](https://python-chess.readthedocs.io).
- [`server.py`](server.py): `POST /decide` and `GET /health`, on Python's standard library.
- [`test_chess_rules.py`](test_chess_rules.py): the rules tested without a server or a grain.

## Run it

You need Python 3.11 or later.

```bash
cd samples/Chess/rules-python
```

```bash
python -m venv .venv
```

Activate it (`.venv\Scripts\activate` on Windows, `source .venv/bin/activate` elsewhere), then:

```bash
pip install -r requirements.txt
```

```bash
python server.py
```

It listens on <http://127.0.0.1:5310>. Then start the bridge server: see [the chess sample's README](../README.md#rules-in-python).

Tests:

```bash
python -m unittest
```

## How it fits with GrainWire

The grain keeps the room. This service only decides:

```
grain:   state + what happened  ──POST /decide──▶  rules service
grain:   ◀── new state + views + effects (or a rejection)
grain:   commits (and saves), then sends the effects
```

So the rules service:

- **keeps nothing between calls.** The state comes with every request. Any copy of the service can answer
  any call, and it can be restarted at any time.
- **never reads the clock.** It uses `now` from the request, and sets deadlines with `schedule` effects.
- **never talks to players.** It returns effects. The grain sends them only after the operation has committed,
  so a player never sees a change the room later forgets.
- **can be called twice for the same thing.** Same input, same answer.

If the service answers with an error, cannot be reached, or takes longer than `Rules:TimeoutSeconds`, the
operation is undone and the player gets an error. The room itself carries on.

## The contract

One endpoint: `POST /decide`, JSON in both directions, camelCase. The request must have a `Content-Length`.
The C# side of it is [`RulesContract.cs`](../Chess.BridgeServer/Bridge/RulesContract.cs).

### Request

```json
{
  "roomType": "chess",
  "roomId": "e31afe825def5fdb31ea2a402666498e",
  "now": "2026-11-19T16:48:30.1234567+00:00",
  "state": { "...": "what this service returned last time, or null for a new room" },
  "members": [ { "player": "alice", "role": "white", "presence": "online" } ],
  "input": { "kind": "command", "player": "alice", "name": "move", "data": { "from": "e2", "to": "e4" } }
}
```

| `input.kind` | When | Other fields | What the decision can do |
|---|---|---|---|
| `create` | The first player creates the room | `player`, `data` (the creation options) | `reject` refuses the creation |
| `join` | A new player joins | `player`, `data` (the join data), `isCreator` | `role` gives them a role; `reject` turns them away |
| `rejoin` | A member comes back | `player`, `data` | `reject` turns them away; their role is kept |
| `command` | A member sends a command | `player`, `name`, `data` | `reject` refuses it (the player gets `rejected`) |
| `deadline` | A deadline set with `schedule` is due | `name` | |
| `left` | A member's grace period ran out, or they were removed | `player`, `reason` (`grace-expired` or `removed`) | |
| `presence` | A member went online or is reconnecting (only if the grain sets `ForwardPresence`) | `player`, `status` | |

Times are ISO 8601. .NET writes seven fraction digits; parse with that in mind.

### Decision

```json
{
  "state": { "...": "the room's new state, any JSON" },
  "role": "white",
  "views": { "*": { "...": "what everyone sees" }, "alice": { "...": "what only alice sees" } },
  "effects": [
    { "kind": "broadcast", "name": "moved", "data": { "san": "e4" } },
    { "kind": "schedule", "name": "flag", "at": "2026-11-19T16:53:30.123Z" }
  ]
}
```

Or, to refuse: `{ "reject": { "reason": "That move is not legal", "code": "illegal-move" } }`. Nothing else is used then.

- **`views`** are the snapshots players get after joining, reconnecting or catching up: one per player id, with `*`
  for everyone else. They are how hidden information works (each player's own hand). Leave `views` out to keep
  the previous ones. A top-level `serverNow` field in a view is filled in with the time the snapshot is sent.
- **`effects`** run in order, after the commit:

| `kind` | Fields | GrainWire method |
|---|---|---|
| `broadcast` | `name`, `data`, `except` (a player id) | `Broadcast` |
| `sendTo` | `player`, `name`, `data` | `SendTo` |
| `setRole` | `player`, `role` | `SetRole` |
| `kick` | `player`, `reason` | `Kick` |
| `removeMember` | `player` | `RemoveMember` |
| `schedule` | `name`, `at` | `Schedule` |
| `cancelSchedule` | `name` | `CancelSchedule` |
| `close` | `reason` | `CloseRoom` |

An effect of an unknown kind, or one with a missing field, undoes the whole operation.

## In another language

Anything that serves this contract works: a Java service (Javalin or Spring with chesslib), Go, Node.js.
Point `Rules:Url` at it. The bridge grain does not change.
