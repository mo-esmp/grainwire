# ADR 0001: Library APIs confirmed by the Phase 0 spike

Date: 2026-11-15
Status: Accepted

A throwaway host (ASP.NET Core + Orleans silo + MQTTnet broker over WebSockets, plus a
separate Orleans client host) was run to confirm the APIs the design depends on.

## Pinned versions

| Package | Version |
|---|---|
| .NET target | `net10.0` (builds with SDK 10 or 11) |
| Microsoft.Orleans.* | 10.4.2 |
| MQTTnet, MQTTnet.Server, MQTTnet.AspNetCore | 5.2.0.1603 |
| MQTT.js (`mqtt` on npm) | 5.16.1 |

## MQTTnet 5 findings

| Need | Result | API |
|---|---|---|
| Broker over WebSockets in ASP.NET Core | Works | `AddHostedMqttServer(o => o.WithoutDefaultEndpoint())`, `AddMqttConnectionHandler()`, `AddConnections()`, `MapMqtt("/mqtt")`, `UseMqttServer(...)` |
| Reject CONNECT with a reason | Works; client sees code and reason string | `ValidatingConnectionAsync`: `ReasonCode`, `ReasonString` |
| Tell the client its player id | Works | `ValidatingConnectionEventArgs.ResponseUserProperties` (CONNACK user properties) |
| Reject SUBSCRIBE per topic | Works; per-topic `NotAuthorized` | `InterceptingSubscriptionAsync`: `Response.ReasonCode`, `ProcessSubscription = false` |
| Shared subscriptions | Rejected by MQTTnet itself (`SharedSubscriptionsNotSupported`) | none needed |
| Swallow a PUBLISH (no routing) | Works; QoS 1 sender gets PUBACK `Success` | `InterceptingPublishAsync`: `ProcessPublish = false` |
| Reject a PUBLISH with a reason | Works; PUBACK carries code and reason | `Response.ReasonCode`, `Response.ReasonString` |
| Inject a message for one client | Works | `MqttServer.InjectApplicationMessage` to `clients/{clientId}/inbox` |
| Per-client outgoing queue limit | Works; overflow drops messages and raises events | `WithMaxPendingMessagesPerClient`, `ApplicationMessageEnqueuedOrDroppedAsync`, `QueuedApplicationMessageOverwrittenAsync` |
| Per-connection state | Works | `SessionItems` on every event |

Three things behave differently from what the design assumed:

1. **Injected messages also pass through `InterceptingPublishAsync`.** The gateway tags its
   own messages with a reserved sender id (`$grainwire`) and lets only those through. Real
   clients cannot use an id starting with `$`.
2. **Will messages cannot be seen at CONNECT.** `ValidatingConnectionEventArgs` does not
   expose the Will. A Will is published through `InterceptingPublishAsync` under the dying
   client's id, so it gets the same policy as any other publish from that client: only
   `rooms/.../cmd|sys/...` topics, never routed to other clients. The design said "rejected at
   CONNECT"; the effect (a Will can never reach another client) is the same.
3. **Queue overflow drops messages; it does not disconnect.** The gateway disconnects the
   client itself when it sees a drop, so the client reconnects and gets a snapshot.

## Orleans 10 findings

| Need | Result | API |
|---|---|---|
| Room type → grain type | Works | custom attribute implementing `IGrainTypeProviderAttribute`; resolve with `GetGrain<IRoomGrain>(GrainId.Create(grainType, key))` |
| Unknown room type | Call retries placement for several seconds, then throws `OrleansException` | the gateway checks the cluster manifest first (`IClusterManifestProvider`) |
| Observer hosted in the silo process | Works | `IGrainFactory.CreateObjectReference<T>` from a hosted service |
| Observer hosted in an Orleans client process | Works | `IClusterClient.CreateObjectReference<T>` |
| One-way calls to a dead observer | No exception in the grain | `[OneWay]` |
| Optional persistence chosen at runtime | Works when created in the grain constructor | `IPersistentStateFactory.Create<T>(GrainContext, new PersistentStateAttribute(name, provider))` |
| Rollback copy of state | Works | `DeepCopier.Copy(state)` (state type needs `[GenerateSerializer]`) |
| One-shot non-interleaving timers | Works | `RegisterGrainTimer(..., new GrainTimerCreationOptions { Period = Timeout.InfiniteTimeSpan, Interleave = false, KeepAlive = true })` |

## Consequences

- `GrainWire.Abstractions` references `Microsoft.Orleans.Sdk` and `Microsoft.Orleans.Runtime`
  (`IPersistentStateFactory` lives in the runtime assembly).
- A room stays active through gateway lease renewals and keep-alive timers; no explicit
  `DelayDeactivation` loop is needed.
