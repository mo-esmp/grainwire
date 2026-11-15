# Security

## Before you go live

- [ ] **Replace the development authenticator.** It trusts any user name. See below.
- [ ] **Serve over TLS** (`wss://`), so tokens and messages are not readable on the network.
- [ ] **Use unguessable room ids** for private rooms (128 random bits, like the chess sample).
      Anyone who knows a room's id can ask to join it.
- [ ] **Decide who may create rooms and who may join** in `OnCreate`, `OnJoin` and `OnRejoin`.
      By default every authenticated player may do both.
- [ ] **Return only what a player may see** from `GetSnapshot(viewer)`, and use `SendTo` for
      private events.
- [ ] **Check the limits** below against your app.
- [ ] **Validate payloads in your handlers.** GrainWire checks who is sending; what they send
      is your app's business.

## What GrainWire enforces

- **Identity comes from the connection.** The `PlayerId` a handler receives was set when the
  connection was authenticated. Nothing in a message can change it.
- **A connection can subscribe only to its own inbox.** Wildcards, shared subscriptions and
  other players' inboxes are refused.
- **Clients cannot publish to each other.** A client may publish only commands to rooms. They
  go to the room grain and are never routed to another client. Retained messages are refused. A
  Will message is treated like any other publish, so it cannot reach another client either.
- **The room is the authority.** It re-checks membership, role and room identity on every
  command. A kicked player's commands are refused even if a gateway has not caught up yet.
- **Client ids are not shared.** A client id names an inbox, so the server never sends one
  player's client id to another.

## Authentication

Implement `IConnectionAuthenticator` and register it. It receives what the client sent when
connecting (`token` arrives in the MQTT password field) and returns the player's identity.

A JWT example, using `Microsoft.IdentityModel.JsonWebTokens`:

```csharp
public sealed class JwtAuthenticator(TokenValidationParameters parameters) : IConnectionAuthenticator
{
    private readonly JsonWebTokenHandler _handler = new();

    public async ValueTask<AuthenticationResult> AuthenticateAsync(ConnectionAuthContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.Token)) return AuthenticationResult.Fail("A token is required.");

        var result = await _handler.ValidateTokenAsync(context.Token, parameters);
        if (!result.IsValid) return AuthenticationResult.Fail("The token is not valid.");

        var subject = result.ClaimsIdentity.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject)) return AuthenticationResult.Fail("The token has no subject.");

        // Claims are passed to OnCreate and OnJoin, so rooms can make decisions based on them.
        var claims = result.ClaimsIdentity.Claims
            .GroupBy(claim => claim.Type)
            .ToDictionary(group => group.Key, group => group.First().Value);
        return AuthenticationResult.Success(new PlayerId(subject), claims);
    }
}
```

```csharp
builder.Services.AddSingleton(new TokenValidationParameters
{
    ValidIssuer = "https://login.example.com",
    ValidAudience = "my-game",
    IssuerSigningKey = signingKey,
});
builder.Services.AddSingleton<IConnectionAuthenticator, JwtAuthenticator>();
builder.Services.AddGrainWireGateway();
```

In the browser, pass a function so that a fresh token is used on every reconnect:

```ts
const client = await connect({ url, token: () => getAccessToken() });
```

A token is checked when the connection is made. A connection that is already open stays open
after its token expires; close connections yourself if you need to cut someone off sooner
(kick them from their rooms, and refuse the next connect).

### The development authenticator

If you register no authenticator, `DevelopmentAuthenticator` is used: the MQTT user name becomes
the player id, with no check. It logs a warning, and it refuses to start in the `Production`
environment unless you set `AllowDevelopmentAuthenticatorInProduction`. Do not set it.

## Limits

Set on `AddGrainWireGateway(options => ...)`. The defaults suit small games and chat; they held
up in the [benchmarks](benchmarks.md).

| Option | Default | When it is exceeded |
|---|---|---|
| `MaxPayloadBytes` | 64 KB | The publish is refused. |
| `MaxConnectionsPerPlayer` | 5 | The new connection is refused. |
| `MaxRoomsPerConnection` | 20 | The join is rejected (`too-many-rooms`). |
| `CommandsPerSecond` / `CommandBurst` | 20 / 40 | The command is rejected (`rate-limited`). |
| `MaxPendingRequests` | 32 | The request is rejected (`too-many-requests`). |
| `MaxOutgoingQueue` | 1000 messages | The slow connection is disconnected. It reconnects and gets a snapshot. |
| `RoomCreationsPerMinute` | 10 per player | Creation is rejected (`rate-limited`). Joining existing rooms still works. |
| `RoomOptions.MaxMembers` | 1000 | The join is rejected (`room-full`). |

These limits are per gateway process. A player connected to several gateways gets the allowance
on each. If you need a hard global limit, enforce it in `OnCreate` or `OnJoin`.

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's "Report a vulnerability" button on the
repository's Security tab.
