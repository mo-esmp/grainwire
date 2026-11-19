# API reference

Every API you use to build on GrainWire, with what each one is for.

- [The room API](#the-room-api): what a room class on the server can declare, read, override and
  call. This is `RoomGrain<TState>`.
- [The client API](#the-client-api): what the .NET client (`GrainWire.Client`) and the TypeScript
  client (`@grainwire/client`) offer. The two have the same shape, so they are listed side by side.

The examples come from the [chess sample](../samples/Chess/README.md):
[`ChessGameGrain.cs`](../samples/Chess/Chess.Server/Game/ChessGameGrain.cs) on the server,
[`GameSession.cs`](../samples/Chess/Chess.BlazorClient/Game/GameSession.cs) for the .NET client and
[`main.ts`](../samples/Chess/Chess.WebClient/src/main.ts) for the TypeScript client.

This page says what exists. For how rooms behave (the commit steps, presence, saving), read
[Rooms](rooms.md).

## The room API

A room is a class that derives from `RoomGrain<TState>`. It lives in the `GrainWire` namespace, in
the `GrainWire.Abstractions` package, which `GrainWire.Server` brings in.

```csharp
[RoomType("chess")]
public class ChessGameGrain : RoomGrain<GameState>
{
    [OnMessage("move", Roles = "white,black")]
    public void Move(PlayerId player, MoveRequest request) { ... }

    protected override Task<JoinResult> OnJoin(JoinContext context) { ... }
}
```

A room class has four kinds of API:

| Kind | What it is | Examples |
|---|---|---|
| [Attributes](#attributes) | Declare the room type and its commands. | `[RoomType]`, `[OnMessage]` |
| [Properties](#properties) | Read the room's data. | `State`, `Members`, `Now` |
| [Hooks](#hooks) | Methods GrainWire calls. You override them. | `OnJoin`, `OnDeadline`, `GetSnapshot` |
| [Actions](#actions) | Methods you call from a handler or a hook. | `Broadcast`, `Reject`, `Schedule` |

### Attributes

| Attribute | Purpose |
|---|---|
| `[RoomType("chess")]` | Names the room type. Clients use this name when they join. Required: a silo does not start if a room class has none. |
| `[OnMessage("move")]` | Marks a method as the handler of the command `move`. |
| `[OnMessage("move", Roles = "white,black")]` | The same, but only members with one of these roles may send it. Others get the code `forbidden`. |

Names of room types and commands may hold 1 to 128 characters from `A-Z`, `a-z`, `0-9`, `_`, `.`,
`~` and `-`. They are case-sensitive. A room may have only one handler per command name.

A command handler:

- may take a `PlayerId` as its first parameter (the sender), then at most one payload parameter;
- may return nothing, a value, a `Task` or a `Task<T>`; a returned value goes back to the client as
  the result of its request;
- may be public or private.

`TState` is a class with a parameterless constructor. Mark it `[GenerateSerializer]` and put an
`[Id(n)]` on each member, so GrainWire can copy and save it.

### Properties

| Property | Type | Purpose |
|---|---|---|
| `State` | `TState` | The room's state. Changes are kept only if the command commits. |
| `RoomId` | `string` | The id of this room, for example the game id. |
| `RoomType` | `string` | The name given to `[RoomType]`. |
| `Now` | `DateTimeOffset` | The current UTC time. Use it in place of `DateTimeOffset.UtcNow`, so tests can control the clock. |
| `Members` | `IReadOnlyList<RoomMember>` | Everyone in the room, with role and presence. |
| `FindMember(player)` | `RoomMember?` | One member, or `null` if the player is not a member. |

A `RoomMember` has `Player` (a `PlayerId`), `Role` (or `null`), `Presence` (`Online`,
`Reconnecting` or `Left`) and `IsOnline`.

A `PlayerId` wraps the player's id; read it with `.Value`. It always comes from the authenticated
connection, never from a message, so a client cannot pretend to be someone else.

In the chess sample, `State` holds the position and the clocks, and `Now` stamps the start of each
turn.

### Hooks

GrainWire calls these at set moments. Override the ones you need; each has a default.

| Hook | Called when | Default | In the chess sample |
|---|---|---|---|
| `Task<CreateResult> OnCreate(CreateContext context)` | A client asks to create the room. | Allow | Reads the time control and sets both clocks. |
| `Task<JoinResult> OnJoin(JoinContext context)` | A new player asks to join. | Accept, no role | Gives out the white seat, then black, then spectator. |
| `Task<JoinResult> OnRejoin(JoinContext context)` | An existing member comes back. Use it for bans or revoked invites. The role is kept. | Accept | Not used. |
| `Task OnPresenceChanged(PlayerId player, PresenceStatus status)` | A member went online or started reconnecting. Other members are told without your help. | Nothing | Not used. |
| `Task OnPlayerLeft(PlayerId player, LeaveReason reason)` | A member's grace period ran out, just before they are removed. | Nothing | The absent player forfeits. |
| `Task OnDeadline(string name)` | A deadline set with `Schedule` is due. | Nothing | The flag falls: the player to move loses on time. |
| `object? GetSnapshot(PlayerId viewer)` | A player needs the current picture: after joining, reconnecting or catching up. Return only what this viewer may see. | The whole `State` | Returns a `GameView`, the same for every viewer. |

`OnPlayerLeft` is not called for `Kick` or `RemoveMember`, so its `reason` is always
`LeaveReason.GraceExpired`.

The hooks receive and return these types:

| Type | Members | Purpose |
|---|---|---|
| `CreateContext` | `Creator`, `Claims`, `Options` | Who is creating the room, the claims of their connection, and the room settings they chose. |
| `CreateResult` | `Allow()`, `Deny(reason)` | Your answer from `OnCreate`. The reason is sent to the client. |
| `JoinContext` | `Player`, `Claims`, `Data`, `IsCreator` | Who is joining, their claims, the join data they sent, and whether this join created the room. |
| `JoinResult` | `Accept(role)`, `Reject(reason)` | Your answer from `OnJoin` and `OnRejoin`. The role is optional. |
| `RoomPayload` | `IsEmpty`, `As<T>()` | Data the client sent with a create or a join. `As<T>()` reads it as your type, or returns `default` when it is empty. |

```csharp
protected override Task<CreateResult> OnCreate(CreateContext context)
{
    int minutes = context.Options.As<CreateGameOptions>()?.Minutes ?? 10;
    ...
    return Task.FromResult(CreateResult.Allow());
}
```

### Actions

Call these from a command handler, a hook, or inside `ExecuteAsync`. Anywhere else they throw
`InvalidOperationException`.

Everything they do is held back until the command commits. If the handler calls `Reject` or throws,
the state is restored and nothing is sent.

| Method | Purpose | In the chess sample |
|---|---|---|
| `Broadcast(name, data, except)` | Sends an event to every member. `data` and `except` are optional; `except` leaves one member out, for example the sender. | `started`, `moved` and `finished` events. |
| `SendTo(player, name, data)` | Sends a private event to one member. | Not used. |
| `Reject(reason, code)` | Ends the command now. The sender gets the reason and the optional code; nobody else sees anything. | "It's not your turn", with the code `not-your-turn`. |
| `SetRole(player, role)` | Changes a member's role, or clears it with `null`. Everyone is told. | Not used. |
| `Kick(player, reason)` | Removes a member. Their client is told it was kicked, with the reason. | Not used. |
| `RemoveMember(player)` | Removes a member without calling it a kick, for example when they give up their seat. | Not used. |
| `CloseRoom(reason)` | Tells every member, then deletes the room. | Not used. |
| `Schedule(name, dueAt)` | Sets a named deadline at a point in time. Using the same name again moves it. `OnDeadline` runs when it is due. | |
| `Schedule(name, delay)` | The same, counted from `Now`. | Sets the `flag` deadline to the time left on the clock of the player to move. |
| `CancelSchedule(name)` | Removes a named deadline. Does nothing if there is none. | Stops the `flag` deadline when the game ends. |
| `ExecuteAsync(action)` | Runs your own code as one room operation. Use it in grain methods you add yourself, so they get the same copy, save, then send steps as a command. | Not used. |

Event names may not be empty or start with `$`.

`SetRole`, `Kick`, `RemoveMember` and the deadline calls take effect when the command commits, so
`Members` still shows the old picture inside the same handler.

### Exceptions

| Exception | Purpose |
|---|---|
| `RoomRejectedException` | What `Reject` throws. You can also throw it yourself from a helper method. |
| `RoomNotCommittedException` | Thrown by `ExecuteAsync` when the change could not be saved and was undone. |

Any other exception from a handler undoes the command, and the client gets the code `handler-error`.

### Setup and options

| API | Purpose |
|---|---|
| `silo.AddGrainWire()` | Registers what room grains need in the Orleans silo. |
| `gw.ConfigureRoom("chess", options => ...)` | Sets the options of one room type. |
| `gw.ConfigureAllRooms(options => ...)` | Sets options for every room type. A room type's own settings are applied after these. |
| `gw.UseJson(jsonOptions)` | Uses your own `JsonSerializerOptions` for payloads. |
| `gw.UseSerializer(serializer)` | Replaces the payload serializer with your own `IRoomSerializer`. It must still produce JSON. |

```csharp
silo.AddGrainWire(gw => gw.ConfigureRoom("chess", room => room.GracePeriod = TimeSpan.FromSeconds(60)));
```

The options of a room type (`RoomOptions`):

| Option | Default | Purpose |
|---|---|---|
| `GracePeriod` | 30 s | How long a member may have no connection before they are removed. |
| `MaxMembers` | 1000 | The most members a room can hold. Further joins get the code `room-full`. |
| `StorageProvider` | none | Name of an Orleans grain storage provider. Without one the room lives in memory only. |
| `Rollback` | `Copy` | How a failed handler is undone. `None` skips the copy; use it only when state is large and every handler checks everything before it changes anything. |
| `LeaseTimeout` | 25 s | A gateway that has not been heard from for this long is treated as gone. |
| `LeaseCheckInterval` | 5 s | How often the room looks for gateways that are gone. |
| `DedupCapacity` | 256 | How many command ids are remembered per player, so a retried command does not run twice. |
| `DedupWindow` | 10 min | Command ids younger than this are always remembered, even beyond `DedupCapacity`. |

The gateway (`AddGrainWireGateway`, `MapGrainWire`) and sign-in (`IConnectionAuthenticator`) are
covered in [Hosting](hosting.md) and [Security](security.md).

## The client API

Both clients do the same work: connect, join rooms, deliver events in order, catch up after a
reconnect, and retry requests safely. The names differ only by each language's habits.

| | .NET | TypeScript |
|---|---|---|
| Package | `GrainWire.Client` | `@grainwire/client` |
| Stop listening | Dispose the `IDisposable` a subscription returns | Call the function a subscription returns |
| Handlers run | One at a time, in order, on a thread-pool thread | One at a time, in order, on the page's thread |

In a .NET UI app, switch to the UI thread inside a handler.

```csharp
await using var client = await GrainWireClient.ConnectAsync(new GrainWireClientOptions
{
    Url = new Uri("ws://localhost:5200/mqtt"),
    Username = "alice",
});
var room = await client.JoinAsync<GameView>("chess", gameId, new JoinOptions { Create = true });

room.OnSnapshot((view, info) => Render(view));
room.On<MovedEvent>("moved", moved => Apply(moved));
await room.RequestAsync("move", new MoveRequest("e2", "e4"));
```

```ts
const client = await connect({ url: "ws://localhost:5200/mqtt", username: "alice" });
const room = await client.join<GameView>("chess", gameId, { create: true });

room.onSnapshot((view, info) => render(view));
room.on<MovedEvent>("moved", (moved) => apply(moved));
await room.request("move", { from: "e2", to: "e4" });
```

### Connecting

| .NET | TypeScript | Purpose |
|---|---|---|
| `GrainWireClient.ConnectAsync(options)` | `connect(options)` | Opens the connection. Completes once the client can receive messages. Fails with `ConnectException` / `ConnectError`. |
| `client.State` | `client.state` | `Connected`, `Reconnecting` or `Disconnected`. |
| `client.StateChanged` (event) | `client.onStateChange(listener)` | Tells you when the connection goes down or comes back, so the page can show it. |
| `client.PlayerId` | `client.playerId` | The player id the server gave this connection. |
| `client.ClientId` | `client.clientId` | The id of this connection. It stays the same across reconnects. |
| `client.JoinAsync<TView>(type, id, options)` | `client.join<TView>(type, id, options)` | Joins a room. See [Joining a room](#joining-a-room). |
| `client.DisposeAsync()` | `client.close()` | Closes the connection. Requests still waiting fail. |

The client reconnects by itself. After a reconnect it joins its rooms again and re-sends requests
that have no reply yet.

Connection options:

| .NET (`GrainWireClientOptions`) | TypeScript (`ConnectOptions`) | Default | Purpose |
|---|---|---|---|
| `Url` | `url` | | The gateway address, for example `wss://example.com/mqtt`. Required. |
| `AccessToken` | `token` (a string) | | The access token your server's authenticator checks. |
| `AccessTokenProvider` | `token` (a function) | | Returns the token. Called again before every reconnect, so an expired token can be replaced. |
| `Username` | `username` | `token` | What it means is up to your server's authenticator. The development authenticator takes it as the player id. |
| `ClientId` | `clientId` | random | The id of this connection. In a browser the TypeScript client keeps it per tab. |
| `RequestTimeout` | `requestTimeoutMs` | 15 s | How long a request waits for a reply. |
| `RetryInterval` | `retryIntervalMs` | 3 s | How often a request without a reply is sent again. |
| `SyncInterval` | `syncIntervalMs` | 15 s | A room that has been silent this long is checked against the server. |
| `GapWait` | `gapWaitMs` | 250 ms | How long to wait for a missing event before asking for a snapshot. |
| `ReconnectDelay` | `reconnectDelayMs` | 500 ms | The first wait before reconnecting. It doubles each time. |
| `MaxReconnectDelay` | `maxReconnectDelayMs` | 15 s | The longest wait before reconnecting. |
| `ConnectTimeout` | `connectTimeoutMs` | 10 s | How long a connection attempt may take. |
| `ConfigureMqtt` | `mqtt` | | Extra settings for the MQTT library underneath, such as TLS options. |
| `SerializerOptions` | | web defaults | .NET only. How payloads are turned into JSON and back. The default uses camelCase names, which matches the server's default. |
| `OnHandlerError` | | | .NET only. Called when one of your handlers throws. Without it, such exceptions are ignored. |
| `MaxPendingHandlerCalls` | | 1000 | .NET only. How many handler calls may wait. Beyond that, the room skips ahead and carries on from a fresh snapshot. |

### Joining a room

`JoinAsync` / `join` completes once the first snapshot has arrived, so the room you get back
already has its view and members. Joining a room the client is already in returns the same room.

| .NET (`JoinOptions`) | TypeScript (`JoinOptions`) | Purpose |
|---|---|---|
| `Create` | `create` | Create the room if it does not exist. Default `false`: the room must exist. |
| `Options` | `options` | Room settings, used only when the room is created. The room reads them in `OnCreate` as `context.Options`. |
| `Data` | `data` | Your own join data, such as an invite code. The room reads it in `OnJoin` and `OnRejoin` as `context.Data`. |

`TView` is the type of the object the room returns from `GetSnapshot`. The .NET client also has
`JoinAsync(type, id, options)` without a type, which leaves the view as raw JSON.

The chess clients create a game with `Create = true` and the chosen minutes as `Options`, and join
one from an invite link with neither.

### The room

What a joined room lets you read:

| .NET | TypeScript | Purpose |
|---|---|---|
| `room.View` | `room.view` | The latest snapshot. Events are not applied to it; it changes only when a new snapshot arrives. |
| `room.Members` | `room.members` | Who is in the room, with role and status. Kept up to date for you. |
| `room.You` | `room.you` | Your own membership. `You.Role` is the role the room gave you in `OnJoin`. |
| `room.Type`, `room.Id` | `room.type`, `room.id` | The room type and id. |
| `room.Key` | `room.key` | The two joined as `type/id`. |
| `room.IsClosed` | `room.closed` | True once the room has ended for this client. |
| `room.ClosedReason` | `room.closedReason` | Why it ended, or nothing while it is open. |

What a room tells you. Each of these takes a handler:

| .NET | TypeScript | Called when |
|---|---|---|
| `room.OnSnapshot((view, info) => ...)` | `room.onSnapshot((view, info) => ...)` | A snapshot arrives: after joining, after a reconnect, and whenever the room had to catch up. If the room already has a snapshot, the handler is called with it at once. Replace your whole picture with it. `info` holds `Members` and `You`. |
| `room.On<TData>(name, data => ...)` | `room.on<TData>(name, (data) => ...)` | An event with this name arrives: one the room sent with `Broadcast` or `SendTo`. A second parameter gives the event's number (`Seq`). |
| `room.OnAny((name, data, meta) => ...)` | `room.onAny((name, data, meta) => ...)` | Any event arrives. Useful for logging. |
| `room.OnPresence(change => ...)` | `room.onPresence((change) => ...)` | A member joins, drops, comes back, leaves or changes role. `change` holds `Player`, `Status` and `Role`. |
| `room.OnClosed(info => ...)` | `room.onClosed((info) => ...)` | The room ends for this client. Called once. `info` holds `Reason` and `Message`. |

Events arrive in order, each exactly once. Apply them to your own state in the `On` handlers.

What you can do with a room:

| .NET | TypeScript | Purpose |
|---|---|---|
| `room.RequestAsync<TResult>(name, data)` | `room.request<TResult>(name, data)` | Sends a command and waits. Completes with the handler's return value once the command has committed. See [Errors](#errors). |
| `room.RequestAsync(name, data)` | | The same, when you do not need the return value. |
| `room.Send(name, data)` | `room.send(name, data)` | Sends a command and does not wait. Nothing tells you whether it was applied. |
| `room.SyncAsync()` | `room.sync()` | Checks the room against the server now. Use it after an unknown outcome. |
| `room.LeaveAsync()` | `room.leave()` | Stops following the room on this connection. The player stays a member until the server's grace period ends. |

A request takes an optional time limit: `timeout` in .NET, `{ timeoutMs }` in TypeScript. The .NET
methods that wait also take a `CancellationToken`.

The chess clients use `OnSnapshot` to draw the board, `On` for the `started`, `moved` and
`finished` events, `OnPresence` to show who is reconnecting, `OnClosed` to say the game is gone,
and `RequestAsync` / `request` for `move` and `resign`.

### Values

| .NET | TypeScript | Values |
|---|---|---|
| `ConnectionState` | `ConnectionState` | `Connected`, `Reconnecting`, `Disconnected` |
| `Member` | `Member` | `Player`, `Role`, `Status` |
| `MemberStatus` | `PresenceStatus` | `Online`, `Reconnecting`, `Left` |
| `PresenceChange` | `PresenceData` | `Player`, `Status`, `Role` |
| `SnapshotInfo` | `SnapshotInfo` | `Members`, `You` |
| `EventMeta` | `EventMeta` | `Seq` |
| `ClosedInfo` | `ClosedInfo` | `Reason`, `Message` |
| `RoomClosedReason` | `ClosedReason` | See below |

TypeScript writes the names in camelCase (`player`, `status`) and the values as lower-case strings
(`"online"`, `"reconnecting"`).

Why a room ended:

| Reason | Meaning | Caused on the server by |
|---|---|---|
| `Closed` | The room was closed. | `CloseRoom` |
| `Kicked` | You were kicked. | `Kick` |
| `Removed` | You are no longer a member. | `RemoveMember`, or your grace period ran out |
| `Lost` | The room's state was lost on the server. | A server failure, for a room without storage |
| `Left` | You left the room yourself. | `LeaveAsync` / `leave` on this client |

### Errors

A request or a join fails with one of these:

| .NET | TypeScript | Meaning | What to do |
|---|---|---|---|
| `RejectedException` | `RejectedError` | The room refused it. Nothing changed. `Reason` is the text and `Code` the code the room passed to `Reject`. | Show the reason. |
| `RoomException` | `RoomError` | It could not be processed. Nothing changed. `Code` says why, for example `room-not-found` or `handler-error`. | Show an error. |
| `OutcomeUnknownException` | `OutcomeUnknownError` | No reply came in time. The command may or may not have been applied. | Call `SyncAsync` / `sync` and look at the state. |
| `NotConnectedException` | `NotConnectedError` | Not sent: there is no connection right now. | Ask the user to try again once the connection is back. |
| `RoomClosedException` | `RoomClosedError` | The room has ended for this client. | Stop using the room. |
| `ConnectException` | `ConnectError` | The first connection failed. Only from `ConnectAsync` / `connect`. | Check the address and the token. |

In .NET all of them derive from `GrainWireClientException`.

A request without a reply is sent again with the same command id, so it never runs twice.

The full list of codes is in [Wire protocol](protocol.md).

```csharp
try
{
    await room.RequestAsync("move", new MoveRequest(from, to));
}
catch (RejectedException rejected)
{
    Show(rejected.Reason);          // "That move is not legal"
}
catch (OutcomeUnknownException)
{
    await room.SyncAsync();         // look at the state to see what happened
}
```
