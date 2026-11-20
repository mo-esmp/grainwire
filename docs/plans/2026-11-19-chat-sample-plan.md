# Chat Sample Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `samples/Chat`: a small but realistic chat app (sign-in, user search, private chats, groups) with a
SQLite database, a Web API, GrainWire rooms for live updates, and a Blazor WebAssembly client.

**Architecture:** The database is the source of truth. All rules live in one `ChatStore` class over EF Core, used by
the minimal-API endpoints and by two in-memory room types: `inbox` (one per user, carries notices about other chats)
and `chat` (one per chat, checks membership in the database, saves then broadcasts each message). A JWT from the
app's own sign-in authenticates both the API and the GrainWire connection.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, Orleans 10, GrainWire, EF Core 10 + SQLite,
`Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.Extensions.Identity.Core` (`PasswordHasher<T>`),
Blazor WebAssembly + `GrainWire.Client`, xUnit.

**Spec:** [`docs/specs/2026-11-19-chat-sample-design.md`](../specs/2026-11-19-chat-sample-design.md). Read it first;
this plan argues from it.

## Global Constraints

- Repo build settings apply: `net10.0`, nullable on, **warnings are errors**, central package versions in
  `Directory.Packages.props`. New packages: `Microsoft.EntityFrameworkCore.Sqlite`,
  `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.Extensions.Identity.Core`, all at `10.0.15`
  (the version the repo already uses for ASP.NET packages; if a package has no 10.0.15, use its latest 10.0.x).
- Projects: `samples/Chat/Chat.Server` (`Microsoft.NET.Sdk.Web`, `RootNamespace`/`AssemblyName` `ChatSample.Server`)
  and `samples/Chat/Chat.BlazorClient` (`ChatSample.BlazorClient`). Copy the csproj shape of the Chess projects,
  including `<NoWarn>$(NoWarn);CS1591</NoWarn>`.
- Ports: server `http://localhost:5300`, client `http://localhost:5302`, Orleans silo `11112`, gateway `30001`.
- Usernames: 3–24 chars of `a-z 0-9 _ . -`, trimmed and lowercased. Passwords ≥ 8 chars. Message text 1–2,000 chars
  after trim. Group title 1–60 chars after trim. Group size 2–100. History page 50. Search results ≤ 20.
- JWT: HMAC-SHA256, `sub` = username, issuer and audience `grainwire-chat-sample`, 7-day lifetime. Key from
  `Jwt:Key` (≥ 32 bytes). Startup throws if it is missing. Only `appsettings.Development.json` holds a key, with a
  comment that a real app keeps it in a secret store.
- Chat ids: `Guid.NewGuid().ToString("N")`. Direct key: the two usernames sorted ordinally, joined with `:`.
- A chat that does not exist and a chat the caller is not in both answer 404.
- Visibility: a member sees messages with `Id > FirstMessageId` (spec, "Who sees which messages").
- No DateTimeOffset in SQL `ORDER BY` or comparisons (SQLite provider cannot translate them). Order messages by
  `Id`, and sort the chat list in memory.
- Style: follow the Chess sample. Explicit types, XML summaries on public types and members in plain English, small
  files. Do not touch `samples/Chess/Chess.BridgeServer` or `samples/Chess/rules-python`, which belong to another
  session. In `GrainWire.slnx`, only add the Chat folder. Leave that session's line in place, and do not stage it
  with your change (stage the file with `git add -p`).

## Review Focus

1. **Uppercase and whitespace in usernames** (`" Alice "` at sign-up, `"ALICE"` in search or in "add member") must
   resolve to the same user `alice`. Tested in Task 2 (`Usernames_are_normalized_everywhere`).
2. **Starting a private chat from the other side, or with yourself.** Bob starting a chat with Alice after Alice
   started one with Bob returns the same chat. Starting a chat with yourself is a 400. Tested in Task 2.
3. **A message sent while the sender is being removed** must not be saved. Tested in Task 2
   (`AddMessage_refuses_a_non_member`) and Task 5 (`Leaving_closes_the_open_chat_and_stops_sending`).
4. **Sign-in with an unknown user or a wrong password** gets the same 401 and message, so names cannot be probed.
   Tested in Task 4.
5. **A request with a malformed id** (`/api/chats/../x`, a 500-char id, an id with `/`) gets a 404, not a 500.
   Tested in Task 4 (`Bad_chat_ids_are_not_found`).

---

## File map

