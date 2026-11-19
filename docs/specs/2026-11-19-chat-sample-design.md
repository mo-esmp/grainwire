# Chat sample: design

Date: 2026-11-19
Status: complete, waiting for the user's approval

## Intent

A second GrainWire sample next to Chess: a small chat app with private and group messaging and a Blazor
WebAssembly client. Chess shows GrainWire on its own. Chat shows GrainWire inside a **realistic app**: a
database that holds the data, a Web API for everything that does not need a live connection, real sign-in, and
GrainWire rooms that carry only what happens live.

Someone reading it should come away knowing how to fit GrainWire into an ordinary ASP.NET app that already has
a database and an API, and in particular:

- how a room checks the app's own database to decide who may join;
- how a client stays in a few rooms (its inbox and the open chat) instead of all of them;
- how rooms and the API tell a user about things that happen elsewhere (a new message, being added to a group);
- how a JWT from the app's own sign-in is used for both the API and the GrainWire connection.

It should stay about as small as Chess: a reader can go through the server code in one sitting.

## Goals

- Users sign up and sign in with a username and password.
- Users find each other by username (prefix search).
- A user can start a private chat with another user. There is at most one private chat per pair of users.
- A user can create a group, give it a name, and add other users by username. Any member can add more members.
  Any member can leave.
- Messages arrive live in an open chat. Older messages load a page at a time.
- The chat list shows every chat of the user, newest activity first, with the last message and an unread count.
  It updates live: a new message in another chat, or being added to a group, shows up without a reload.
- Everything is kept in a database and survives a server restart.
- It runs with `dotnet run`, with no Docker and no other setup, like Chess.

## Non-goals

- A TypeScript client. The client is Blazor only.
- Group admin roles, removing other members, renaming groups, group avatars.
- Accepting or declining invitations. Being added to a group works like WhatsApp: you are in, and you are told.
- Online/last-seen indicators, typing indicators, read receipts shown to others.
- Editing or deleting messages, attachments, reactions, replies, threads.
- Push notifications, email, password reset, changing usernames.
- Database migrations. The schema is created at startup with `EnsureCreated` (see "What the sample leaves out").
- Browser end-to-end tests for the Blazor client. (Server tests are in scope: see "Tests".)

## Anti-goals

The sample fails, even if it works, if:

- it grows into a chat product that is too big to read, hiding GrainWire under app code;
- the rooms become a second copy of the data: state that can disagree with the database;
- a client can read or post in a chat it is not a member of, by guessing ids or by calling a room directly;
- a message can appear live and then be missing after a reload (published before it was saved).

## Constraints from GrainWire

- **Room membership is presence.** A GrainWire member is removed when its grace period runs out after the last
  connection drops. Being in a group must last for days, so chat membership lives in the database, and a room's
  member list only means "has this chat open right now".
- **A connection may be in at most 20 rooms** (`MaxRoomsPerConnection`). The client joins only its inbox room and
  the chat on screen, never all its chats.
- **Rooms without a storage provider live in memory** and cease when their last member is gone. Both room types
  here are in memory: the database holds everything worth keeping.
- **`GetSnapshot` is synchronous.** It cannot query the database. This is one reason snapshots carry little and
  the client loads data through the API.
- A room grain's constructor can take services from DI (it derives from `Grain`, and `RoomGrain`'s constructor
  takes no arguments).
- **An in-memory room that ended does not come back by itself.** When a client reconnects it rejoins with
  `create: false` (`src/GrainWire.Client/Room.cs:394`). If the room ceased meanwhile (its last member's grace
  period ran out, or the server restarted), the join gets `room-not-found` and the client closes the room as
  `Lost`, with no snapshot. Both client classes must handle `Lost` by joining again with `Create = true`
  (unlike Chess, where `Lost` ends the game).
- **Calling a grain method on a room that is not running activates an empty one.** `ExecuteAsync` then runs
  against a blank `State` and commits (`RoomGrain.cs:329-359`); a derived room cannot see whether the room
  really exists. So each room keeps its own `Loaded` flag, set in `OnCreate`. A grain method (`Notify`,
  `MemberAdded`, `MemberLeft`) on a room that is not loaded does nothing and calls `DeactivateOnIdle()`, and
  `OnCreate` replaces `State` entirely rather than adding to it.
- **Room creation is rate-limited to 10 per player per minute** (`RoomCreationsPerMinute`). Here every open of a
  chat nobody else has open is a creation, and so are the inbox join at sign-in and every rejoin after `Lost`.
  The server raises the limit to 60 in `AddGrainWireGateway` (my call), with a comment explaining why.
- **Command dedup covers only the client's own automatic retries** (every 3 s, up to the 15 s request timeout),
  and is forgotten when a member is removed or the room ends. So after `OutcomeUnknownException` the client does
  not re-send: it reloads the newest page of messages and lets the user decide.

