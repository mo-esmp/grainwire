# Getting started

Build a small chat room: a server with one room class, and a web page that talks to it.
It takes about fifteen minutes. You need the .NET 10 SDK.

> GrainWire is in preview and its packages are not on NuGet or npm yet. Until they are, build
> them from this repository: `dotnet pack -c Release -o artifacts`, and `npm run build && npm pack`
> in `clients/ts`. The code below was run against packages built that way.

## 1. Create the server

```bash
dotnet new web -n MyRooms
```

```bash
cd MyRooms
```

```bash
dotnet add package GrainWire.Server --prerelease
```

`GrainWire.Server` brings in the room base class and Orleans.

## 2. Write a room

Replace `Program.cs` with:

```csharp
using GrainWire;
using GrainWire.Server;

var builder = WebApplication.CreateBuilder(args);

builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddGrainWire();
});
builder.Services.AddGrainWireGateway();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGrainWire();
app.Run();

[GenerateSerializer]
public class ChatState
{
    [Id(0)] public List<ChatLine> Lines { get; set; } = new();
}

[GenerateSerializer]
public record ChatLine([property: Id(0)] string From, [property: Id(1)] string Text);

public record SayRequest(string Text);

[RoomType("chat")]
public class ChatRoom : RoomGrain<ChatState>
{
    [OnMessage("say")]
    public void Say(PlayerId player, SayRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) Reject("Say something first.");

        var line = new ChatLine(player.Value, request.Text.Trim());
        State.Lines.Add(line);
        if (State.Lines.Count > 100) State.Lines.RemoveAt(0);   // the snapshot carries the last 100 lines

        Broadcast("said", line);
    }
}
```

What is in there:

- `[RoomType("chat")]` names the room type. Every chat room is one `ChatRoom` grain, and its
  `State` belongs to that room alone.
- `[OnMessage("say")]` handles the `say` command. `player` comes from the connection, never
  from the message, so a client cannot pretend to be someone else.
- `Reject(...)` ends the command. Nothing the handler did is kept, and nothing is sent.
- `Broadcast(...)` is held back until the command has finished successfully. Then every member
  gets the event, in order.
- The state must be marked `[GenerateSerializer]` with an `[Id]` on each member. That is what
  lets GrainWire copy it (to undo a failed command) and save it.

## 3. Write the page

Create `wwwroot/index.html`:

```html
<!doctype html>
<meta charset="utf-8" />
<title>Chat</title>
<ul id="lines"></ul>
<form id="form"><input id="text" autocomplete="off" autofocus /> <button>Say</button></form>
<p id="status"></p>

<script type="module">
  import { connect, RejectedError } from "https://esm.sh/@grainwire/client";

  const name = prompt("Your name?") || "guest";
  const client = await connect({ url: `ws://${location.host}/mqtt`, username: name });
  const room = await client.join("chat", "lobby", { create: true });

  const lines = document.querySelector("#lines");
  const show = (line) => (lines.appendChild(document.createElement("li")).textContent = `${line.from}: ${line.text}`);

  // A snapshot arrives after joining, after a reconnect, and whenever the page had to catch up.
  room.onSnapshot((view) => {
    lines.replaceChildren();
    view.lines.forEach(show);
  });
  room.on("said", show);
  room.onPresence((p) => (document.querySelector("#status").textContent = `${p.player} is ${p.status}`));

  document.querySelector("#form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const input = document.querySelector("#text");
    try {
      await room.request("say", { text: input.value });
      input.value = "";
    } catch (error) {
      alert(error instanceof RejectedError ? error.reason : error.message);
    }
  });
</script>
```

In a real project, install the client with `npm install @grainwire/client` and import it through
your bundler.

## 4. Run it

```bash
dotnet run
```

Open the address it prints in two browser windows, with two different names, and chat.

Things to try:

- **Send an empty message.** The room rejects it and tells only you.
- **Open a third window.** It gets the earlier lines right away, from the snapshot.
- **Go offline** in one window (DevTools → Network → Offline). The others see
  "… is reconnecting". Go back online: the page catches up by itself.

## What you did not have to write

Topics, subscriptions, message ordering, who is online, reconnecting, catching up after a lost
message, retrying a request safely, and undoing a failed command. That is what GrainWire is for.

## Next

- [Rooms](rooms.md): hooks, roles, private events, kicking, deadlines, saving.
- [Guarantees](guarantees.md): exactly what is promised about ordering and delivery.
- [Security](security.md): replace the development authenticator before going live.
- [Hosting](hosting.md): one process, a cluster, or separate gateways.
- [The chess sample](../samples/Chess/README.md): a complete small game.
