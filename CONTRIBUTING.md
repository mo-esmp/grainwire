# Contributing

Thanks for your interest in GrainWire. Issues and pull requests are welcome.

## Layout

| Path | What |
|---|---|
| `src/GrainWire.Abstractions` | Contracts, `RoomGrain<TState>` (the room pipeline), silo registration |
| `src/GrainWire.Server` | The MQTT gateway |
| `clients/ts` | `@grainwire/client`, the browser client |
| `tests/GrainWire.Tests` | .NET tests: room pipeline, persistence, wire format, gateway, failover |
| `tests/GrainWire.TestHost` | A server with fault injection, used by the TypeScript tests |
| `samples/Chess` | The chess sample and its browser test |
| `bench/GrainWire.Bench` | The benchmark |
| `docs` | Documentation, the design, and decision records (`docs/adr`) |

## Build and test

You need the .NET 10 SDK and Node.js 20 or later.

```bash
dotnet test
```

The TypeScript tests start `tests/GrainWire.TestHost` themselves (and build it if needed):

```bash
cd clients/ts && npm ci && npm test
```

The chess sample's browser test needs Playwright's Chromium once
(`npx playwright install chromium`):

```bash
cd samples/Chess/Web && npm ci && npm run e2e
```

## What a change needs

- **Tests.** Most behaviour is tested through the grain interface with a fake gateway
  (`RoomCoreTests`), which is fast and needs no MQTT. Use the gateway and failover tests for
  anything that depends on real connections or on a server stopping.
- **The protocol.** If the wire format changes, update `docs/protocol.md` and the example
  messages in `docs/protocol-golden` in the same change. Both the .NET and the TypeScript tests
  read those files.
- **The guarantees.** `docs/guarantees.md` is a promise to users. A change that weakens one
  needs a discussion in an issue first.
- **Warnings are errors** in the .NET build.

## Design background

- [The design](docs/specs/2026-11-15-grainwire-design.md) explains why things are the way they are.
- [`docs/adr`](docs/adr) records what was learned while building it, including where the
  implementation differs from the design.