```
Directory.Packages.props                     + 3 package versions
GrainWire.slnx                               + /samples/Chat/ folder with both projects
README.md                                    + link to the Chat sample next to Chess
tests/GrainWire.Tests/GrainWire.Tests.csproj + ProjectReference to Chat.Server
tests/GrainWire.Tests/ChatStoreTests.cs      rules, against ChatStore and a temp SQLite file
tests/GrainWire.Tests/ChatAuthTests.cs       tokens and the connection authenticator
tests/GrainWire.Tests/ChatServerTests.cs     the whole server in-process: HTTP + GrainWire.Client
tests/GrainWire.Tests/ChatServerFixture.cs   starts Chat.Server on free ports with a temp database

samples/Chat/README.md
samples/Chat/Chat.Server/
  Chat.Server.csproj, Program.cs, ChatServerHost.cs, appsettings.json, appsettings.Development.json,
  Properties/launchSettings.json
  Data/ChatDb.cs        DbContext + entities (User, Chat, Member, Message)
  Data/Rules.cs         validation, normalization, limits
  Data/ChatStore.cs     every query and rule; DTOs; ChatException
  Auth/JwtSettings.cs, Auth/TokenService.cs, Auth/JwtConnectionAuthenticator.cs
  Api/AuthEndpoints.cs, Api/ChatEndpoints.cs
  Rooms/InboxRoom.cs    IInboxRoom + InboxRoom + InboxNotice
  Rooms/ChatRoom.cs     IChatRoom + ChatRoom + ChatRoomState + ChatView
  Rooms/RoomNotifier.cs API/rooms -> grains, failures logged
samples/Chat/Chat.BlazorClient/
  Chat.BlazorClient.csproj, Program.cs, App.razor, _Imports.razor, Layout/MainLayout.razor,
  Properties/launchSettings.json, wwwroot/{index.html, appsettings.json, css/app.css}
  Services/Session.cs, Services/ChatApi.cs, Services/ChatConnection.cs
  Chat/Models.cs, Chat/Inbox.cs, Chat/ChatSession.cs
  Pages/Login.razor, Pages/Chats.razor, Pages/NotFound.razor
  Components/ChatList.razor, Components/UserSearch.razor, Components/NewGroupDialog.razor, Components/ChatPane.razor
```

---

### Task 1: Server project, data model and rules

**Files:**
- Modify: `Directory.Packages.props`, `GrainWire.slnx`, `tests/GrainWire.Tests/GrainWire.Tests.csproj`
- Create: `samples/Chat/Chat.Server/Chat.Server.csproj`, `Program.cs` (temporary: `WebApplication.CreateBuilder(args).Build().Run();`), `Data/ChatDb.cs`, `Data/Rules.cs`
- Test: `tests/GrainWire.Tests/ChatStoreTests.cs` (Rules part)

**Interfaces:**
- Produces:
  - `ChatSample.Server.Data.Rules` (static):
    `string? NormalizeUsername(string? raw)` (null when invalid),
    `string? CleanText(string? text)`, `string? CleanTitle(string? title)`,
    `bool IsValidPassword(string? password)`, `string DirectKey(string a, string b)`,
    `const int PageSize = 50, SearchLimit = 20, MaxGroupMembers = 100`.
  - `ChatDb : DbContext` with `DbSet<User> Users`, `DbSet<Chat> Chats`, `DbSet<Member> Members`,
    `DbSet<Message> Messages`. Entities as in the spec's table, plus `Member.FirstMessageId`. `Chat.LastMessageAt`
    is `DateTimeOffset` and is never used in SQL ordering. `Chat.DirectKey` has a unique index (nullable). `Member`
    has the key (`ChatId`, `Username`). `Message` has an index (`ChatId`, `Id`). `Chat.Kind` is a string,
    `"direct"` or `"group"` (constants `ChatKinds.Direct` and `ChatKinds.Group`).

- [ ] **Step 1: Add the packages, the project, the solution entry and the test reference.** Check that
  `dotnet build samples/Chat/Chat.Server` succeeds.
- [ ] **Step 2: Write the failing tests** in `ChatStoreTests.cs`:

```csharp
[Theory]
[InlineData(" Alice ", "alice")]
[InlineData("bob_1.x-y", "bob_1.x-y")]
[InlineData("ab", null)]
[InlineData("a-name-that-is-25-chars-x", null)]
[InlineData("bad name", null)]
[InlineData("élan", null)]
[InlineData(null, null)]
public void Usernames_are_normalized_or_refused(string? raw, string? expected) =>
    Assert.Equal(expected, Rules.NormalizeUsername(raw));

[Fact]
public void Text_title_and_password_limits()
{
    Assert.Equal("hi", Rules.CleanText("  hi \n"));
    Assert.Null(Rules.CleanText("   "));
    Assert.NotNull(Rules.CleanText(new string('x', 2000)));
    Assert.Null(Rules.CleanText(new string('x', 2001)));
    Assert.Null(Rules.CleanTitle(new string('x', 61)));
    Assert.False(Rules.IsValidPassword("1234567"));
    Assert.True(Rules.IsValidPassword("12345678"));
    Assert.Equal(Rules.DirectKey("bob", "alice"), Rules.DirectKey("alice", "bob"));
    Assert.Equal("alice:bob", Rules.DirectKey("bob", "alice"));
}
```

