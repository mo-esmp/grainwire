using System.Security.Claims;
using ChatSample.Server.Data;

namespace ChatSample.Server.Api;

/// <summary>Body of "start a private chat".</summary>
public sealed record DirectRequest(string Username);

/// <summary>Body of "create a group".</summary>
public sealed record GroupRequest(string Title, string[] Usernames);

/// <summary>Body of "add a member".</summary>
public sealed record AddMemberRequest(string Username);

/// <summary>Body of "mark as read".</summary>
public sealed record ReadRequest(long MessageId);

/// <summary>The endpoints that need a signed-in user: search, chat list, groups, history and read markers.</summary>
public static class ChatEndpoints
{
    /// <summary>Maps the endpoints under the given group, which must require authorization.</summary>
    public static void MapChats(this RouteGroupBuilder api)
    {
        api.AddEndpointFilter(ChatExceptionFilter);

        api.MapGet("/users", async (ClaimsPrincipal user, ChatStore store, string? search) =>
            Results.Ok(await store.SearchUsersAsync(Caller(user), search)));

        RouteGroupBuilder chats = api.MapGroup("/chats");

        chats.MapGet("/", async (ClaimsPrincipal user, ChatStore store) =>
            Results.Ok(await store.ListChatsAsync(Caller(user))));

        chats.MapPost("/direct", async (ClaimsPrincipal user, ChatStore store, TimeProvider time, DirectRequest request) =>
        {
            (ChatSummary chat, _) = await store.GetOrCreateDirectAsync(Caller(user), request.Username, time.GetUtcNow());
            return Results.Ok(chat);
        });

        chats.MapPost("/group", async (ClaimsPrincipal user, ChatStore store, TimeProvider time, GroupRequest request) =>
            Results.Ok(await store.CreateGroupAsync(Caller(user), request.Title, request.Usernames ?? [], time.GetUtcNow())));

        chats.MapPost("/{id}/members", async (ClaimsPrincipal user, ChatStore store, TimeProvider time, string id, AddMemberRequest request) =>
        {
            await store.AddMemberAsync(Caller(user), id, request.Username, time.GetUtcNow());
            return Results.NoContent();
        });

        chats.MapDelete("/{id}/members/me", async (ClaimsPrincipal user, ChatStore store, string id) =>
        {
            await store.LeaveAsync(Caller(user), id);
            return Results.NoContent();
        });

        chats.MapGet("/{id}/messages", async (ClaimsPrincipal user, ChatStore store, string id, long? before) =>
            Results.Ok(await store.GetMessagesAsync(Caller(user), id, before)));

        chats.MapPost("/{id}/read", async (ClaimsPrincipal user, ChatStore store, string id, ReadRequest request) =>
        {
            await store.MarkReadAsync(Caller(user), id, request.MessageId);
            return Results.NoContent();
        });
    }

    private static string Caller(ClaimsPrincipal user) => user.FindFirst("sub")!.Value;

    /// <summary>Turns a refused request (<see cref="ChatException"/>) into a problem answer: 404, 400 or 409.</summary>
    private static async ValueTask<object?> ChatExceptionFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (ChatException e)
        {
            int status = e.Error switch
            {
                ChatError.NotFound => StatusCodes.Status404NotFound,
                ChatError.Taken => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Results.Problem(detail: e.Message, statusCode: status);
        }
    }
}