## Approaches considered

**A. The database holds everything; rooms carry only live updates. (Chosen.)** SQLite through EF Core holds
users, chats, memberships and messages. The Web API does sign-up, sign-in, search, starting chats, adding
members, leaving, listing chats, loading history and marking read. A chat room checks the database when someone
joins, and its `send` command saves the message and then broadcasts it. Each user has an inbox room that tells
them about activity elsewhere. This is how real chat backends are built, it keeps the rooms small, and it costs
a database write per message.

**B. Grains hold everything with Orleans storage; the database holds only users.** This shows GrainWire's
saving more, but searching and paging history across grains is awkward, and Orleans has no SQLite storage
provider, so it would need PostgreSQL or SQL Server and Docker. Not chosen.

Also decided with the user:

- **Real sign-in**: username and password; the server issues a JWT that the API and the GrainWire connection
  both accept.
- **SQLite**, so it runs without setup. Switching to PostgreSQL is a change of EF Core provider.
- **One server project** that hosts the API, the Orleans silo and the MQTT endpoint, like `Chess.Server`.

## Layout

```
samples/Chat/
  README.md
  Chat.Server/          ASP.NET Core: Web API + Orleans silo + GrainWire gateway + SQLite
  Chat.BlazorClient/    Blazor WebAssembly, using GrainWire.Client and HttpClient
```

Both projects are added to `GrainWire.slnx` under `/samples/Chat/` (note: another session currently has an
uncommitted change to `GrainWire.slnx`; the builder adds the Chat entries without disturbing it), and the root `README.md` links the sample
next to Chess. Namespaces follow Chess: `ChatSample.Server`, `ChatSample.BlazorClient` (my call; Chess uses
`ChessFromScratch.*`).

Ports (my call): server `http://localhost:5300`, Blazor client `http://localhost:5302`. The server allows CORS
from the client's origin, configured in `appsettings.json`.

## The data

EF Core with SQLite, one `ChatDb` context, file `chat.db` next to the server (path in configuration).
The schema is created with `Database.EnsureCreated()` at startup.

| Table | Columns |
|---|---|
| `Users` | `Username` (key, lowercase), `PasswordHash`, `CreatedAt` |
| `Chats` | `Id` (string, 32 hex chars, a GUID in `N` format; valid as a GrainWire room id), `Kind` (`direct` or `group`), `Title` (groups only), `DirectKey` (direct only: the two usernames sorted and joined with `:`, unique), `CreatedAt`, `LastMessageAt` |
| `Members` | `ChatId`, `Username` (together the key), `JoinedAt`, `FirstMessageId`, `LastReadMessageId` (see "Who sees which messages") |
| `Messages` | `Id` (long, auto-increment), `ChatId`, `Sender`, `Text`, `SentAt`; index on (`ChatId`, `Id`) |

Rules:

- Usernames are 3–24 characters of `a-z 0-9 _ . -`, stored and compared in lowercase. They are the user's
  identity everywhere, including as the GrainWire `PlayerId`. They never change.
- Passwords: at least 8 characters, hashed with `PasswordHasher<T>` from `Microsoft.Extensions.Identity.Core`.
- Message text: 1–2,000 characters after trimming.
- A group has 2–100 members (my call; well under GrainWire's 1,000-member room limit). Its title is 1–60 characters.
- When the last member leaves a group, the group and its messages stay in the database (my call: simplest, and
  nothing can reach them).

## Sign-in

- `POST /api/auth/register { username, password }` creates the user and returns `{ token, username }`.
  A taken username gives 409.
- `POST /api/auth/login { username, password }` returns `{ token, username }`, or 401.
- The token is a JWT signed with HMAC-SHA256: `sub` = username, issuer and audience from configuration,
  valid for 7 days. The signing key is in `appsettings.Development.json` with a comment that a real app keeps it
  in a secret store; startup fails outside Development if no key is configured.
- The API uses `Microsoft.AspNetCore.Authentication.JwtBearer`. GrainWire uses a `JwtAuthenticator`
  (`IConnectionAuthenticator`) built from the same `TokenValidationParameters`, as in `docs/security.md`. The
  development authenticator is not used.
- The Blazor client keeps the token in local storage and passes it to `GrainWireClient` through
  `AccessTokenProvider`, so a reconnect uses the stored token.

## The Web API

All under `/api`, all except sign-in require the bearer token. Minimal APIs, grouped in a few files.

| Endpoint | Does |
|---|---|
| `GET /api/users?search=al` | Up to 20 usernames starting with the text, excluding the caller. |
| `GET /api/chats` | The caller's chats, newest activity first: id, kind, title (for a direct chat, the other user's name), members, last message (sender, text, time), unread count. |
| `POST /api/chats/direct { username }` | Returns the private chat with that user, creating it if there is none. 404 if the user does not exist; 400 for yourself. |
| `POST /api/chats/group { title, usernames[] }` | Creates a group with the caller and those users. Unknown usernames give 400 and nothing is created. |
| `POST /api/chats/{id}/members { username }` | Adds a user to a group. Caller must be a member. Adding someone already in it does nothing. |
| `DELETE /api/chats/{id}/members/me` | Leaves a group. |
| `GET /api/chats/{id}/messages?before={messageId}` | Up to 50 messages older than `before` (or the newest 50), oldest first. Caller must be a member. |
| `POST /api/chats/{id}/read { messageId }` | Moves the caller's read marker forward (never back). |