- [ ] **Step 3: Run and see them fail.** `dotnet test tests/GrainWire.Tests --filter "FullyQualifiedName~ChatStoreTests"`. Expected: compile failure, because `Rules` does not exist yet.
- [ ] **Step 4: Implement `Rules` and `ChatDb`.**
- [ ] **Step 5: Run the tests.** Same command. Expected: PASS.
- [ ] **Step 6: Commit.** Message: `Add the chat sample's data model and rules.` Stage `GrainWire.slnx` with `git add -p` (see Global Constraints).

### Task 2: ChatStore, where every rule lives

**Files:**
- Create: `samples/Chat/Chat.Server/Data/ChatStore.cs`
- Test: `tests/GrainWire.Tests/ChatStoreTests.cs`

**Interfaces:**
- Consumes: `ChatDb`, `Rules` (Task 1).
- Produces (namespace `ChatSample.Server.Data`):
  - `enum ChatError { NotFound, Invalid, Taken }` and `sealed class ChatException(ChatError error, string message) : Exception(message)` with `ChatError Error`.
  - `[GenerateSerializer] sealed record MessageDto([property: Id(0)] long Id, [property: Id(1)] string Sender, [property: Id(2)] string Text, [property: Id(3)] DateTimeOffset SentAt)`
  - `sealed record ChatSummary(string Id, string Kind, string Title, IReadOnlyList<string> Members, MessageDto? LastMessage, int Unread)`. For a direct chat, `Title` is the other user's name.
  - `sealed record ChatInfo(string Id, string Kind, string? Title, IReadOnlyList<string> Members)`
  - `sealed record SentMessage(MessageDto Message, IReadOnlyList<string> Recipients)`. The recipients are the other members.
  - `sealed class ChatStore(IDbContextFactory<ChatDb> dbs)` with:
    - `Task<bool> AddUserAsync(string username, string passwordHash, DateTimeOffset now)`. False when the name is taken.
    - `Task<string?> GetPasswordHashAsync(string username)`
    - `Task<IReadOnlyList<string>> SearchUsersAsync(string caller, string? prefix)`. Normalizes the prefix with lowercase and trim; an empty prefix returns `[]`. Excludes the caller, returns at most 20, ordered.
    - `Task<(ChatSummary Chat, bool Created)> GetOrCreateDirectAsync(string caller, string other, DateTimeOffset now)`. Throws `Invalid` for yourself and `NotFound` for an unknown user.
    - `Task<ChatSummary> CreateGroupAsync(string caller, string? title, IReadOnlyList<string> usernames, DateTimeOffset now)`. Throws `Invalid` for a bad title, an unknown user (the message names them), no other members, or more than 100 members. Creates nothing on error.
    - `Task<bool> AddMemberAsync(string caller, string chatId, string username, DateTimeOffset now)`. Returns true if newly added and false if already a member. Throws `NotFound` when the caller is not a member or the chat does not exist, and `Invalid` for a direct chat, an unknown user, or a full group. Sets `FirstMessageId` and `LastReadMessageId` to the chat's newest message id, or 0 when there is none.
    - `Task LeaveAsync(string caller, string chatId)`. Throws `NotFound` when the caller is not a member, and `Invalid` for a direct chat. Deletes the `Members` row.
    - `Task<IReadOnlyList<ChatSummary>> ListChatsAsync(string caller)`. Sorted in memory by the last message's `SentAt`, or the chat's `CreatedAt` when it has no messages, newest first. Unread counts messages with `Id > max(LastRead, First)` whose sender is not the caller. `LastMessage` is the newest message the caller can see.
    - `Task<IReadOnlyList<MessageDto>> GetMessagesAsync(string caller, string chatId, long? before)`. Throws `NotFound` when the caller is not a member. Returns up to 50 visible messages with `Id < before` (or the newest), oldest first.
    - `Task MarkReadAsync(string caller, string chatId, long messageId)`. Throws `NotFound` when the caller is not a member. Only moves the marker forward.
    - `Task<ChatInfo?> GetChatInfoAsync(string chatId)`
    - `Task<bool> IsMemberAsync(string chatId, string username)`
    - `Task<SentMessage?> AddMessageAsync(string chatId, string sender, string text, DateTimeOffset now)`. Null when the sender is not a member. In one `SaveChanges`, it inserts the message, sets `Chat.LastMessageAt`, and sets the sender's `LastReadMessageId` to the new id (two saves are fine if the id is needed first, but both are in one transaction).
  - Every method that takes a username normalizes it with `Rules.NormalizeUsername`. A name that does not normalize counts as an unknown user.

- [ ] **Step 1: Write the failing tests.** Use a helper `static ChatStore NewStore()`. It makes a `ChatDb` over `Data Source={tempfile}`, calls `EnsureCreated`, and returns a store over a small `IDbContextFactory<ChatDb>` test class. Seed users `alice`, `bob`, `carol` and `dave` with the hash `"x"`. Tests and their key assertions:

