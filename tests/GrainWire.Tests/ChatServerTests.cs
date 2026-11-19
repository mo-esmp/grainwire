using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatSample.Server.Api;
using ChatSample.Server.Data;

namespace GrainWire.Tests;

[Collection(ChatServerCollection.Name)]
public sealed class ChatServerTests(ChatServerFixture server)
{
    private static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("detail").GetString();
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
        HttpResponseMessage traversal = await frankHttp.GetAsync("/api/chats/../messages");
        Assert.Equal(HttpStatusCode.NotFound, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
    }
}