A chat that is not found and a chat the caller is not in both give 404, so ids cannot be probed.

Sending a message is **not** in the API: it goes through the chat room, so it is published only after it is
saved, and the client's automatic retries of a send are not saved twice (GrainWire's command dedup; see the
limits of this under "Constraints").

## The rooms

### `inbox` room (`InboxGrain`, room id = username)

One per user. It holds no data; it is how a user hears about things that happen in chats they do not have open.

- `OnCreate` and `OnJoin` accept only when the player is the user the inbox belongs to (`player == RoomId`).
- It exposes a grain method (my call: `IInboxGrain.Notify(InboxEvent)`, run inside `ExecuteAsync`) that
  broadcasts one event to the user's connections. If the user is offline the event goes nowhere, which is fine:
  the chat list is loaded from the API on sign-in.
- Events:
  - `activity { chatId, messageId, sender, text, sentAt }`: a new message in one of the user's chats.
  - `added { chatId }`: the user was added to a group or someone started a private chat with them.
  - `removed { chatId }`: the user left a group (so other tabs drop it).
- The snapshot is empty. The client treats every snapshot (after joining, reconnecting or catching up) as
  "reload the chat list from the API".

### `chat` room (`ChatRoomGrain`, room id = chat id)

One per chat, alive while someone has it open.

