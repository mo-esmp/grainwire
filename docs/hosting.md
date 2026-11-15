# Hosting

GrainWire has two parts that can run together or apart:

- **Room grains** run in Orleans silos.
- **The gateway** accepts MQTT connections over WebSockets and talks to the room grains.

Your room code is the same in every setup below.

## One process (the default)

The gateway runs inside the silo. This is what the getting-started guide and the chess sample use.

```csharp
builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();   // or any Orleans clustering provider
    silo.AddGrainWire();
});
builder.Services.AddGrainWireGateway();

var app = builder.Build();
app.MapGrainWire();                  // ws(s)://host/mqtt
app.Run();
```

## Several of those

Run the same app on more servers, with a real Orleans clustering provider (Azure Table, ADO.NET,
Redis, Kubernetes and so on), behind a load balancer that supports WebSockets.

A player's connection is on one server and their room's grain may be on another. GrainWire
handles that. No sticky sessions are needed: a client that reconnects to a different server
joins again and gets a snapshot.

## Separate gateways

Run the gateway in its own processes, as Orleans clients. Connections and room logic then scale
separately, and restarting a silo does not drop anyone's WebSocket.

The silo hosts (no MQTT endpoint):

```csharp
builder.UseOrleans(silo =>
{
    silo.UseAzureStorageClustering(...);
    silo.AddGrainWire();
});
```

The gateway hosts:

```csharp
builder.UseOrleansClient(client =>
{
    client.UseAzureStorageClustering(...);
    client.AddGrainWireClient();
});
builder.Services.AddGrainWireGateway();

var app = builder.Build();
app.MapGrainWire();
app.Run();
```

The gateway processes do not need your room classes. They only pass messages along.

## What happens when a process stops

| What stops | Effect |
|---|---|
| A gateway (or a silo that contains one) | Its clients reconnect to another gateway and get a snapshot. Rooms show them as "reconnecting" in the meantime. |
| A silo, persistent rooms | The rooms are re-activated on another silo from their last save. Gateways re-attach their connections within one lease interval (10 s by default) and clients get a snapshot, without reconnecting. |
| A silo, in-memory rooms | Those rooms are lost. Their clients are told. |

Gateways renew a lease with every room they hold connections in, every 10 seconds
(`LeaseRenewInterval`). A room treats a gateway it has not heard from for 25 seconds
(`RoomOptions.LeaseTimeout`) as gone. Keep the renew interval well under the timeout.

## Saving rooms

Give a room type a storage provider to make it persistent. Any Orleans grain storage provider
works:

```csharp
silo.AddAzureBlobGrainStorage("games", ...);
silo.AddGrainWire(gw => gw.ConfigureRoom("chess", room => room.StorageProvider = "games"));
```

A persistent room is written after every successful command, so the provider's write latency is
added to every command. See [Rooms](rooms.md#saving).

## Behind a proxy

- Allow WebSocket upgrades on the gateway path (`/mqtt` by default; change it with
  `options.Path`).
- Raise idle timeouts above the MQTT keep-alive (60 seconds in the browser client), or
  connections that are merely quiet will be cut.
- Terminate TLS at the proxy or in Kestrel. Clients then connect with `wss://`.