```csharp
Usernames_are_normalized_everywhere
    // AddUser("alice") then AddUser(" ALICE ") -> false; Search("bob","AL") == ["alice"];
    // AddMember(... "CAROL" ...) adds "carol"
Search_matches_prefixes_excludes_the_caller_and_stops_at_twenty
    // 25 users "user00".."user24": Search("alice","user").Count == 20; Search("alice","al") excludes alice -> []
One_private_chat_per_pair_from_either_side
    // a = GetOrCreateDirect(alice,bob) Created==true; b = GetOrCreateDirect(bob,"Alice") -> b.Chat.Id==a.Chat.Id, Created==false;
    // a.Chat.Title=="bob", b.Chat.Title=="alice"
Private_chat_with_yourself_or_a_stranger_is_refused
    // (alice,alice) -> ChatError.Invalid; (alice,"nobody") -> ChatError.NotFound
Group_with_an_unknown_user_creates_nothing
    // CreateGroup(alice,"trip",["bob","nobody"]) -> Invalid, message contains "nobody"; ListChats(alice) is empty
Group_needs_a_title_and_another_member
    // title "  " -> Invalid; usernames [] or ["alice"] -> Invalid
Non_members_get_not_found
    // group of alice+bob; carol: GetMessages, AddMember, Leave, MarkRead -> NotFound; random id -> NotFound
AddMessage_refuses_a_non_member
    // AddMessage(groupId,"carol","hi") == null; after Leave(bob): AddMessage(groupId,"bob","hi") == null
A_new_member_sees_only_later_messages
    // alice sends "before"; AddMember(alice,id,carol); alice sends "after";
    // GetMessages(carol) texts == ["after"]; ListChats(carol)[0].Unread == 1, LastMessage.Text == "after"
Leaving_and_being_added_again_starts_over
    // carol added, "one" sent, carol leaves, "two" sent, carol added again, "three" sent:
    // GetMessages(carol) texts == ["three"]
Unread_counts_skip_your_own_messages_and_move_only_forward
    // bob sends 3 to a direct chat; alice Unread==3; MarkRead(alice, id, second.Id) -> 1;
    // MarkRead(alice, id, first.Id) -> still 1; alice sends one -> alice Unread still 1
History_pages_back_fifty_at_a_time
    // 55 messages: GetMessages(null) -> 50, first.Text=="m5", last=="m54"; GetMessages(before: first.Id) -> 5 ("m0".."m4")
Chats_are_listed_newest_activity_first
    // chat X created, then Y; a message in X -> ListChats order [X, Y]
Adding_an_existing_member_does_nothing
    // AddMember(alice,id,bob) == false
```

- [ ] **Step 2: Run and see them fail.** `dotnet test tests/GrainWire.Tests --filter "FullyQualifiedName~ChatStoreTests"`. Expected: compile failure, because `ChatStore` does not exist yet.
- [ ] **Step 3: Implement `ChatStore`.** Each method creates its own `ChatDb` with `await using`. Check membership with one query on `Members`.
- [ ] **Step 4: Run the tests.** Same command. Expected: all PASS.
- [ ] **Step 5: Commit.** Message: `Add ChatStore: users, chats, members and messages.`

### Task 3: Sign-in: tokens and the GrainWire authenticator

**Files:**
- Create: `samples/Chat/Chat.Server/Auth/JwtSettings.cs`, `Auth/TokenService.cs`, `Auth/JwtConnectionAuthenticator.cs`
- Test: `tests/GrainWire.Tests/ChatAuthTests.cs` (no server needed)

**Interfaces:**
- Produces (namespace `ChatSample.Server.Auth`):
  - `sealed class JwtSettings { string Issuer; string Audience; string Key; int LifetimeDays = 7; TokenValidationParameters ToValidationParameters(); }`. Bound from the `Jwt` section. `ToValidationParameters()` validates issuer, audience, lifetime and key, with `NameClaimType = "sub"`.
  - `static JwtSettings JwtSettings.Load(IConfiguration configuration)`. Throws `InvalidOperationException("Jwt:Key must be set to at least 32 bytes ...")` when the key is missing or short.
  - `sealed class TokenService(JwtSettings settings, TimeProvider time)` with `string Issue(string username)`. Uses `JsonWebTokenHandler` with `sub` = username.
  - `sealed class JwtConnectionAuthenticator(JwtSettings settings) : IConnectionAuthenticator`. Same shape as the example in `docs/security.md`: it fails for an empty token, an invalid token, or a missing `sub`, and on success returns `AuthenticationResult.Success(new PlayerId(sub), claims)`.

- [ ] **Step 1: Write the failing tests.**
  - `Tokens_round_trip_through_the_authenticator`: `Issue("alice")`, then `AuthenticateAsync` gives player `"alice"`.
  - `The_authenticator_refuses_bad_tokens`: an empty token, `"garbage"`, a token signed with another key, and an expired token (use a `FakeTimeProvider`, or a `TimeProvider` subclass, set 8 days back) each give a failed result.
  - `A_missing_or_short_key_stops_startup`: `JwtSettings.Load` over a config with no key, or a 10-char key, throws.

  Build the context directly: `new ConnectionAuthContext { ClientId = "c1", Token = token }`. Its properties are public `init` properties.
