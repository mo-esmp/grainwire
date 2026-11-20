using GrainWire;

namespace ChatSample.Server.Rooms;

/// <summary>
/// Tells rooms about changes made elsewhere: inboxes about new messages and memberships, open chat rooms about
/// members who came or went. A failed call is logged and never fails the caller, because the database change it
/// reports is already saved and the rooms catch up from the database on their own.
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

    /// <summary>
    /// Tells the chat's room, if it is open, that its members changed. It is tried twice. If both fail, the room still
    /// catches up: it reads the member list from the database again on the next message and the next join.
    /// </summary>
    public async Task ChatMembersChangedAsync(string chatId)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await Chat(chatId).MembersChanged();
                return;
            }
            catch (Exception e) when (attempt < 2)
            {
                log.LogInformation(e, "Telling chat {Chat} about a member change failed; trying again.", chatId);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not tell chat {Chat} about a member change.", chatId);
                return;
            }
        }
    }

    private IChatRoom Chat(string chatId) => grains.GetGrain<IChatRoom>(new RoomAddress("chat", chatId).ToGrainId());
}
