using System.Security.Claims;
using ChatSample.Server.Data;
using ChatSample.Server.Rooms;

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

        chats.MapPost("/direct", async (ClaimsPrincipal user, ChatStore store, RoomNotifier notifier, TimeProvider time, DirectRequest request) =>
        {
            string me = Caller(user);
            (ChatSummary chat, bool created) = await store.GetOrCreateDirectAsync(me, request.Username, time.GetUtcNow());
            if (created)
            {
                TellAdded(notifier, chat, me);
            }

            return Results.Ok(chat);
        });

        chats.MapPost("/group", async (ClaimsPrincipal user, ChatStore store, RoomNotifier notifier, TimeProvider time, GroupRequest request) =>
        {
            string me = Caller(user);
            ChatSummary chat = await store.CreateGroupAsync(me, request.Title, request.Usernames ?? [], time.GetUtcNow());
            TellAdded(notifier, chat, me);
            return Results.Ok(chat);
        });

        chats.MapPost("/{id}/members", async (ClaimsPrincipal user, ChatStore store, RoomNotifier notifier, TimeProvider time, string id, AddMemberRequest request) =>
        {
            if (await store.AddMemberAsync(Caller(user), id, request.Username, time.GetUtcNow()))
            {
                // The store accepted the name, so it is valid.
                string added = Rules.NormalizeUsername(request.Username)!;
                notifier.Inbox(added, new InboxNotice("added", id, null));
                await notifier.ChatMemberAddedAsync(id, added);
            }

            return Results.NoContent();
        });

        chats.MapDelete("/{id}/members/me", async (ClaimsPrincipal user, ChatStore store, RoomNotifier notifier, string id) =>
        {
            string me = Caller(user);
            await store.LeaveAsync(me, id);

            // The leaver's other tabs drop the chat from their list.
            notifier.Inbox(me, new InboxNotice("removed", id, null));
            await notifier.ChatMemberLeftAsync(id, me);
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

    /// <summary>Tells every member of a new chat except the one who made it.</summary>
    private static void TellAdded(RoomNotifier notifier, ChatSummary chat, string creator)
    {
        foreach (string member in chat.Members.Where(m => m != creator))
        {
            notifier.Inbox(member, new InboxNotice("added", chat.Id, null));
        }
    }

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