- `OnCreate` loads the chat's kind, title and member list from the database into `State`, and denies creation
  when the chat does not exist or the creator is not a member. `OnJoin` and `OnRejoin` accept only database
  members. (`MemberLeft` kicks the user, so their next join goes through `OnJoin`; `OnRejoin` covers the case
  where that kick never happened because the API's call to the room failed.)
- `send { text }` (command): validates the text, checks in the database that the sender is still a member
  (my call: one indexed query per message, so a failed `MemberLeft` call can never let someone keep posting;
  rejected with code `not-member`), saves the message, updates `Chats.LastMessageAt`, moves the
  sender's read marker to it, broadcasts `message { id, sender, text, sentAt }`, and returns the saved message.
  Then it notifies every other member's inbox with `activity`.
- Grain methods called by the API, run inside `ExecuteAsync`:
  - `MemberAdded(username)`: adds to `State.Members`, broadcasts `member-added { username }`.
  - `MemberLeft(username)`: removes from `State.Members`, broadcasts `member-left { username }`, and kicks that
    user from the room if they have it open.
- The snapshot is `{ chatId, kind, title, members }`. The client loads messages from the API: the newest page
  after every snapshot, older pages when the user scrolls up.

### How the pieces call each other

- API → chat room (`MemberAdded`, `MemberLeft`) and API → inbox (`Notify`), after the database change is saved.
- Chat room → inbox (`Notify`) after a message is saved.
- Inboxes never call anything, so there are no call cycles between grains.
- Calls to inboxes do not wait for an answer (my call: mark `Notify` `[OneWay]`), so a slow or busy inbox does not
  hold up a send. GrainWire's validator allows `[OneWay]`; it rejects only `[Reentrant]`, `[MayInterleave]`,
  `[AlwaysInterleave]` and `[ReadOnly]` (`src/GrainWire.Abstractions/Internal/RoomTypeInfo.cs:120-139`).
- The database write in `send` happens inside the handler. If it fails the handler throws, nothing is broadcast,
  and the client gets `handler-error`. Because the room has no storage provider, nothing can fail after the
  handler returns, so a saved message is always published.
- When the API changes the database and the call to a grain then fails, the database change stands; open clients
  catch up on their next snapshot or reload. The API logs the failure and still returns success.

### Server setup

Like `Chess.Server/Program.cs`: localhost clustering, `silo.AddGrainWire()` (room types are found as ordinary
Orleans grains; default options), `AddGrainWireGateway` with `RoomCreationsPerMinute = 60`, `MapGrainWire`,
plus EF Core, JWT bearer auth, CORS and the API endpoints. Grains get `IDbContextFactory<ChatDb>` from DI.
Orleans ports are configurable as in Chess, with defaults that differ from Chess's so both samples can run at
once (my call: silo 11112, gateway 30001).

## Tests

Chess's server is covered in `tests/GrainWire.Tests/ChessSampleTests.cs`, and CI has a `chess-sample` job. Chat
follows the same pattern (my call):

- `tests/GrainWire.Tests/ChatSampleTests.cs`, with `GrainWire.Tests.csproj` referencing `Chat.Server`. It covers
  the rules the sample depends on: username and message validation, one private chat per pair, the
  404-for-non-members rule, the membership check on `send`, and the history rule in "Who sees which messages"
  (including a member who leaves and is added again). Whether these run through the API in memory (`WebApplicationFactory`) or against the classes
  directly is the builder's call.
- A `chat-sample` CI job that builds `Chat.Server` and `Chat.BlazorClient`. No Playwright.

## The Blazor client

Same shape as `Chess.BlazorClient`.

- `Services/Auth.cs`: sign up, sign in, sign out, the stored token and username.
- `Services/Api.cs`: a typed wrapper around `HttpClient` for the endpoints above, adding the bearer token. A 401
  signs the user out.
- `Services/ChatConnection.cs`: the one `GrainWireClient`, opened after sign-in, closed on sign-out
  (like `ChessConnection`).
- `Chat/Inbox.cs`: joins the inbox room first, then loads the chat list, and keeps it current from inbox events
  (moves a chat to the top, updates last message and unread count, adds or drops chats). Reloads on every
  snapshot, and joins again with `Create = true` when the room closes as `Lost`.
- `Chat/ChatSession.cs`: one open chat, like `GameSession`. Joins the chat room (with `Create = true`), loads the
  newest page on every snapshot, appends `message` events (merging by message id, so a message seen both live
  and in a page appears once), loads older pages on demand, sends, and marks read. It joins again with
  `Create = true` when the room closes as `Lost`, and closes the chat (back to the list) when it is kicked or the
  join is rejected because the user is no longer a member.
- Pages:
  - `/login`: sign in or sign up.
  - `/`: two columns. Left: chat list, a "new chat" search box (type a name, pick a user, opens the private
    chat) and a "new group" button. Right: the open chat (`/c/{id}`) with messages, a send box, and for groups
    the member list with "add member" and "leave group".
  - A small dialog for creating a group: title plus usernames picked from search.
- Connection state shows as a line at the top ("Reconnecting…"), like Chess.
- Server address in `wwwroot/appsettings.json` (`Chat:ServerUrl` for the API, `Chat:MqttUrl` for GrainWire).

Rough sketch of the main page:

```
+------------------------+-----------------------------------------+
| [search users...]      |  # weekend-trip        alice, bob, you  |
| [+ New group]          |                       [+ Add] [Leave]   |
|------------------------|-----------------------------------------|
| weekend-trip       (2) |  alice: who's driving?                  |
|   bob: I can drive     |  bob:   I can drive                     |
| alice                  |                                         |
|   see you then         |                                         |
|                        |  [type a message...............] [Send] |
+------------------------+-----------------------------------------+
```

## README

`samples/Chat/README.md` in the same voice as the Chess one: what each part is, how to run it (server, then
client), "Things to try" (two browsers chatting; a third user added to a group sees it appear; go offline and
back; restart the server and the history is still there), and "What the sample leaves out": migrations, a
production key store, rate limits on sign-up, password reset, and the non-goals above.

## Who sees which messages

Decided by the user: **a member sees only messages sent after they were added** (the WhatsApp rule).

- `Members` gets one more column, `FirstMessageId`: the id of the newest message in the chat at the moment the
  member was added (0 if none). The member sees messages with `Id > FirstMessageId`. An id is used, not
  `JoinedAt`, so two events in the same millisecond cannot be ambiguous.
- The people a group is created with, and both users of a private chat, start at 0 and see everything.
- `LastReadMessageId` starts at `FirstMessageId`, so earlier messages never count as unread.
- The rule applies everywhere a member reads messages: the history endpoint, the last message and the unread
  count in `GET /api/chats`, and `activity` notifications (which only go to current members anyway).
- Leaving deletes the `Members` row. Someone added again gets a new row with a new `FirstMessageId`, so they do not
  see what was said while they were out, and no longer see what they saw before they left either. (The server has no
  per-user copy; a phone app would keep the old messages locally. The README says so.)
- In the open chat room, a newly added member receives only `message` events that are broadcast after they join, so
  live delivery follows the same rule without extra code.

## Left to the builder

- File and class names inside each project, beyond those named here.
- Styling, kept plain like Chess.
- How the client shows errors (a short notice, like `GameSession.Flash`).
- EF Core and JWT package versions, added to `Directory.Packages.props`.
