using Microsoft.EntityFrameworkCore;

namespace ChatSample.Server.Data;

/// <summary>The two kinds of chat.</summary>
public static class ChatKinds
{
    /// <summary>A private chat between two users.</summary>
    public const string Direct = "direct";

    /// <summary>A chat with a title and two or more members.</summary>
    public const string Group = "group";
}

/// <summary>A registered user. The lowercase username is the identity everywhere.</summary>
public sealed class User
{
    /// <summary>The lowercase username (the key).</summary>
    public string Username { get; set; } = "";

    /// <summary>The password hash.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>When the user registered.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A private or group chat.</summary>
public sealed class Chat
{
    /// <summary>A GUID in "N" format (the key), which is also the GrainWire room id.</summary>
    public string Id { get; set; } = "";

    /// <summary>"direct" or "group" (see <see cref="ChatKinds"/>).</summary>
    public string Kind { get; set; } = ChatKinds.Direct;

    /// <summary>The group's title. Null for a private chat.</summary>
    public string? Title { get; set; }

    /// <summary>For a private chat, the two usernames sorted and joined with a colon. Unique. Null for a group.</summary>
    public string? DirectKey { get; set; }

    /// <summary>When the chat was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the newest message was sent. Never use it in SQL ordering: sort in memory.</summary>
    public DateTimeOffset LastMessageAt { get; set; }
}

/// <summary>A user's membership of a chat.</summary>
public sealed class Member
{
    /// <summary>The chat (part of the key).</summary>
    public string ChatId { get; set; } = "";

    /// <summary>The member's username (part of the key).</summary>
    public string Username { get; set; } = "";

    /// <summary>When the user joined.</summary>
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>The member sees only messages with an id greater than this.</summary>
    public long FirstMessageId { get; set; }

    /// <summary>The newest message the member has read.</summary>
    public long LastReadMessageId { get; set; }
}

/// <summary>One message in a chat.</summary>
public sealed class Message
{
    /// <summary>The auto-increment id. Messages are ordered by it.</summary>
    public long Id { get; set; }

    /// <summary>The chat the message belongs to.</summary>
    public string ChatId { get; set; } = "";

    /// <summary>The sender's username.</summary>
    public string Sender { get; set; } = "";

    /// <summary>The message text.</summary>
    public string Text { get; set; } = "";

    /// <summary>When the message was sent.</summary>
    public DateTimeOffset SentAt { get; set; }
}

/// <summary>The chat sample's database.</summary>
public sealed class ChatDb(DbContextOptions<ChatDb> options) : DbContext(options)
{
    /// <summary>The registered users.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>All chats.</summary>
    public DbSet<Chat> Chats => Set<Chat>();

    /// <summary>Who is in which chat.</summary>
    public DbSet<Member> Members => Set<Member>();

    /// <summary>All messages.</summary>
    public DbSet<Message> Messages => Set<Message>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasKey(u => u.Username);
        modelBuilder.Entity<Chat>().HasKey(c => c.Id);
        modelBuilder.Entity<Chat>().HasIndex(c => c.DirectKey).IsUnique();
        modelBuilder.Entity<Member>().HasKey(m => new { m.ChatId, m.Username });
        modelBuilder.Entity<Message>().HasIndex(m => new { m.ChatId, m.Id });
    }
}
