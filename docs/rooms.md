# Rooms

A room is one Orleans grain. It owns the room's state and handles one operation at a time, so
handlers never need locks. This page lists everything a room class can do.

```csharp
[RoomType("chess")]
public class ChessGameGrain : RoomGrain<GameState> { ... }
```

`GameState` is any class with a parameterless constructor, marked `[GenerateSerializer]` with an
`[Id(n)]` on each member.

## Commands

```csharp
[OnMessage("move", Roles = "white,black")]
public void Move(PlayerId player, MoveRequest request) { ... }
```

- A handler takes the `PlayerId` of the sender and, optionally, one payload parameter.
- It may return nothing, a value, a `Task`, or a `Task<T>`. A returned value goes back to the
  caller as the result of `room.request(...)`.
- `Roles` limits the command to members with one of those roles. Leave it out to allow every
  member.

Every command runs like this:

1. The room checks that the sender is a member, has an allowed role, and is talking to this
   room and not an earlier one with the same id.
2. If the same command id was already processed, the recorded outcome is returned and nothing
   else happens.
3. The state is copied.
4. The handler runs. What it sends is held back.
5. If the handler calls `Reject(...)` or throws, the copy is restored and nothing is sent.
6. Otherwise the state is saved (for persistent rooms), and only then are the events and the
   reply released.

So clients never see a change that was not kept.

## What a handler can do

| Call | Effect, once the command commits |
|---|---|
| `Broadcast(name, data, except: player)` | Sends an event to every member, optionally all but one. |
| `SendTo(player, name, data)` | Sends a private event to one member. It is ordered together with public events. |
| `Reject(reason, code)` | Ends the command now. Nothing is kept or sent. The caller gets `RejectedError`. |
| `SetRole(player, role)` | Changes a member's role. Everyone is told. |
| `Kick(player, reason)` | Removes a member and tells their connections why. |
| `RemoveMember(player)` | Removes a member (for example because they gave up their seat). |
| `CloseRoom(reason)` | Tells everyone, then deletes the room. |
| `Schedule(name, dueAt)` / `CancelSchedule(name)` | Sets or cancels a named deadline. See below. |

Also available: `State`, `Members` (with role and presence), `FindMember(player)`, `RoomId`,
`RoomType` and `Now`.

`Kick`, `RemoveMember`, `SetRole` and the deadline calls take effect when the command commits,
so `Members` still shows the old picture inside the same handler.

## Hooks

Override these when you need them. Each has a sensible default.

| Hook | When | Default |
|---|---|---|
| `OnCreate(CreateContext)` | Someone asks to create the room. Read `context.Options`; return `Allow()` or `Deny(reason)`. | allow |
| `OnJoin(JoinContext)` | A new player asks to join. Return `Accept(role)` or `Reject(reason)`. | accept, no role |
| `OnRejoin(JoinContext)` | An existing member comes back. Use it for bans or revoked invites. The role is kept. | accept |
| `OnPresenceChanged(player, status)` | A member went online or started reconnecting. | nothing |
| `OnPlayerLeft(player, reason)` | A member's grace period ran out, just before they are removed. | nothing |
| `OnDeadline(name)` | A scheduled deadline is due. | nothing |
| `GetSnapshot(viewer)` | Builds what one player may see. Return less than `State` to hide information. | the whole state |

Hooks run through the same steps as commands: what they send is held back, and a hook that
throws changes nothing.

`OnPlayerLeft` is not called for `Kick` or `RemoveMember`: the handler that removed the member
already knows.

## Private state

`GetSnapshot(viewer)` is called once per player, so a card game returns only that player's hand:

```csharp
protected override object GetSnapshot(PlayerId viewer) =>
    new TableView(State.Table, State.Hands[viewer.Value], State.Hands.ToDictionary(h => h.Key, h => h.Value.Count));
```

Use `SendTo` for private events ("you drew the ace"). A player's private and public events arrive
in one ordered stream.

## Presence and the grace period

Each member is **online** (at least one connection), **reconnecting** (no connection, grace
period running) or gone. When the grace period ends, `OnPlayerLeft` runs and the member is
removed. Other members are told at each step.

A player may have several tabs open. They stay online while any tab is connected.

## Deadlines

```csharp
Schedule("flag", TimeSpan.FromMinutes(5));

protected override Task OnDeadline(string name)
{
    if (name == "flag") { /* the clock ran out */ }
    return Task.CompletedTask;
}
```

A deadline is part of the room. In a persistent room it survives a restart; one that came due
while the room was not running fires as soon as the room runs again.

## Calling the room from elsewhere

To change the room from your own grain methods (another grain calls in, say), wrap the work in
`ExecuteAsync`. It gets the same copy, save, then send treatment as a command:

```csharp
public Task StartTournamentRound() => ExecuteAsync(() =>
{
    State.Round++;
    Broadcast("round-started", State.Round);
    return Task.CompletedTask;
});
```

Do not mark a room grain `[Reentrant]`, and do not add `[AlwaysInterleave]` or `[ReadOnly]`
methods to it. GrainWire refuses to start a silo that has one, because two operations could then
run at once.

## Options

```csharp
silo.AddGrainWire(gw => gw
    .ConfigureAllRooms(room => room.GracePeriod = TimeSpan.FromSeconds(20))
    .ConfigureRoom("chess", room =>
    {
        room.StorageProvider = "games";
        room.GracePeriod = TimeSpan.FromSeconds(60);
    }));
```

| Option | Default | |
|---|---|---|
| `GracePeriod` | 30 s | How long a member may be without a connection before they are removed. |
| `MaxMembers` | 1000 | |
| `StorageProvider` | none | Name of an Orleans grain storage provider. Without it the room lives in memory only. |
| `Rollback` | `Copy` | `None` skips the copy. Only for large state whose handlers check everything before changing anything. |
| `LeaseTimeout` | 25 s | A gateway that has not been heard from for this long is treated as gone. |
| `DedupCapacity` / `DedupWindow` | 256 / 10 min | How many command ids are remembered per player for safe retries. |

## Saving

**Without a storage provider** (the default) a room lives in memory. It stays alive while it
has members. If its server fails, the room is lost and clients are told (`onClosed`, reason
`lost`). When the last member is gone, the room ceases to exist.

**With a storage provider**, the room is saved after every successful operation: the state, the
members and their roles, the remembered command ids, and the deadlines. If saving fails, the
operation is undone and the caller gets the error `not-committed`.

After a restart, a persistent room carries on. Clients get a fresh snapshot without doing
anything, every member starts as "reconnecting", and the grace period starts over, so nobody is
removed because a server failed.

Presence, connections and event numbering are never saved. They start fresh every time the room
is activated.

A persistent room with no members stays in storage until a handler calls `CloseRoom`.

## The payload format

Payloads are JSON, written with System.Text.Json and camelCase names. Pass your own
`JsonSerializerOptions` with `gw.UseJson(options)`. In v1 the envelope is always JSON, so a custom
`IRoomSerializer` must also produce JSON.
