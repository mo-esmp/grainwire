using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ChatSample.Server.Data;

/// <summary>Why a <see cref="ChatStore"/> call was refused.</summary>
public enum ChatError
{
    /// <summary>The chat does not exist, or the caller is not in it.</summary>
    NotFound,

    /// <summary>The request breaks a rule (bad title, unknown user, wrong kind of chat, full group).</summary>
    Invalid,

    /// <summary>The name is already used.</summary>
    Taken,
}

/// <summary>Thrown by <see cref="ChatStore"/> when a request is refused.</summary>
public sealed class ChatException(ChatError error, string message) : Exception(message)
{
    /// <summary>Why the request was refused.</summary>
    public ChatError Error { get; } = error;
}

/// <summary>A message as clients and rooms see it.</summary>
[GenerateSerializer]
public sealed record MessageDto(
    [property: Id(0)] long Id,
    [property: Id(1)] string Sender,
    [property: Id(2)] string Text,
    [property: Id(3)] DateTimeOffset SentAt);

/// <summary>One line of a user's chat list. For a private chat, <paramref name="Title"/> is the other user's name.</summary>
public sealed record ChatSummary(
    string Id,
    string Kind,
    string Title,
    IReadOnlyList<string> Members,
    MessageDto? LastMessage,
    int Unread);

/// <summary>What a room needs to know about a chat.</summary>
public sealed record ChatInfo(string Id, string Kind, string? Title, IReadOnlyList<string> Members);

/// <summary>A stored message and the members who should be told about it (everyone but the sender).</summary>
public sealed record SentMessage(MessageDto Message, IReadOnlyList<string> Recipients);

