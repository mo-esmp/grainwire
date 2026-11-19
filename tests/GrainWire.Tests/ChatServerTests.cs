using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatSample.Server.Api;
using ChatSample.Server.Data;
using GrainWire.Client;
using JoinOptions = GrainWire.Client.JoinOptions;

namespace GrainWire.Tests;

[Collection(ChatServerCollection.Name)]
public sealed class ChatServerTests(ChatServerFixture server)
{
    private static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("detail").GetString();
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>The data of an inbox event: the chat, and the message for <c>activity</c>.</summary>
    private sealed record InboxEvent(string ChatId, MessageDto? Message);

    /// <summary>The data of <c>member-added</c> and <c>member-left</c>.</summary>
    private sealed record MemberEvent(string Username);

    /// <summary>The chat room's snapshot.</summary>
    private sealed record ChatView(string Id, string Kind, string? Title, string[] Members);

    private Task<GrainWireClient> ConnectAsync(string token) =>
        GrainWireClient.ConnectAsync(new GrainWireClientOptions { Url = server.MqttUrl, AccessToken = token });

    /// <summary>Completes with the data of the first <paramref name="name"/> event the room receives.</summary>
    private static Task<T> NextAsync<T>(Room room, string name)
    {
        TaskCompletionSource<T> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        room.On<T>(name, data => seen.TrySetResult(data));
        return seen.Task.WaitAsync(Wait);
    }

    private static Task<Room<JsonElement>> OpenInboxAsync(GrainWireClient client, string username) =>
        client.JoinAsync("inbox", username, new JoinOptions { Create = true });

    private static Task<Room<ChatView>> OpenChatAsync(GrainWireClient client, string chatId) =>
        client.JoinAsync<ChatView>("chat", chatId, new JoinOptions { Create = true });

    private static async Task<string[]> TextsAsync(HttpClient http, string chatId, long? before = null)
    {
        string query = before is null ? "" : $"?before={before}";
        MessageDto[]? page = await http.GetFromJsonAsync<MessageDto[]>($"/api/chats/{chatId}/messages{query}");
        return page!.Select(m => m.Text).ToArray();
    }

    private static async Task<ChatSummary> SummaryAsync(HttpClient http, string chatId)
    {
        ChatSummary[]? chats = await http.GetFromJsonAsync<ChatSummary[]>("/api/chats");
        return chats!.Single(c => c.Id == chatId);
    }

