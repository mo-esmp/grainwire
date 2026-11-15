# Changelog

## 0.1.0-preview.1 (not yet published)

First preview.

- `GrainWire.Abstractions`: `RoomGrain<TState>` with commit-then-publish, rollback, safe retries
  (command de-duplication), per-player ordered events, presence with grace periods, roles,
  private events, kicking, closing, durable deadlines, and optional persistence through any
  Orleans storage provider.
- `GrainWire.Server`: the MQTT gateway on MQTTnet 5 over WebSockets. Inbox-only subscriptions,
  a publish allow-list, limits, ordered delivery, lease renewal and recovery. Runs inside a silo
  or as a standalone Orleans client.
- `@grainwire/client`: the browser client. Ordering, catching up by snapshot, idle sync,
  reconnecting with back-off, and request retries.
- The chess sample, the wire protocol, benchmarks and documentation.
