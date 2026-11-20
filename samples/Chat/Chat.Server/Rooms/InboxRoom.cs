using ChatSample.Server.Data;
using GrainWire;
using Orleans.Concurrency;

namespace ChatSample.Server.Rooms;

/// <summary>
/// One per user (the room id is the username). It holds no data: it tells the user's connections about chats
/// they do not have open.
/// </summary>
[RoomType("inbox")]
public sealed class InboxRoom : RoomGrain<InboxState>, IInboxRoom
{
    /// <inheritdoc />
    public Task Notify(InboxNotice notice)
    {
        if (!State.Loaded)
        {
            // Nobody has this inbox open, so there is no one to tell.
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        return ExecuteAsync(() =>
        {
            Broadcast(notice.Event, new { chatId = notice.ChatId, message = notice.Message });
            return Task.CompletedTask;
        });
    }

    /// <summary>Only the user the inbox belongs to may create it.</summary>
    protected override Task<CreateResult> OnCreate(CreateContext context)
    {
        if (context.Creator.Value != RoomId)
        {
            return Task.FromResult(CreateResult.Deny("This is not your inbox."));
        }

        State.Loaded = true;
        return Task.FromResult(CreateResult.Allow());
    }

    /// <summary>Only the user the inbox belongs to may join it.</summary>
    protected override Task<JoinResult> OnJoin(JoinContext context) => Task.FromResult(OwnerOnly(context));

    /// <summary>Only the user the inbox belongs to may come back.</summary>
    protected override Task<JoinResult> OnRejoin(JoinContext context) => Task.FromResult(OwnerOnly(context));

    /// <summary>The inbox has nothing to show. The client reloads its chat list from the API on every snapshot.</summary>
    protected override object GetSnapshot(PlayerId viewer) => new { };

    private JoinResult OwnerOnly(JoinContext context) =>
        context.Player.Value == RoomId ? JoinResult.Accept() : JoinResult.Reject("This is not your inbox.");
}

/// <summary>
/// Something a user should hear about in a chat they may not have open. <paramref name="Event"/> is
/// <c>activity</c> (a new message), <c>added</c> or <c>removed</c>. <paramref name="Message"/> is set only for
/// <c>activity</c>.
/// </summary>
[GenerateSerializer]
public sealed record InboxNotice(
    [property: Id(0)] string Event,
    [property: Id(1)] string ChatId,
    [property: Id(2)] MessageDto? Message);

/// <summary>How the API and the chat rooms reach a user's inbox.</summary>
public interface IInboxRoom : IGrainWithStringKey
{
    /// <summary>Sends the notice to the user's open connections. The caller does not wait for it.</summary>
    [OneWay]
    Task Notify(InboxNotice notice);
}

/// <summary>The inbox room's state: only whether the room was really created.</summary>
[GenerateSerializer]
public sealed class InboxState
{
    /// <summary>
    /// True once the room was created. A call on a room that is not running starts an empty one, which this tells
    /// apart from a real room.
    /// </summary>
    [Id(0)]
    public bool Loaded { get; set; }
}