    private static async Task<ChatSummary> CreateGroupAsync(HttpClient http, params string[] others)
    {
        HttpResponseMessage created = await http.PostAsJsonAsync("/api/chats/group", new GroupRequest("Team", others));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ChatSummary>())!;
    }

    [Fact]
    public async Task Sign_up_sign_in_and_search()
    {
        string alice = ChatServerFixture.NewName("alice");
        string bob = ChatServerFixture.NewName("alice");
        string aliceToken = await server.SignUpAsync(alice);
        await server.SignUpAsync(bob);

        using HttpClient anonymous = server.Http();
        HttpResponseMessage again = await anonymous.PostAsJsonAsync("/api/auth/register", new Credentials(alice.ToUpperInvariant(), "password1"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        HttpResponseMessage wrong = await anonymous.PostAsJsonAsync("/api/auth/login", new Credentials(alice, "not-the-password"));
        HttpResponseMessage unknown = await anonymous.PostAsJsonAsync("/api/auth/login", new Credentials("nobody", "password1"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await DetailAsync(wrong), await DetailAsync(unknown));
        Assert.Equal("Wrong username or password.", await DetailAsync(wrong));

        HttpResponseMessage good = await anonymous.PostAsJsonAsync("/api/auth/login", new Credentials(alice, "password1"));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        HttpResponseMessage weak = await anonymous.PostAsJsonAsync("/api/auth/register", new Credentials(ChatServerFixture.NewName("weak"), "short"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        using HttpClient http = server.Http(aliceToken);
        string[]? found = await http.GetFromJsonAsync<string[]>("/api/users?search=alice");
        Assert.NotNull(found);
        Assert.Contains(bob, found);
        Assert.DoesNotContain(alice, found);

        HttpResponseMessage noToken = await anonymous.GetAsync("/api/users?search=alice");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }

    [Fact]
    public async Task Private_chat_and_group_over_http()
    {
        string carol = ChatServerFixture.NewName("carol");
        string dave = ChatServerFixture.NewName("dave");
        string erin = ChatServerFixture.NewName("erin");
        using HttpClient carolHttp = server.Http(await server.SignUpAsync(carol));
        using HttpClient daveHttp = server.Http(await server.SignUpAsync(dave));
        await server.SignUpAsync(erin);

        ChatSummary? fromCarol = await (await carolHttp.PostAsJsonAsync("/api/chats/direct", new DirectRequest(dave))).Content.ReadFromJsonAsync<ChatSummary>();
        ChatSummary? fromDave = await (await daveHttp.PostAsJsonAsync("/api/chats/direct", new DirectRequest(carol))).Content.ReadFromJsonAsync<ChatSummary>();
        Assert.NotNull(fromCarol);
        Assert.Equal(fromCarol.Id, fromDave!.Id);

        HttpResponseMessage unknown = await carolHttp.PostAsJsonAsync("/api/chats/group", new GroupRequest("Team", [dave, "no-such-user"]));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        HttpResponseMessage created = await carolHttp.PostAsJsonAsync("/api/chats/group", new GroupRequest("Team", [dave, erin]));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        ChatSummary? group = await created.Content.ReadFromJsonAsync<ChatSummary>();

        ChatSummary[]? chats = await carolHttp.GetFromJsonAsync<ChatSummary[]>("/api/chats");
        Assert.NotNull(chats);
        Assert.Contains(chats, c => c.Id == fromCarol.Id);
        Assert.Contains(chats, c => c.Id == group!.Id);
    }

    [Fact]
    public async Task Bad_chat_ids_are_not_found()
    {
        string frank = ChatServerFixture.NewName("frank");
        string gina = ChatServerFixture.NewName("gina");
        string harry = ChatServerFixture.NewName("harry");
        using HttpClient frankHttp = server.Http(await server.SignUpAsync(frank));
        using HttpClient ginaHttp = server.Http(await server.SignUpAsync(gina));
        await server.SignUpAsync(harry);

        ChatSummary? chat = await (await frankHttp.PostAsJsonAsync("/api/chats/direct", new DirectRequest(harry))).Content.ReadFromJsonAsync<ChatSummary>();

        HttpResponseMessage tooLong = await frankHttp.GetAsync($"/api/chats/{new string('a', 500)}/messages");
        HttpResponseMessage notMember = await ginaHttp.GetAsync($"/api/chats/{chat!.Id}/messages");
        // Uri removes "..", even escaped, unless told not to. This sends the path as written.
        Uri raw = new($"{frankHttp.BaseAddress}api/chats/../messages", new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        Assert.Equal("/api/chats/../messages", raw.PathAndQuery);
        HttpResponseMessage traversal = await frankHttp.GetAsync(raw);
        Assert.Equal(HttpStatusCode.NotFound, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
    }

    [Fact]
    public async Task A_connection_needs_a_valid_token()
    {
        await Assert.ThrowsAsync<ConnectException>(() => ConnectAsync("garbage"));
    }

    [Fact]
    public async Task Only_the_owner_can_join_an_inbox()
    {
        string alice = ChatServerFixture.NewName("alice");
        string bob = ChatServerFixture.NewName("bob");
        await using GrainWireClient aliceClient = await ConnectAsync(await server.SignUpAsync(alice));
        await using GrainWireClient bobClient = await ConnectAsync(await server.SignUpAsync(bob));

        // Bob can neither create Alice's inbox nor join it once it is open.
        await Assert.ThrowsAnyAsync<GrainWireClientException>(() => OpenInboxAsync(bobClient, alice));
        Room<JsonElement> inbox = await OpenInboxAsync(aliceClient, alice);
        Assert.Equal(alice, inbox.You!.Player);
        await Assert.ThrowsAnyAsync<GrainWireClientException>(() => OpenInboxAsync(bobClient, alice));
    }

    [Fact]
    public async Task A_message_reaches_the_open_chat_and_the_other_inbox()
    {
        string alice = ChatServerFixture.NewName("alice");
        string bob = ChatServerFixture.NewName("bob");
        string aliceToken = await server.SignUpAsync(alice);
        string bobToken = await server.SignUpAsync(bob);
        using HttpClient aliceHttp = server.Http(aliceToken);
        using HttpClient bobHttp = server.Http(bobToken);
        await using GrainWireClient aliceClient = await ConnectAsync(aliceToken);
        await using GrainWireClient bobClient = await ConnectAsync(bobToken);

        Room<JsonElement> bobInbox = await OpenInboxAsync(bobClient, bob);
        Task<InboxEvent> added = NextAsync<InboxEvent>(bobInbox, "added");
        HttpResponseMessage started = await aliceHttp.PostAsJsonAsync("/api/chats/direct", new DirectRequest(bob));
        ChatSummary chat = (await started.Content.ReadFromJsonAsync<ChatSummary>())!;
        Assert.Equal(chat.Id, (await added).ChatId);

        Room<ChatView> room = await OpenChatAsync(aliceClient, chat.Id);
        Assert.Equal(new[] { alice, bob }.Order(StringComparer.Ordinal), room.View!.Members);
        Assert.Equal(("direct", chat.Id), (room.View.Kind, room.View.Id));

        Task<InboxEvent> activity = NextAsync<InboxEvent>(bobInbox, "activity");
        Task<MessageDto> broadcast = NextAsync<MessageDto>(room, "message");
        MessageDto first = await room.RequestAsync<MessageDto>("send", new { Text = " hi " });
        Assert.Equal(("hi", alice), (first.Text, first.Sender));
        Assert.Equal(first.Id, (await broadcast).Id);

        InboxEvent notice = await activity;
        Assert.Equal(chat.Id, notice.ChatId);
        Assert.Equal("hi", notice.Message!.Text);
        Assert.Equal(1, (await SummaryAsync(bobHttp, chat.Id)).Unread);

        RejectedException empty = await Assert.ThrowsAsync<RejectedException>(() => room.RequestAsync<MessageDto>("send", new { Text = "  " }));
        Assert.Equal("invalid-text", empty.Code);

        // The history pages backwards from "before", and the read marker is taken from the body.
        MessageDto second = await room.RequestAsync<MessageDto>("send", new { Text = "again" });
        Assert.Equal(["hi", "again"], await TextsAsync(bobHttp, chat.Id));
        Assert.Equal(["hi"], await TextsAsync(bobHttp, chat.Id, before: second.Id));
        Assert.Empty(await TextsAsync(bobHttp, chat.Id, before: first.Id));
        Assert.Equal(2, (await SummaryAsync(bobHttp, chat.Id)).Unread);
        HttpResponseMessage read = await bobHttp.PostAsJsonAsync($"/api/chats/{chat.Id}/read", new ReadRequest(first.Id));
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        Assert.Equal(1, (await SummaryAsync(bobHttp, chat.Id)).Unread);
    }

    [Fact]
    public async Task A_stranger_cannot_open_or_post_in_a_chat()
    {
        string alice = ChatServerFixture.NewName("alice");
        string bob = ChatServerFixture.NewName("bob");
        string carol = ChatServerFixture.NewName("carol");
        string aliceToken = await server.SignUpAsync(alice);
        await server.SignUpAsync(bob);
        using HttpClient aliceHttp = server.Http(aliceToken);
        await using GrainWireClient aliceClient = await ConnectAsync(aliceToken);
        await using GrainWireClient carolClient = await ConnectAsync(await server.SignUpAsync(carol));
        ChatSummary chat = await CreateGroupAsync(aliceHttp, bob);

        // Nobody has the chat open, so Carol's join would create the room.
        await Assert.ThrowsAnyAsync<GrainWireClientException>(() => OpenChatAsync(carolClient, chat.Id));
        await Assert.ThrowsAnyAsync<GrainWireClientException>(() => OpenChatAsync(carolClient, "no-such-chat"));

        // Alice has it open, so Carol's join is an ordinary join. Carol never gets in, so she cannot send.
        await OpenChatAsync(aliceClient, chat.Id);
        await Assert.ThrowsAnyAsync<GrainWireClientException>(() => OpenChatAsync(carolClient, chat.Id));
        Assert.Empty(await TextsAsync(aliceHttp, chat.Id));
    }

    [Fact]
    public async Task An_added_member_is_told_and_sees_only_later_messages()
    {
        string alice = ChatServerFixture.NewName("alice");
        string bob = ChatServerFixture.NewName("bob");
        string carol = ChatServerFixture.NewName("carol");
        string aliceToken = await server.SignUpAsync(alice);
        string carolToken = await server.SignUpAsync(carol);
        await server.SignUpAsync(bob);
        using HttpClient aliceHttp = server.Http(aliceToken);
        using HttpClient carolHttp = server.Http(carolToken);
        await using GrainWireClient aliceClient = await ConnectAsync(aliceToken);
        await using GrainWireClient carolClient = await ConnectAsync(carolToken);
        ChatSummary chat = await CreateGroupAsync(aliceHttp, bob);

        Room<ChatView> room = await OpenChatAsync(aliceClient, chat.Id);
        await room.RequestAsync<MessageDto>("send", new { Text = "before" });

        Room<JsonElement> carolInbox = await OpenInboxAsync(carolClient, carol);
        Task<InboxEvent> added = NextAsync<InboxEvent>(carolInbox, "added");
        Task<MemberEvent> memberAdded = NextAsync<MemberEvent>(room, "member-added");
        HttpResponseMessage add = await aliceHttp.PostAsJsonAsync($"/api/chats/{chat.Id}/members", new AddMemberRequest(carol.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
        InboxEvent notice = await added;
        Assert.Equal(chat.Id, notice.ChatId);
        Assert.Null(notice.Message);
        Assert.Equal(carol, (await memberAdded).Username);

        MessageDto after = await room.RequestAsync<MessageDto>("send", new { Text = "after" });
        Assert.Equal(["after"], await TextsAsync(carolHttp, chat.Id));
        Assert.Empty(await TextsAsync(carolHttp, chat.Id, before: after.Id));
        Assert.Equal(["before"], await TextsAsync(aliceHttp, chat.Id, before: after.Id));

        Room<ChatView> carolRoom = await OpenChatAsync(carolClient, chat.Id);
        Assert.Contains(carol, carolRoom.View!.Members);
    }

    [Fact]
    public async Task Leaving_closes_the_open_chat_and_stops_sending()
    {
        string alice = ChatServerFixture.NewName("alice");
        string bob = ChatServerFixture.NewName("bob");
        string aliceToken = await server.SignUpAsync(alice);
        string bobToken = await server.SignUpAsync(bob);
        using HttpClient aliceHttp = server.Http(aliceToken);
        using HttpClient bobHttp = server.Http(bobToken);
        await using GrainWireClient aliceClient = await ConnectAsync(aliceToken);
        await using GrainWireClient bobClient = await ConnectAsync(bobToken);
        ChatSummary chat = await CreateGroupAsync(aliceHttp, bob);

        Room<ChatView> aliceRoom = await OpenChatAsync(aliceClient, chat.Id);
        Room<ChatView> bobRoom = await OpenChatAsync(bobClient, chat.Id);
        Room<JsonElement> bobInbox = await OpenInboxAsync(bobClient, bob);
        TaskCompletionSource<ClosedInfo> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bobRoom.OnClosed(info => closed.TrySetResult(info));
        Task<MemberEvent> memberLeft = NextAsync<MemberEvent>(aliceRoom, "member-left");
        Task<InboxEvent> removed = NextAsync<InboxEvent>(bobInbox, "removed");

        HttpResponseMessage leave = await bobHttp.DeleteAsync($"/api/chats/{chat.Id}/members/me");
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);

        Assert.Equal(RoomClosedReason.Kicked, (await closed.Task.WaitAsync(Wait)).Reason);
        Assert.Equal(chat.Id, (await removed).ChatId);
        Assert.Equal(bob, (await memberLeft).Username);
        await Assert.ThrowsAnyAsync<GrainWireClientException>(() => OpenChatAsync(bobClient, chat.Id));
    }
}