- [ ] **Step 2: Run and see them fail.** Expected: compile failure.
- [ ] **Step 3: Implement the three classes.**
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: `Add JWT sign-in for the chat sample's API and connections.`

### Task 4: Host and Web API

**Files:**
- Create: `samples/Chat/Chat.Server/ChatServerHost.cs`, `Api/AuthEndpoints.cs`, `Api/ChatEndpoints.cs`, `appsettings.json`, `appsettings.Development.json`, `Properties/launchSettings.json`
- Modify: `samples/Chat/Chat.Server/Program.cs` → `ChatServerHost.Build(args).Run();`
- Create: `tests/GrainWire.Tests/ChatServerFixture.cs`
- Test: `tests/GrainWire.Tests/ChatServerTests.cs`

**Interfaces:**
- Consumes: `ChatStore`, `ChatException` (Task 2); `JwtSettings`, `TokenService`, `JwtConnectionAuthenticator` (Task 3).
- Produces:
  - `public static class ChatServerHost { public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null); }`. It calls `configure` before reading any configuration. It registers:
    - Orleans, with `UseLocalhostClustering(Orleans:SiloPort ?? 11112, Orleans:GatewayPort ?? 30001)` and `silo.AddGrainWire()`;
    - `AddGrainWireGateway(o => o.RoomCreationsPerMinute = 60)`, with a comment on why;
    - `AddDbContextFactory<ChatDb>(UseSqlite(Chat:Database ?? "Data Source=chat.db"))`, then `EnsureCreated` at startup;
    - `ChatStore` and `TokenService` as singletons, `TimeProvider.System`, and `PasswordHasher<string>`;
    - JWT bearer auth from `JwtSettings`, with `MapInboundClaims = false`;
    - `IConnectionAuthenticator` → `JwtConnectionAuthenticator`;
    - CORS for the origins in `Chat:ClientOrigins` (default `["http://localhost:5302"]`);
    - `RoomNotifier`, added in Task 5. Until then the endpoints do not notify.

    Middleware order: CORS, authentication, authorization, `MapGrainWire()`, then the API. `ChatException` maps to `Results.Problem(detail: message, statusCode: 404/400/409)`. Map it in one place, an endpoint filter on the `/api` group or a small middleware.
  - Endpoints, exactly as in the spec's API table. Request and response bodies:
    - `record Credentials(string Username, string Password)` → `record SignedIn(string Token, string Username)`;
    - `record DirectRequest(string Username)`;
    - `record GroupRequest(string Title, string[] Usernames)`;
    - `record AddMemberRequest(string Username)`;
    - `record ReadRequest(long MessageId)`.

    Chat endpoints return `ChatSummary`, or `MessageDto[]` for messages. The caller is `user.FindFirst("sub")!.Value`. Register and login validate with `Rules`, give 400 for invalid input, 409 for a taken name, and 401 with the same detail `"Wrong username or password."` for an unknown user or a wrong password.
  - `ChatServerFixture : IAsyncLifetime` (in `GrainWire.Tests`). Starts `ChatServerHost.Build([], b => ...)` with:
    - `Urls=http://127.0.0.1:{Ports.Next()}` and fresh Orleans ports;
    - `Chat:Database` set to a temp file;
    - a 32-byte test `Jwt:Key`;
    - `Environment` set to `Development`.

    Exposes `HttpClient Http(string? token = null)`, `Uri MqttUrl` (`ws://127.0.0.1:{port}/mqtt`), and `Task<string> SignUpAsync(string name)`, which registers with password `"password1"` and returns the token. Each test uses fresh usernames, e.g. `"alice" + n` with a counter, so tests share one server.

- [ ] **Step 1: Write the failing tests** in `ChatServerTests.cs` (collection `ChatServer`, fixture above):
  - `Sign_up_sign_in_and_search`:
    - register gives 200 with a token;
    - the same name in other case gives 409;
    - login with a wrong password and login as `"nobody"` both give 401, and their `detail` strings are equal;
    - `GET /api/users?search=<prefix>` with a token returns the other user and not the caller;
    - without a token it gives 401.
  - `Private_chat_and_group_over_http`:
    - `POST /api/chats/direct` twice (from each side) returns the same id;
    - `POST /api/chats/group` with an unknown name gives 400;
    - `GET /api/chats` lists both chats.
  - `Bad_chat_ids_are_not_found`: `GET /api/chats/{id}/messages` for `new string('a', 500)` and for a valid-looking id the caller is not in both give 404. A `..` path segment gives 404 too.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/GrainWire.Tests --filter "FullyQualifiedName~ChatServerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement the host, the endpoints and the config files.** `launchSettings.json` uses `http://localhost:5300`, like the Chess one.
- [ ] **Step 4: Run the tests.** Expected: PASS. Then run the whole test project, to check that the Chess and room-core tests still pass with Chat.Server referenced.
- [ ] **Step 5: Commit.** Message: `Add the chat sample's host and Web API.`

