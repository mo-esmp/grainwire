# GrainWire chat sample

A small chat app with private chats and groups: a database, a Web API, real sign-in and a Blazor client.
Chess shows GrainWire on its own. This sample shows GrainWire inside an ordinary ASP.NET app that already has
a database and an API.

The database holds the data. GrainWire rooms carry only what happens live.

- [`Chat.Server/Data/ChatStore.cs`](Chat.Server/Data/ChatStore.cs): the database rules. Users, chats, members,
  messages, who sees which messages, unread counts.
- [`Chat.Server/Rooms/ChatRoom.cs`](Chat.Server/Rooms/ChatRoom.cs): one room per open chat. It checks the
  database before anyone joins, and its `send` command saves a message and then broadcasts it.
- [`Chat.Server/Rooms/InboxRoom.cs`](Chat.Server/Rooms/InboxRoom.cs): one room per user. It tells the user about
  things that happen in chats they do not have open.
- [`Chat.Server/Api/ChatEndpoints.cs`](Chat.Server/Api/ChatEndpoints.cs): the Web API for everything that does
  not need a live connection: search, start a chat, add a member, leave, list chats, load history, mark read.
- [`Chat.BlazorClient/Chat/ChatSession.cs`](Chat.BlazorClient/Chat/ChatSession.cs): the open chat in the Blazor
  client, written against `GrainWire.Client` and the API.

Why the data is not in the rooms:

- **Membership is presence.** A room member is removed when its grace period runs out after the last
  connection drops. Being in a group has to last for days, so membership lives in the database. A room's
  member list only means "has this chat open right now".
- **A connection can be in 20 rooms at most.** The client joins only its inbox and the chat on screen, not all
  its chats.
- **The inbox.** Because the client is in only a few rooms, each user has an inbox room. A new message in a
  chat that is not open, or being added to a group, arrives there.

Everything else (connections, ordering, reconnecting, command retries) is GrainWire. The sign-in is a JWT that
both the API and the GrainWire connection accept.

## Run it

You need the .NET 10 SDK. There is nothing else to set up: the database is SQLite, created on first start.

Run the commands from the repository root. Start the server:

```bash
dotnet run --project samples/Chat/Chat.Server
```

It listens on <http://localhost:5300> and keeps its data in `chat.db` next to the server project (git ignores
it). The development signing key for the JWT is in
[`appsettings.Development.json`](Chat.Server/appsettings.Development.json).

In a second terminal, start the client:

```bash
dotnet run --project samples/Chat/Chat.BlazorClient
```

Open <http://localhost:5302> and sign up. Then open it again as a second user. The sign-in is kept in local
storage, which is shared by every tab of one browser, so use a second browser or a private window for the
second user.

The server address is set in [`Chat.BlazorClient/wwwroot/appsettings.json`](Chat.BlazorClient/wwwroot/appsettings.json).

## Things to try

- **Chat live.** Sign up two users in two browsers. Find the other by name and start a private chat. Messages
  arrive as they are sent.
- **Unread counts.** Send a message while the other user has a different chat open or none. A badge appears in
  their list. It clears when they open the chat.
- **Add someone to a group.** Create a group with one other user, then add a third user. The group appears in
  their list at once. They see only the messages sent after they were added.
- **Leave a group.** The group disappears from your list, and the others' member list updates.
- **Restart the server.** Stop it and start it again. Both clients show "Connection lost. Reconnecting…", come
  back by themselves, the history is still there, and sending works.
- **Go offline.** Turn off one browser's network (DevTools → Network → Offline). The others keep working. Turn
  it back on: the chat catches up by itself, with no reload.

## What the sample leaves out

- **Migrations.** The schema is created at startup with `EnsureCreated`. A real app uses EF Core migrations.
- **A real key store.** The JWT signing key sits in `appsettings.Development.json`. A real app keeps it in a
  secret store, and the server refuses to start without a key outside development.
- **Token expiry and refresh.** The sign-in token lasts 7 days and is not renewed. When the server refuses it at
  connect time the client signs the user out. A connection that is already open and later loses the token keeps
  trying to reconnect with it, and nothing signs the user out until the next call to the API.
- **Private inboxes.** Whether a room exists, such as whether a user's inbox room is running (so whether they
  were online recently), can be seen by anyone who can name the room, and inbox ids are usernames. A real app
  would use unguessable inbox ids.
- **Sign-up limits and password reset.** Anyone can sign up as often as they like, and a forgotten password
  cannot be recovered.
- **Message history for people who were out.** The server has no per-user copy. Someone who leaves a group and
  is added again sees only what is said after they come back, not what they saw before. A phone app would keep
  the old messages on the device.
- **Group admins and more.** There are no admin roles, removing other members, renaming groups, invitations to
  accept, typing or read indicators, editing or deleting messages, attachments, or push notifications.
- **Browser tests for the client.** The server is covered by tests in `tests/GrainWire.Tests`:
  [`ChatStoreTests.cs`](../../tests/GrainWire.Tests/ChatStoreTests.cs),
  [`ChatAuthTests.cs`](../../tests/GrainWire.Tests/ChatAuthTests.cs) and
  [`ChatServerTests.cs`](../../tests/GrainWire.Tests/ChatServerTests.cs).
