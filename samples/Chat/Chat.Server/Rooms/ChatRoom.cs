using ChatSample.Server.Data;
using GrainWire;

namespace ChatSample.Server.Rooms;

/// <summary>How the API tells an open chat room that its members changed.</summary>
public interface IChatRoom : IGrainWithStringKey
{
    /// <summary>The user was added to the chat.</summary>
    Task MemberAdded(string username);

    /// <summary>The user left the chat. If they have it open, it is closed for them.</summary>
    Task MemberLeft(string username);
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

        Broadcast("message", sent.Message);
        foreach (string recipient in sent.Recipients)
        {
            notifier.Inbox(recipient, new InboxNotice("activity", RoomId, sent.Message));
        }

        // The sender's inbox hears it too, so their chat list moves the chat up, in this tab and in others.
        notifier.Inbox(player.Value, new InboxNotice("activity", RoomId, sent.Message));

        return sent.Message;
    }

    /// <inheritdoc />
    public Task MemberAdded(string username)
    {
        if (!State.Loaded)
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        return ExecuteAsync(() =>
        {
            if (!State.Members.Contains(username))
            {
                State.Members.Add(username);
                State.Members.Sort(StringComparer.Ordinal);
            }

            Broadcast("member-added", new { username });
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task MemberLeft(string username)
    {
        if (!State.Loaded)
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        return ExecuteAsync(() =>
        {
            State.Members.Remove(username);
            Broadcast("member-left", new { username });
            PlayerId player = new(username);
            if (FindMember(player) is not null)
            {
                Kick(player, "You left this chat.");
            }

            return Task.CompletedTask;
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

    private async Task<JoinResult> MembersOnlyAsync(JoinContext context) =>
        await store.IsMemberAsync(RoomId, context.Player.Value)
            ? JoinResult.Accept()
            : JoinResult.Reject("This chat does not exist.");
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
