using GrainWire;

namespace ChatSample.Server.Rooms;

/// <summary>
/// Tells rooms about changes made elsewhere: inboxes about new messages and memberships, open chat rooms about
/// members who came or went. A failed call is logged and never fails the caller, because the database change it
/// reports is already saved and clients catch up on their next snapshot.
/// </summary>
public sealed class RoomNotifier(IGrainFactory grains, ILogger<RoomNotifier> log)
{
    /// <summary>Sends a notice to the user's inbox without waiting for it. Never throws.</summary>
    public void Inbox(string username, InboxNotice notice)
    {
        try
        {
            // Notify is one-way: the call returns as soon as it is sent.
            _ = grains.GetGrain<IInboxRoom>(new RoomAddress("inbox", username).ToGrainId()).Notify(notice);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not tell {User}'s inbox about {Event} in chat {Chat}.", username, notice.Event, notice.ChatId);
        }
    }

    /// <summary>Tells the chat's room, if it is open, that the user was added.</summary>
    public async Task ChatMemberAddedAsync(string chatId, string username)
    {
        try
        {
            await Chat(chatId).MemberAdded(username);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not tell chat {Chat} that {User} was added.", chatId, username);
        }
    }

    /// <summary>Tells the chat's room, if it is open, that the user left.</summary>
    public async Task ChatMemberLeftAsync(string chatId, string username)
    {
        try
        {
            await Chat(chatId).MemberLeft(username);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not tell chat {Chat} that {User} left.", chatId, username);
        }
    }

    private IChatRoom Chat(string chatId) => grains.GetGrain<IChatRoom>(new RoomAddress("chat", chatId).ToGrainId());
}
