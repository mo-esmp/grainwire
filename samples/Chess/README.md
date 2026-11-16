# GrainWire chess sample

An online chess game in two small parts:

- [`Server/ChessGameGrain.cs`](Server/ChessGameGrain.cs): the game. Seats, turns, clocks, resigning and
  forfeits, in about 100 lines. The rules of chess come from the MIT-licensed
  [Gera.Chess](https://www.nuget.org/packages/Gera.Chess) library.
- [`Web/src/main.ts`](Web/src/main.ts): the browser. It draws what the server sends and sends moves.

Everything else (connections, ordering, presence, reconnecting, saving) is GrainWire.

## Run it

You need the .NET 10 SDK and Node.js 20 or later.

Build the browser client once (it is written into `Server/wwwroot`):

```bash
cd clients/ts && npm ci && npm run build
```

```bash
cd samples/Chess/Web && npm ci && npm run build
```

Start the server:

```bash
dotnet run --project samples/Chess/Server
```

Open <http://localhost:5200>, create a game, and open the invite link in a second browser (or a
private window) under a different name. A third visitor watches.

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

`npm run dev` in `Web/` serves the site with hot reload and forwards `/mqtt` to the server on
port 5200. `npm run e2e` builds the site and plays a game in two real browsers with Playwright
(run `npx playwright install chromium` once first).