/// <summary>Users, chats, members and messages in the database. Every rule of the chat sample lives here.</summary>
public sealed class ChatStore(IDbContextFactory<ChatDb> dbs)
{
    /// <summary>Adds a user. Returns false when the name is taken (or is not a valid name).</summary>
    public async Task<bool> AddUserAsync(string username, string passwordHash, DateTimeOffset now)
    {
        string? name = Rules.NormalizeUsername(username);
        if (name is null)
        {
            return false;
        }

        await using ChatDb db = await dbs.CreateDbContextAsync();
        if (await db.Users.AnyAsync(u => u.Username == name))
        {
            return false;
        }

        db.Users.Add(new User { Username = name, PasswordHash = passwordHash, CreatedAt = now });
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Someone registered the same name between the check and the save.
            return false;
        }
    }

    /// <summary>The user's password hash, or null when there is no such user.</summary>
    public async Task<string?> GetPasswordHashAsync(string username)
    {
        string? name = Rules.NormalizeUsername(username);
        if (name is null)
        {
            return null;
        }

        await using ChatDb db = await dbs.CreateDbContextAsync();
        return await db.Users.AsNoTracking().Where(u => u.Username == name).Select(u => u.PasswordHash).FirstOrDefaultAsync();
    }

    /// <summary>Usernames starting with the prefix, without the caller, at most 20, in order. An empty prefix finds nobody.</summary>
    public async Task<IReadOnlyList<string>> SearchUsersAsync(string caller, string? prefix)
    {
        string? text = prefix?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        string me = Rules.NormalizeUsername(caller) ?? caller;
        await using ChatDb db = await dbs.CreateDbContextAsync();
        return await db.Users.AsNoTracking()
            .Where(u => u.Username.StartsWith(text) && u.Username != me)
            .OrderBy(u => u.Username)
            .Take(Rules.SearchLimit)
            .Select(u => u.Username)
            .ToListAsync();
    }

    /// <summary>Finds the private chat between two users, or creates it. <c>Created</c> says which happened.</summary>
    public async Task<(ChatSummary Chat, bool Created)> GetOrCreateDirectAsync(string caller, string other, DateTimeOffset now)
    {
        string me = Rules.NormalizeUsername(caller) ?? throw new ChatException(ChatError.NotFound, "Unknown user.");
        string? them = Rules.NormalizeUsername(other);
        if (them is null)
        {
            throw new ChatException(ChatError.NotFound, "No such user.");
        }

        if (them == me)
        {
            throw new ChatException(ChatError.Invalid, "You cannot chat with yourself.");
        }

        await using ChatDb db = await dbs.CreateDbContextAsync();
        if (!await db.Users.AnyAsync(u => u.Username == them))
        {
            throw new ChatException(ChatError.NotFound, "No such user.");
        }

        string key = Rules.DirectKey(me, them);
        Chat? chat = await db.Chats.FirstOrDefaultAsync(c => c.DirectKey == key);
        bool created = false;
        if (chat is null)
        {
            chat = new Chat { Id = NewId(), Kind = ChatKinds.Direct, DirectKey = key, CreatedAt = now, LastMessageAt = now };
            db.Chats.Add(chat);
            db.Members.Add(NewMember(chat.Id, me, now, 0));
            db.Members.Add(NewMember(chat.Id, them, now, 0));
            try
            {
                await db.SaveChangesAsync();
                created = true;
            }
            catch (DbUpdateException)
            {
                // The other side created the chat first: use theirs.
                db.ChangeTracker.Clear();
                chat = await db.Chats.FirstAsync(c => c.DirectKey == key);
            }
        }

        Member mine = await db.Members.AsNoTracking().FirstAsync(m => m.ChatId == chat.Id && m.Username == me);
        return (await SummarizeAsync(db, chat, mine), created);
    }

    /// <summary>Creates a group with the caller and the listed users. Throws <see cref="ChatError.Invalid"/> and creates nothing when a rule is broken.</summary>
    public async Task<ChatSummary> CreateGroupAsync(string caller, string? title, IReadOnlyList<string> usernames, DateTimeOffset now)
    {
        string me = Rules.NormalizeUsername(caller) ?? throw new ChatException(ChatError.NotFound, "Unknown user.");
        string cleanTitle = Rules.CleanTitle(title)
            ?? throw new ChatException(ChatError.Invalid, "A group needs a title of 1 to 60 characters.");

        var names = new List<string> { me };
        foreach (string raw in usernames)
        {
            string? name = Rules.NormalizeUsername(raw);
            if (name is null)
            {
                throw new ChatException(ChatError.Invalid, $"Unknown user: {raw}.");
            }

            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        if (names.Count < 2)
        {
            throw new ChatException(ChatError.Invalid, "A group needs at least one other member.");
        }

        if (names.Count > Rules.MaxGroupMembers)
        {
            throw new ChatException(ChatError.Invalid, $"A group has at most {Rules.MaxGroupMembers} members.");
        }

        await using ChatDb db = await dbs.CreateDbContextAsync();
        List<string> known = await db.Users.AsNoTracking().Where(u => names.Contains(u.Username)).Select(u => u.Username).ToListAsync();
        foreach (string name in names)
        {
            if (!known.Contains(name))
            {
                throw new ChatException(ChatError.Invalid, $"Unknown user: {name}.");
            }
        }

        var chat = new Chat { Id = NewId(), Kind = ChatKinds.Group, Title = cleanTitle, CreatedAt = now, LastMessageAt = now };
        db.Chats.Add(chat);
        foreach (string name in names)
        {
            db.Members.Add(NewMember(chat.Id, name, now, 0));
        }

        await db.SaveChangesAsync();
        Member mine = await db.Members.AsNoTracking().FirstAsync(m => m.ChatId == chat.Id && m.Username == me);
        return await SummarizeAsync(db, chat, mine);
    }

    /// <summary>
    /// Adds a user to a group. True when added, false when already a member. The new member sees only
    /// messages sent from now on.
    /// </summary>
    public async Task<bool> AddMemberAsync(string caller, string chatId, string username, DateTimeOffset now)
    {
        string me = Rules.NormalizeUsername(caller) ?? throw new ChatException(ChatError.NotFound, "No such chat.");
        await using ChatDb db = await dbs.CreateDbContextAsync();

        // Every check and the insert run in one transaction that takes SQLite's write lock at its start (BEGIN
        // IMMEDIATE). Two people adding different users can't both see a free last place.
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        if (!await db.Members.AnyAsync(m => m.ChatId == chatId && m.Username == me))
        {
            throw new ChatException(ChatError.NotFound, "No such chat.");
        }

        Chat chat = await db.Chats.AsNoTracking().FirstAsync(c => c.Id == chatId);
        if (chat.Kind == ChatKinds.Direct)
        {
            throw new ChatException(ChatError.Invalid, "You cannot add people to a private chat.");
        }

        string? name = Rules.NormalizeUsername(username);
        if (name is null || !await db.Users.AnyAsync(u => u.Username == name))
        {
            throw new ChatException(ChatError.Invalid, $"Unknown user: {username}.");
        }

        if (await db.Members.AnyAsync(m => m.ChatId == chatId && m.Username == name))
        {
            return false;
        }

        if (await db.Members.CountAsync(m => m.ChatId == chatId) >= Rules.MaxGroupMembers)
        {
            throw new ChatException(ChatError.Invalid, $"A group has at most {Rules.MaxGroupMembers} members.");
        }

        long newest = await db.Messages.Where(m => m.ChatId == chatId).MaxAsync(m => (long?)m.Id) ?? 0;
        db.Members.Add(NewMember(chatId, name, now, newest));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    /// <summary>Removes the caller from a group.</summary>
    public async Task LeaveAsync(string caller, string chatId)
    {
        await using ChatDb db = await dbs.CreateDbContextAsync();
        Member member = await FindMemberAsync(db, caller, chatId);
        Chat chat = await db.Chats.AsNoTracking().FirstAsync(c => c.Id == chatId);
        if (chat.Kind == ChatKinds.Direct)
        {
            throw new ChatException(ChatError.Invalid, "You cannot leave a private chat.");
        }

        db.Members.Remove(member);
        await db.SaveChangesAsync();
    }

    /// <summary>The caller's chats, the most recently active first.</summary>
    public async Task<IReadOnlyList<ChatSummary>> ListChatsAsync(string caller)
    {
        string? me = Rules.NormalizeUsername(caller);
        if (me is null)
        {
            return [];
        }

        await using ChatDb db = await dbs.CreateDbContextAsync();
        List<Member> mine = await db.Members.AsNoTracking().Where(m => m.Username == me).ToListAsync();
        var ids = mine.Select(m => m.ChatId).ToList();
        List<Chat> chats = await db.Chats.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync();

        var items = new List<(DateTimeOffset At, ChatSummary Summary)>();
        foreach (Chat chat in chats)
        {
            Member member = mine.First(m => m.ChatId == chat.Id);
            ChatSummary summary = await SummarizeAsync(db, chat, member);

            // A chat with no messages counts from when the caller joined it, so a group they were just added to is
            // near the top (for the creator and in a direct chat that is the same moment as the chat's creation).
            items.Add((summary.LastMessage?.SentAt ?? member.JoinedAt, summary));
        }

        return items.OrderByDescending(i => i.At).Select(i => i.Summary).ToList();
    }

    /// <summary>Up to 50 messages the caller can see, oldest first. With <paramref name="before"/>, only those with a smaller id.</summary>
    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(string caller, string chatId, long? before)
    {
        await using ChatDb db = await dbs.CreateDbContextAsync();
        Member member = await FindMemberAsync(db, caller, chatId);
        long first = member.FirstMessageId;
        List<Message> page = await db.Messages.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.Id > first && (before == null || m.Id < before))
            .OrderByDescending(m => m.Id)
            .Take(Rules.PageSize)
            .ToListAsync();
        page.Reverse();
        return page.Select(ToDto).ToList();
    }

    /// <summary>
    /// Marks messages up to <paramref name="messageId"/> as read. The marker never moves back, and never past the
    /// chat's newest message, so a made-up id cannot hide messages that arrive later.
    /// </summary>
    public async Task MarkReadAsync(string caller, string chatId, long messageId)
    {
        await using ChatDb db = await dbs.CreateDbContextAsync();
        Member member = await FindMemberAsync(db, caller, chatId);
        long newest = await db.Messages.Where(m => m.ChatId == chatId).MaxAsync(m => (long?)m.Id) ?? 0;
        messageId = Math.Min(messageId, newest);
        if (messageId > member.LastReadMessageId)
        {
            member.LastReadMessageId = messageId;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>The chat's kind, title and members, or null when it does not exist.</summary>
    public async Task<ChatInfo?> GetChatInfoAsync(string chatId)
    {
        await using ChatDb db = await dbs.CreateDbContextAsync();
        Chat? chat = await db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId);
        return chat is null ? null : new ChatInfo(chat.Id, chat.Kind, chat.Title, await MemberNamesAsync(db, chatId));
    }

    /// <summary>True when the user is in the chat.</summary>
    public async Task<bool> IsMemberAsync(string chatId, string username)
    {
        string? name = Rules.NormalizeUsername(username);
        if (name is null)
        {
            return false;
        }

        await using ChatDb db = await dbs.CreateDbContextAsync();
        return await db.Members.AnyAsync(m => m.ChatId == chatId && m.Username == name);
    }

    /// <summary>
    /// Stores a message from a member and counts it as read by the sender. Returns null when the sender is
    /// not in the chat.
    /// </summary>
    public async Task<SentMessage?> AddMessageAsync(string chatId, string sender, string text, DateTimeOffset now)
    {
        string? name = Rules.NormalizeUsername(sender);
        if (name is null)
        {
            return null;
        }

        await using ChatDb db = await dbs.CreateDbContextAsync();

        // The transaction starts before the membership check. SQLite takes the write lock at its start (BEGIN
        // IMMEDIATE), so a leave can't slip in between the check and the insert.
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        Member? member = await db.Members.FirstOrDefaultAsync(m => m.ChatId == chatId && m.Username == name);
        if (member is null)
        {
            return null;
        }

        Chat chat = await db.Chats.FirstAsync(c => c.Id == chatId);
        var message = new Message { ChatId = chatId, Sender = name, Text = text, SentAt = now };
        db.Messages.Add(message);
        await db.SaveChangesAsync(); // The id is generated here, and the member needs it.
        chat.LastMessageAt = now;
        member.LastReadMessageId = message.Id;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        List<string> recipients = (await MemberNamesAsync(db, chatId)).Where(n => n != name).ToList();
        return new SentMessage(ToDto(message), recipients);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static Member NewMember(string chatId, string username, DateTimeOffset now, long newestMessageId) => new()
    {
        ChatId = chatId,
        Username = username,
        JoinedAt = now,
        FirstMessageId = newestMessageId,
        LastReadMessageId = newestMessageId,
    };

    private static MessageDto ToDto(Message m) => new(m.Id, m.Sender, m.Text, m.SentAt);

    /// <summary>The caller's membership row, or <see cref="ChatError.NotFound"/> when they are not in the chat.</summary>
    private static async Task<Member> FindMemberAsync(ChatDb db, string caller, string chatId)
    {
        string? name = Rules.NormalizeUsername(caller);
        Member? member = name is null ? null : await db.Members.FirstOrDefaultAsync(m => m.ChatId == chatId && m.Username == name);
        return member ?? throw new ChatException(ChatError.NotFound, "No such chat.");
    }

    private static async Task<IReadOnlyList<string>> MemberNamesAsync(ChatDb db, string chatId) =>
        await db.Members.AsNoTracking().Where(m => m.ChatId == chatId).OrderBy(m => m.Username).Select(m => m.Username).ToListAsync();

    private static async Task<ChatSummary> SummarizeAsync(ChatDb db, Chat chat, Member mine)
    {
        IReadOnlyList<string> members = await MemberNamesAsync(db, chat.Id);
        long first = mine.FirstMessageId;
        long read = Math.Max(mine.LastReadMessageId, first);
        Message? last = await db.Messages.AsNoTracking()
            .Where(m => m.ChatId == chat.Id && m.Id > first)
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync();
        int unread = await db.Messages.CountAsync(m => m.ChatId == chat.Id && m.Id > read && m.Sender != mine.Username);
        string title = chat.Kind == ChatKinds.Direct
            ? members.FirstOrDefault(n => n != mine.Username) ?? mine.Username
            : chat.Title ?? "";
        return new ChatSummary(chat.Id, chat.Kind, title, members, last is null ? null : ToDto(last), unread);
    }
}