### Task 5: The rooms: inbox and chat

**Files:**
- Create: `samples/Chat/Chat.Server/Rooms/InboxRoom.cs`, `Rooms/ChatRoom.cs`, `Rooms/RoomNotifier.cs`
- Modify: `ChatServerHost.cs` (register `RoomNotifier`), `Api/ChatEndpoints.cs` (call it after each change)
- Test: `tests/GrainWire.Tests/ChatServerTests.cs`

**Interfaces:**
- Consumes: `ChatStore`, `MessageDto`, `SentMessage`, `ChatInfo` (Task 2).
- Produces (namespace `ChatSample.Server.Rooms`):
  - `[GenerateSerializer] sealed record InboxNotice([property: Id(0)] string Event, [property: Id(1)] string ChatId, [property: Id(2)] MessageDto? Message)`. `Event` is `"activity"`, `"added"` or `"removed"`. The event data sent to clients is `{ chatId, message }`. That is the spec's `activity` fields with the message grouped, and `message` is null for `added` and `removed`.
  - `public interface IInboxRoom : IGrainWithStringKey { [OneWay] Task Notify(InboxNotice notice); }`
  - `[RoomType("inbox")] sealed class InboxRoom : RoomGrain<InboxState>, IInboxRoom`, where `InboxState` has `[Id(0)] bool Loaded`.
    - `OnCreate` allows only `Creator.Value == RoomId` and sets `Loaded`.
    - `OnJoin` and `OnRejoin` accept only `player == RoomId`.
    - `GetSnapshot` returns an empty object (`new { }`).
    - `Notify`: when not `Loaded`, it calls `DeactivateOnIdle()` and returns. Otherwise it runs `ExecuteAsync`, which broadcasts `notice.Event` with `new { chatId, message }`.
  - `public interface IChatRoom : IGrainWithStringKey { Task MemberAdded(string username); Task MemberLeft(string username); }`
  - `[RoomType("chat")] sealed class ChatRoom(ChatStore store, RoomNotifier notifier) : RoomGrain<ChatRoomState>, IChatRoom`. `ChatRoomState` has `Loaded`, `Kind`, `Title?` and `List<string> Members`.
    - `OnCreate` calls `GetChatInfoAsync(RoomId)`. When the chat is missing or the creator is not in it, it returns `Deny("This chat does not exist.")`. Otherwise it **replaces** every field of `State`.
    - `OnJoin` and `OnRejoin` check `IsMemberAsync`.
    - `[OnMessage("send")] Task<MessageDto> Send(PlayerId player, SendRequest request)`, with `record SendRequest(string Text)`:
      - `CleanText` fails → `Reject("A message is 1 to 2,000 characters.", "invalid-text")`;
      - `AddMessageAsync` returns null → `Reject("You are no longer in this chat.", "not-member")`;
      - otherwise `Broadcast("message", msg)`, then `notifier.Inbox(r, new InboxNotice("activity", RoomId, msg))` for each recipient, then return the message.
    - `MemberAdded` and `MemberLeft` do nothing on a room that is not `Loaded` (they call `DeactivateOnIdle`). Otherwise, inside `ExecuteAsync`:
      - `MemberAdded` updates `Members` and broadcasts `member-added { username }`;
      - `MemberLeft` updates `Members`, broadcasts `member-left { username }`, and calls `Kick(player, "You left this chat.")` if `FindMember` finds them.
    - `GetSnapshot` returns `record ChatView(string Id, string Kind, string? Title, IReadOnlyList<string> Members)`.
  - `sealed class RoomNotifier(IGrainFactory grains, ILogger<RoomNotifier> log)`:
    - `void Inbox(string username, InboxNotice notice)` (one-way, never throws);
    - `Task ChatMemberAddedAsync(string chatId, string username)` and `Task ChatMemberLeftAsync(string chatId, string username)`, which catch and log failures.

    Get grains by GrainWire's grain id, never by string key alone: `grains.GetGrain<IInboxRoom>(new RoomAddress("inbox", username).ToGrainId())`, and the same with `"chat"`.
  - API notifications, sent after the store call succeeds:
    - a direct chat that was `Created` → `added` to the other user;
    - a new group → `added` to every other member;
    - an add that returned true → `added` to the new member, plus `ChatMemberAddedAsync`;
    - leaving → `removed` to the leaver (other tabs), plus `ChatMemberLeftAsync`.

