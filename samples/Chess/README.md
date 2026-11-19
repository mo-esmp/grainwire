# GrainWire chess sample

An online chess game: one server and two clients that can play each other.

- [`Chess.Server/Game/ChessGameGrain.cs`](Chess.Server/Game/ChessGameGrain.cs): the game. Seats, turns, clocks, resigning and
  forfeits, in about 100 lines. The rules of chess come from the MIT-licensed
  [Gera.Chess](https://www.nuget.org/packages/Gera.Chess) library.
- [`Chess.WebClient/src/main.ts`](Chess.WebClient/src/main.ts): the browser client in TypeScript. It draws what the
  server sends and sends moves.
- [`Chess.BlazorClient/Game/GameSession.cs`](Chess.BlazorClient/Game/GameSession.cs): the same client in Blazor
  WebAssembly, written against `GrainWire.Client`. See [The Blazor client](#the-blazor-client).

Everything else (connections, ordering, presence, reconnecting, saving) is GrainWire.

## Run it

You need the .NET 10 SDK and Node.js 20 or later.

Build the browser client once (it is written into `Chess.Server/wwwroot`):

```bash
cd clients/ts && npm ci && npm run build
```

```bash
cd samples/Chess/Chess.WebClient && npm ci && npm run build
```

Start the server:

```bash
dotnet run --project samples/Chess/Chess.Server
```

Open <http://localhost:5200>, create a game, and open the invite link in a second browser (or a
private window) under a different name. A third visitor watches.

## The Blazor client

`Chess.BlazorClient` is a second client for the same server. It runs in the browser as WebAssembly and uses
`GrainWire.Client`, the .NET client library, over the same WebSocket endpoint as the TypeScript client.

With the server running, start it in a second terminal:

```bash
dotnet run --project samples/Chess/Chess.BlazorClient
```

Open <http://localhost:5202>. A game created in one client can be joined from the other: the invite id is
the last part of the address in both (`/g/<id>` here, `#/g/<id>` in the TypeScript client).

The server address is set in [`Chess.BlazorClient/wwwroot/appsettings.json`](Chess.BlazorClient/wwwroot/appsettings.json).

## Rules in Python

`Chess.BridgeServer` is a second server for the same clients, with the rules of the game written in Python.
Its room grain, [`ChessRoom`](Chess.BridgeServer/ChessRoom.cs), only lists the commands and who may send them.
For everything else it calls a rules service and applies the answer:

- [`Chess.BridgeServer/Bridge/ExternalRoomGrain.cs`](Chess.BridgeServer/Bridge/ExternalRoomGrain.cs): the bridge.
  It turns every hook and command into one `POST /decide` with the room's state, and applies the decision with
  the ordinary `RoomGrain` methods. It is not specific to chess.
- [`rules-python/chess_rules.py`](rules-python/chess_rules.py): the game, as one function that takes the state
  and what happened and returns the new state and what to send. The contract is described in
  [`rules-python/README.md`](rules-python/README.md).

The grain still does everything GrainWire promises: one command at a time, nothing sent before the commit,
retries that never run twice, saving, and recovery. The Python service keeps no state, so it can be restarted
or run as several copies. If it fails or is too slow, the command is undone and the player gets an error.

Run it (after building the browser client as above). Start the rules service as described in
[`rules-python/README.md`](rules-python/README.md#run-it), then:

```bash
dotnet run --project samples/Chess/Chess.BridgeServer
```

Open <http://localhost:5300>. It serves the same browser client. The Blazor client can use it too: set `Chess:ServerUrl`
in its [`appsettings.json`](Chess.BlazorClient/wwwroot/appsettings.json) to `ws://localhost:5300/mqtt`.

`npm run e2e:bridge` in `Chess.WebClient/` plays the same browser games as `npm run e2e`, against this server.
It starts the rules service with `python` (set `PYTHON` to use another one, such as a virtual environment's).

## Things to try

- **Drop a player.** Turn off one browser's network (DevTools → Network → Offline). The opponent
  sees "reconnecting…". Turn it back on: the game is back, with no reload.
- **Stay away for a minute.** After the 60-second grace period the absent player forfeits.
- **Play an illegal move.** The server refuses it and the board does not change.
- **Let the clock run out.** The flag falls on the server, even if nobody is connected.

## What the sample leaves out

- **Real sign-in.** The name you type is trusted as your identity (GrainWire's development
  authenticator). A real app registers its own `IConnectionAuthenticator`, for example one that
  validates a JWT.
- **Durable storage.** Games are saved with Orleans' in-memory storage, so they survive a grain
  restart but not a server restart. Swap in any Orleans storage provider in `Program.cs`.
- **Chess niceties.** Pawns always promote to a queen in the UI, and there are no draw offers.

## Develop

`npm run dev` in `Chess.WebClient/` serves the site with hot reload and forwards `/mqtt` to the server on
port 5200. `npm run e2e` builds the site and plays a game in two real browsers with Playwright
(run `npx playwright install chromium` once first).
