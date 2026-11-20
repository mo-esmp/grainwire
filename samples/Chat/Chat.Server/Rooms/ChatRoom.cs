using ChatSample.Server.Data;
using GrainWire;

namespace ChatSample.Server.Rooms;

/// <summary>How the API tells an open chat room that its members changed.</summary>
public interface IChatRoom : IGrainWithStringKey
{
    /// <summary>
    /// Someone was added or left. The room reads the member list again from the database, tells who has the chat
    /// open, and closes it for anyone who is no longer a member.
    /// </summary>
    Task MembersChanged();
}

/// <summary>
/// One per chat (the room id is the chat id), running while someone has the chat open. Messages are sent here,
/// saved in the database and passed on to the open room and to every member's inbox.
/// </summary>
[RoomType("chat")]
public sealed class ChatRoom(ChatStore store, RoomNotifier notifier) : RoomGrain<ChatRoomState>, IChatRoom
{
    /// <summary>Saves a message from a member, sends it to the room and tells every member's inbox.</summary>
    [OnMessage("send")]
    public async Task<MessageDto> Send(PlayerId player, SendRequest request)
    {
        string? text = Rules.CleanText(request.Text);
        if (text is null)
        {
            Reject("A message is 1 to 2,000 characters.", "invalid-text");
        }

        // Checked in the database on every message, so someone who left can never keep posting.
        SentMessage? sent = await store.AddMessageAsync(RoomId, player.Value, text, Now);
        if (sent is null)
        {
            Reject("You are no longer in this chat.", "not-member");
        }

        // The database has just said who the members are. If the room missed a change (the API's call to it failed),
        // it catches up here.
        IReadOnlySet<string> gone = ApplyMembers([.. sent.Recipients, player.Value]);
        if (gone.Count == 0)
        {
            Broadcast("message", sent.Message);
        }
        else
        {
            // A kick takes effect after this command's events are sent, so a broadcast would still reach the
            // members being removed. Send it to the others one by one instead.
            foreach (RoomMember member in Members.Where(m => !gone.Contains(m.Player.Value)))
            {
                SendTo(member.Player, "message", sent.Message);
            }
        }

        foreach (string recipient in sent.Recipients)
        {
            notifier.Inbox(recipient, new InboxNotice("activity", RoomId, sent.Message));
        }

        // The sender's inbox hears it too, so their chat list moves the chat up, in this tab and in others.
        notifier.Inbox(player.Value, new InboxNotice("activity", RoomId, sent.Message));

        return sent.Message;
    }

    /// <inheritdoc />
    public Task MembersChanged()
    {
        if (!State.Loaded)
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        return ExecuteAsync(async () =>
        {
            ChatInfo? chat = await store.GetChatInfoAsync(RoomId);
            ApplyMembers(chat?.Members ?? []);
        });
    }

    /// <summary>Loads the chat from the database. Only a member may open it.</summary>
    protected override async Task<CreateResult> OnCreate(CreateContext context)
    {
        ChatInfo? chat = await store.GetChatInfoAsync(RoomId);
        if (chat is null || !chat.Members.Contains(context.Creator.Value))
        {
            return CreateResult.Deny("This chat does not exist.");
        }

        // Replaced as a whole, so nothing from an earlier run of this room is left over.
        State = new ChatRoomState { Loaded = true, Kind = chat.Kind, Title = chat.Title, Members = [.. chat.Members] };
        return CreateResult.Allow();
    }

    /// <summary>Only members of the chat may open it.</summary>
    protected override Task<JoinResult> OnJoin(JoinContext context) => MembersOnlyAsync(context);

    /// <summary>
    /// Checks again when someone comes back, in case they left the chat and the room was never told.
    /// </summary>
    protected override Task<JoinResult> OnRejoin(JoinContext context) => MembersOnlyAsync(context);

    /// <summary>The chat's kind, title and members. Everyone in the room sees the same.</summary>
    protected override object GetSnapshot(PlayerId viewer) =>
        new ChatView(RoomId, State.Kind, State.Title, State.Members);

    /// <summary>
    /// Lets in only members. The member list is read from the database each time, so the room also catches up on
    /// any change it was not told about.
    /// </summary>
    private async Task<JoinResult> MembersOnlyAsync(JoinContext context)
    {
        ChatInfo? chat = await store.GetChatInfoAsync(RoomId);
        IReadOnlyList<string> members = chat?.Members ?? [];
        ApplyMembers(members);
        return members.Contains(context.Player.Value)
            ? JoinResult.Accept()
            : JoinResult.Reject("This chat does not exist.");
    }

    /// <summary>
    /// Makes the room's member list match <paramref name="members"/>: tells the room who came and who went, and
    /// closes the chat for anyone gone who has it open. Returns the names of those it closed it for.
    /// </summary>
    private HashSet<string> ApplyMembers(IReadOnlyList<string> members)
    {
        HashSet<string> closed = new(StringComparer.Ordinal);
        foreach (string username in State.Members.Except(members, StringComparer.Ordinal).ToList())
        {
            Broadcast("member-left", new { username });
            PlayerId player = new(username);
            if (FindMember(player) is not null)
            {
                Kick(player, "You are no longer in this chat.");
                closed.Add(username);
            }
        }

        foreach (string username in members.Except(State.Members, StringComparer.Ordinal))
        {
            Broadcast("member-added", new { username });
        }

        State.Members = [.. members.Order(StringComparer.Ordinal)];
        return closed;
    }
}

/// <summary>The chat room's state: what it loaded from the database when it was created.</summary>
[GenerateSerializer]
public sealed class ChatRoomState
{
    /// <summary>
    /// True once the room was created. A call on a room that is not running starts an empty one, which this tells
    /// apart from a real room.
    /// </summary>
    [Id(0)]
    public bool Loaded { get; set; }

    /// <summary>"direct" or "group".</summary>
    [Id(1)]
    public string Kind { get; set; } = "";

    /// <summary>The group's title, or null for a private chat.</summary>
    [Id(2)]
    public string? Title { get; set; }

    /// <summary>The usernames of the chat's members, in order.</summary>
    [Id(3)]
    public List<string> Members { get; set; } = [];
}

/// <summary>The body of the <c>send</c> command.</summary>
public sealed record SendRequest(string Text);

/// <summary>What the chat room shows when someone opens it. Messages are loaded through the API.</summary>
public sealed record ChatView(string Id, string Kind, string? Title, IReadOnlyList<string> Members);