- [ ] **Step 1: Write the failing tests** (`GrainWireClient` with `Url = fixture.MqttUrl` and `AccessToken = token`). Wait for events with `TaskCompletionSource`, with a 10 s timeout.
  - `A_connection_needs_a_valid_token`: `GrainWireClient.ConnectAsync` with `AccessToken = "garbage"` throws `ConnectException`.
  - `Only_the_owner_can_join_an_inbox`: Bob joining `inbox`/alice throws `GrainWireClientException`. Alice joining her own inbox works.
  - `A_message_reaches_the_open_chat_and_the_other_inbox`:
    - Alice and Bob have a direct chat, and Bob has joined his inbox (`Create = true`);
    - Alice joins `chat`/id with `Create = true` and sends `"hi"`;
    - Alice's `RequestAsync` returns the message;
    - Bob's inbox gets `activity` with `message.text == "hi"`;
    - `GET /api/chats` for Bob shows `Unread == 1`.
  - `A_stranger_cannot_open_or_post_in_a_chat`: Carol joining the chat throws `GrainWireClientException`. `send` was never reached.
  - `An_added_member_is_told_and_sees_only_later_messages`:
    - Alice has the group open and sends `"before"`;
    - Alice adds Carol via the API;
    - Carol's inbox gets `added`, and Alice's room gets `member-added { username = carol }`;
    - Alice sends `"after"`;
    - Carol's `GET messages` texts are `["after"]`, and Carol can join the room.
  - `Leaving_closes_the_open_chat_and_stops_sending`:
    - Bob has the group open and calls `DELETE .../members/me`;
    - Bob's room `OnClosed` reports `Kicked`;
    - Bob joining again throws `GrainWireClientException`;
    - Alice's room got `member-left`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/GrainWire.Tests --filter "FullyQualifiedName~ChatServerTests"`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement the rooms and the notifier, and wire up the endpoints.**
- [ ] **Step 4: Run the tests.** Same command, then the whole test project. Expected: all PASS.
- [ ] **Step 5: Run it by hand.** `dotnet run --project samples/Chat/Chat.Server` starts on 5300 and creates `chat.db`. Stop it. Add `chat.db*` to the sample's `.gitignore`, or to the root one, under a samples comment.
- [ ] **Step 6: Commit.** Message: `Add the chat sample's inbox and chat rooms.`

### Task 6: Blazor client: services, inbox and chat session

