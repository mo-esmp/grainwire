using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatSample.BlazorClient.Chat;

namespace ChatSample.BlazorClient.Services;

/// <summary>The server refused a request. <paramref name="Message"/> is the reason it gave, fit to show the user.</summary>
public sealed class ApiException(int status, string message) : Exception(message)
{
    /// <summary>The HTTP status code, or 0 when the server could not be reached.</summary>
    public int Status { get; } = status;
}

/// <summary>
/// The chat server's Web API: sign-in, search, the chat list, groups, history and read markers. A 401 from any call
/// except register and sign-in means the token is no longer good, so the user is signed out.
/// </summary>
public sealed class ChatApi(HttpClient http, Session session)
{
    /// <summary>Creates an account and signs it in.</summary>
    public Task<SignedIn> RegisterAsync(string username, string password) =>
        SendAsync<SignedIn>(HttpMethod.Post, "api/auth/register", new Credentials(username, password), signOutOn401: false);

    /// <summary>Checks a username and password.</summary>
    public Task<SignedIn> LoginAsync(string username, string password) =>
        SendAsync<SignedIn>(HttpMethod.Post, "api/auth/login", new Credentials(username, password), signOutOn401: false);

    /// <summary>Finds users whose names start with <paramref name="prefix"/>.</summary>
    public Task<IReadOnlyList<string>> SearchAsync(string prefix) =>
        SendAsync<IReadOnlyList<string>>(HttpMethod.Get, $"api/users?search={Uri.EscapeDataString(prefix)}");

    /// <summary>The user's chats with their last message and unread count.</summary>
    public Task<IReadOnlyList<ChatSummary>> ListChatsAsync() =>
        SendAsync<IReadOnlyList<ChatSummary>>(HttpMethod.Get, "api/chats");

    /// <summary>Opens the private chat with a user, creating it if needed.</summary>
    public Task<ChatSummary> StartDirectAsync(string username) =>
        SendAsync<ChatSummary>(HttpMethod.Post, "api/chats/direct", new DirectRequest(username));

    /// <summary>Creates a group with the given members, besides the user.</summary>
    public Task<ChatSummary> CreateGroupAsync(string title, IEnumerable<string> usernames) =>
        SendAsync<ChatSummary>(HttpMethod.Post, "api/chats/group", new GroupRequest(title, [.. usernames]));

    /// <summary>Adds a user to a chat.</summary>
    public Task AddMemberAsync(string chatId, string username) =>
        SendAsync(HttpMethod.Post, $"api/chats/{Uri.EscapeDataString(chatId)}/members", new AddMemberRequest(username));

    /// <summary>Leaves a chat.</summary>
    public Task LeaveAsync(string chatId) =>
        SendAsync(HttpMethod.Delete, $"api/chats/{Uri.EscapeDataString(chatId)}/members/me");

    /// <summary>
    /// One page of messages, oldest first. Without <paramref name="before"/> it is the newest page; with it, the page
    /// before that message id.
    /// </summary>
    public Task<IReadOnlyList<MessageDto>> GetMessagesAsync(string chatId, long? before)
    {
        string url = $"api/chats/{Uri.EscapeDataString(chatId)}/messages";
        if (before is { } id)
        {
            url += $"?before={id}";
        }

        return SendAsync<IReadOnlyList<MessageDto>>(HttpMethod.Get, url);
    }

    /// <summary>Says that the user has read the chat up to a message.</summary>
    public Task MarkReadAsync(string chatId, long messageId) =>
        SendAsync(HttpMethod.Post, $"api/chats/{Uri.EscapeDataString(chatId)}/read", new ReadRequest(messageId));

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null, bool signOutOn401 = true)
    {
        using HttpResponseMessage response = await SendCoreAsync(method, url, body, signOutOn401);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>() ?? throw new ApiException(0, "The server sent an empty answer.");
        }
        catch (JsonException)
        {
            throw new ApiException(0, "The server sent an answer that could not be read.");
        }
    }

    private async Task SendAsync(HttpMethod method, string url, object? body = null)
    {
        using HttpResponseMessage response = await SendCoreAsync(method, url, body, signOutOn401: true);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string url, object? body, bool signOutOn401)
    {
        using HttpRequestMessage request = new(method, url);
        if (session.Token is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            throw new ApiException(0, "The chat server cannot be reached. Is it running?");
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized && signOutOn401)
            {
                await session.SignOutAsync();
            }

            throw new ApiException((int)response.StatusCode, await ReadProblemAsync(response));
        }
    }

    /// <summary>The <c>detail</c> of a ProblemDetails answer, or a generic text when there is none.</summary>
    private static async Task<string> ReadProblemAsync(HttpResponseMessage response)
    {
        try
        {
            ProblemBody? problem = await response.Content.ReadFromJsonAsync<ProblemBody>();
            if (!string.IsNullOrWhiteSpace(problem?.Detail))
            {
                return problem.Detail;
            }
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            // Not a problem answer: use the generic text.
        }

        return $"The server refused the request ({(int)response.StatusCode}).";
    }

    private sealed record ProblemBody(string? Detail);
}