**Files:**
- Create: `samples/Chat/Chat.BlazorClient/` project files (copy the Chess client's csproj, `index.html` and `launchSettings.json` shape; port 5302), `wwwroot/appsettings.json` (`Chat:ServerUrl` = `http://localhost:5300`, `Chat:MqttUrl` = `ws://localhost:5300/mqtt`), `Program.cs`, `Services/Session.cs`, `Services/ChatApi.cs`, `Services/ChatConnection.cs`, `Chat/Models.cs`, `Chat/Inbox.cs`, `Chat/ChatSession.cs`
- Modify: `GrainWire.slnx` (add the client), staged with `git add -p`

**Interfaces:**
- Consumes the server's HTTP and room contracts from Tasks 4–5. `Chat/Models.cs` holds client copies:
  `MessageDto`, `ChatSummary`, `ChatView`, `SignedIn`, `InboxEvent(string ChatId, MessageDto? Message)` (the data of the server's `InboxNotice` events), `MemberEvent(string Username)`, and the request records.
- Produces (namespace `ChatSample.BlazorClient`):
  - `Services.Session(IJSRuntime js)`:
    - `string? Username`, `string? Token`, `bool IsSignedIn`, `event Action? Changed`;
    - `ValueTask LoadAsync()`, `ValueTask SignInAsync(SignedIn s)` and `ValueTask SignOutAsync()`.

    It keeps its data in localStorage under `chat.token` and `chat.username`.
  - `Services.ChatApi(HttpClient http, Session session)`. One method per endpoint:
    - `RegisterAsync`, `LoginAsync`, `SearchAsync`, `ListChatsAsync`, `StartDirectAsync`, `CreateGroupAsync`, `AddMemberAsync`, `LeaveAsync`, `GetMessagesAsync(string chatId, long? before)`, `MarkReadAsync`;
    - it throws `ApiException(int Status, string Message)` with the problem `detail`, and calls `session.SignOutAsync()` on a 401 from any call except login and register.

    `HttpClient.BaseAddress` comes from `Chat:ServerUrl`.
  - `Services.ChatConnection(IConfiguration, Session)`. Like `ChessConnection`: `Task<GrainWireClient> GetAsync()` uses `AccessTokenProvider = _ => ValueTask.FromResult(session.Token)`, and the connection is closed on sign-out.
  - `Chat.Inbox(ChatConnection, ChatApi, Session)` (singleton):
    - `IReadOnlyList<ChatSummary> Chats`, `string? Error`, `event Action? Changed`;
    - `Task StartAsync()`: joins `inbox`/username (`Create = true`) **before** loading `ListChatsAsync`;
    - `Task StopAsync()`, `void MarkReadLocally(string chatId)`.

    Events and closing:
    - `activity` moves the chat to the top, sets its last message, and adds 1 to unread unless the sender is me or the chat is the open one (`string? OpenChatId { get; set; }`);
    - `added` and `removed`, and every snapshot, reload the list;
    - `OnClosed` with `Lost` calls `StartAsync` again.
  - `Chat.ChatSession(ChatConnection, ChatApi, Inbox)`, disposed by the page, like `GameSession`:
    - `ChatView? Chat`, `IReadOnlyList<MessageDto> Messages`, `bool HasOlder`, `string Notice`, `string? Error`;
    - `Task<bool> OpenAsync(string chatId)`, `Task SendAsync(string text)`, `Task LoadOlderAsync()`.

    Rules:
    - Every snapshot loads the newest page. Messages merge by `Id` into a list sorted by `Id`.
    - The newest visible message is marked read, through the API and `Inbox.MarkReadLocally`.
    - `member-added` and `member-left` update `Chat.Members`.
    - `Lost` → join again with `Create = true`.
    - `Kicked`, `Removed` or a rejected join → set `Error` ("You are no longer in this chat.").
    - `OutcomeUnknownException` on send → `Flash("No answer from the server. Checking…")` and reload the newest page. Never re-send.

- [ ] **Step 1: Create the project and the services.** Check that `dotnet build samples/Chat/Chat.BlazorClient` succeeds with no warnings.
- [ ] **Step 2: Commit.** Message: `Add the chat sample's Blazor client services.`

### Task 7: Blazor client: pages

**Files:**
- Create: `App.razor` (router; `NotFound` like Chess), `_Imports.razor`, `Layout/MainLayout.razor` (connection line "Reconnecting…" like Chess, user name, sign-out), `Pages/Login.razor` (`/login`), `Pages/Chats.razor` (`/` and `/c/{Id}`), `Pages/NotFound.razor`, `Components/ChatList.razor`, `Components/UserSearch.razor`, `Components/NewGroupDialog.razor`, `Components/ChatPane.razor`, `wwwroot/css/app.css`

**Interfaces:**
- Consumes: `Session`, `ChatApi`, `Inbox` and `ChatSession` from Task 6.
- Behavior:
  - `/login`: two buttons, "Sign in" and "Create account". Errors are shown inline. On success it goes to `/`.
  - `Chats`: redirects to `/login` when there is no session, and calls `Inbox.StartAsync()` on first render. The layout follows the spec's sketch:
    - **left column:** `UserSearch` (300 ms debounce; picking a user calls `StartDirectAsync` and navigates to `/c/{id}`), a "New group" button, then `ChatList` (title, last message as `sender: text`, and an unread badge when it is above 0);
    - **right column:** `ChatPane` for the `Id` in the route, which creates a `ChatSession` and sets `Inbox.OpenChatId`. For groups it has a member list, "Add member" (using `UserSearch`) and "Leave group" (after leaving, navigate to `/`).

    The send box submits on Enter. A "Load older" button shows while `HasOlder` is true.
  - `NewGroupDialog`: title, chosen users (picked via `UserSearch`, removable), Create. Shows API errors.
  - Styling: plain, like the Chess client.
- [ ] **Step 1: Build the pages.** `dotnet build samples/Chat/Chat.BlazorClient` → no warnings.
- [ ] **Step 2: Check it in the browser.** Run the server and the client, then open `http://localhost:5302` in two browser contexts:
  1. Sign up as `alice` and `bob`. A wrong password shows the error.
  2. Alice searches `bo` and opens a chat with Bob, then sends "hi". Bob's list shows it at the top with badge 1 and no reload. Opening it clears the badge.
  3. Alice creates group "trip" with Bob and sends "before". She adds `carol` (signed up in a third context): the group appears in Carol's list, and Carol does not see "before". After Alice sends "after", Carol does see it.
  4. Bob leaves "trip": it disappears from his list, and his open pane shows the "no longer in this chat" message.
  5. Restart the server while both are connected. Both reconnect by themselves, history is still there, and sending works.
- [ ] **Step 3: Commit.** Message: `Add the chat sample's Blazor pages.`

### Task 8: README and links

**Files:**
- Create: `samples/Chat/README.md`
- Modify: `README.md` (Documentation list: `- [Chat sample](samples/Chat/README.md): private and group chat with a database, a Web API and a Blazor client`)

- [ ] **Step 1: Write the README** in the Chess README's voice and structure.
  - **What each part is:** links to `ChatStore.cs`, `ChatRoom.cs`, `InboxRoom.cs`, `ChatEndpoints.cs` and `ChatSession.cs`, with one line on the "database holds the data, rooms carry the live part" idea and the three reasons (membership is presence, 20 rooms per connection, inbox).
  - **Run it:** the server, then the client, both with `dotnet run`.
  - **Things to try:** the spec's list.
  - **What the sample leaves out:**
    - migrations (`EnsureCreated`);
    - a real key store;
    - sign-up rate limits and password reset;
    - per-device message history for re-added members;
    - the spec's non-goals.
- [ ] **Step 2: Check the build and tests.** `dotnet build -c Release` on the solution, then `dotnet test --no-build -c Release`. Expected: PASS. CI's `dotnet` job builds the whole solution and runs these tests, so no new CI job is needed. This replaces the spec's "chat-sample CI job".
- [ ] **Step 3: Commit.** Message: `Document the chat sample.`
