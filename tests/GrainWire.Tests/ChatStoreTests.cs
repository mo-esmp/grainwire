using ChatSample.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrainWire.Tests;

public sealed class ChatStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _files = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A SQLite file lock on Windows must not fail a test.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class TestDbFactory(string connectionString) : IDbContextFactory<ChatDb>
    {
        public ChatDb CreateDbContext() =>
            new(new DbContextOptionsBuilder<ChatDb>().UseSqlite(connectionString).Options);
    }

    private ChatStore NewStore()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chat-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var factory = new TestDbFactory($"Data Source={path}");
        using (ChatDb db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        var store = new ChatStore(factory);
        foreach (string name in new[] { "alice", "bob", "carol", "dave" })
        {
            Assert.True(store.AddUserAsync(name, "x", T0).GetAwaiter().GetResult());
        }

        return store;
    }

    private static async Task<ChatException> Refused(Func<Task> action) => await Assert.ThrowsAsync<ChatException>(action);

    private static async Task<string> NewGroup(ChatStore store, params string[] others) =>
        (await store.CreateGroupAsync("alice", "trip", others, T0)).Id;

    private static async Task<List<string>> Texts(ChatStore store, string user, string chatId) =>
        (await store.GetMessagesAsync(user, chatId, null)).Select(m => m.Text).ToList();

    [Fact]
    public async Task Usernames_are_normalized_everywhere()
    {
        ChatStore store = NewStore();
        Assert.False(await store.AddUserAsync(" ALICE ", "x", T0));
        Assert.Equal(["alice"], await store.SearchUsersAsync("bob", "AL"));

        string id = await NewGroup(store, "bob");
        Assert.True(await store.AddMemberAsync("alice", id, "CAROL", T0));
        Assert.True(await store.IsMemberAsync(id, "carol"));
    }

    [Fact]
    public async Task Search_matches_prefixes_excludes_the_caller_and_stops_at_twenty()
    {
        ChatStore store = NewStore();
        for (int i = 0; i < 25; i++)
        {
            Assert.True(await store.AddUserAsync($"user{i:00}", "x", T0));
        }

        Assert.Equal(20, (await store.SearchUsersAsync("alice", "user")).Count);
        Assert.Empty(await store.SearchUsersAsync("alice", "al"));
        Assert.Empty(await store.SearchUsersAsync("alice", "  "));
    }

    [Fact]
    public async Task One_private_chat_per_pair_from_either_side()
    {
        ChatStore store = NewStore();
        (ChatSummary Chat, bool Created) a = await store.GetOrCreateDirectAsync("alice", "bob", T0);
        (ChatSummary Chat, bool Created) b = await store.GetOrCreateDirectAsync("bob", "Alice", T0);

        Assert.True(a.Created);
        Assert.False(b.Created);
        Assert.Equal(a.Chat.Id, b.Chat.Id);
        Assert.Equal("bob", a.Chat.Title);
        Assert.Equal("alice", b.Chat.Title);
    }

    [Fact]
    public async Task Private_chat_with_yourself_or_a_stranger_is_refused()
    {
        ChatStore store = NewStore();
        Assert.Equal(ChatError.Invalid, (await Refused(() => store.GetOrCreateDirectAsync("alice", "alice", T0))).Error);
        Assert.Equal(ChatError.NotFound, (await Refused(() => store.GetOrCreateDirectAsync("alice", "nobody", T0))).Error);
    }

    [Fact]
    public async Task Group_with_an_unknown_user_creates_nothing()
    {
        ChatStore store = NewStore();
        ChatException e = await Refused(() => store.CreateGroupAsync("alice", "trip", ["bob", "nobody"], T0));

        Assert.Equal(ChatError.Invalid, e.Error);
        Assert.Contains("nobody", e.Message);
        Assert.Empty(await store.ListChatsAsync("alice"));
    }

    [Fact]
    public async Task Group_needs_a_title_and_another_member()
    {
        ChatStore store = NewStore();
        Assert.Equal(ChatError.Invalid, (await Refused(() => store.CreateGroupAsync("alice", "  ", ["bob"], T0))).Error);
        Assert.Equal(ChatError.Invalid, (await Refused(() => store.CreateGroupAsync("alice", "trip", [], T0))).Error);
        Assert.Equal(ChatError.Invalid, (await Refused(() => store.CreateGroupAsync("alice", "trip", ["alice"], T0))).Error);
    }

    [Fact]
    public async Task Non_members_get_not_found()
    {
        ChatStore store = NewStore();
        string id = await NewGroup(store, "bob");

        foreach (string chat in new[] { id, "no-such-chat" })
        {
            Assert.Equal(ChatError.NotFound, (await Refused(() => store.GetMessagesAsync("carol", chat, null))).Error);
            Assert.Equal(ChatError.NotFound, (await Refused(() => store.AddMemberAsync("carol", chat, "dave", T0))).Error);
            Assert.Equal(ChatError.NotFound, (await Refused(() => store.LeaveAsync("carol", chat))).Error);
            Assert.Equal(ChatError.NotFound, (await Refused(() => store.MarkReadAsync("carol", chat, 1))).Error);
        }
    }

    [Fact]
    public async Task AddMessage_refuses_a_non_member()
    {
        ChatStore store = NewStore();
        string id = await NewGroup(store, "bob");

        Assert.Null(await store.AddMessageAsync(id, "carol", "hi", T0));
        await store.LeaveAsync("bob", id);
        Assert.Null(await store.AddMessageAsync(id, "bob", "hi", T0));
    }

    [Fact]
    public async Task A_new_member_sees_only_later_messages()
    {
        ChatStore store = NewStore();
        string id = await NewGroup(store, "bob");
        await store.AddMessageAsync(id, "alice", "before", T0);
        await store.AddMemberAsync("alice", id, "carol", T0);
        await store.AddMessageAsync(id, "alice", "after", T0.AddMinutes(1));

        Assert.Equal(["after"], await Texts(store, "carol", id));
        ChatSummary summary = Assert.Single(await store.ListChatsAsync("carol"));
        Assert.Equal(1, summary.Unread);
        Assert.Equal("after", summary.LastMessage?.Text);
    }

    [Fact]
    public async Task Leaving_and_being_added_again_starts_over()
    {
        ChatStore store = NewStore();
        string id = await NewGroup(store, "bob");
        await store.AddMemberAsync("alice", id, "carol", T0);
        await store.AddMessageAsync(id, "alice", "one", T0);
        await store.LeaveAsync("carol", id);
        await store.AddMessageAsync(id, "alice", "two", T0);
        await store.AddMemberAsync("alice", id, "carol", T0);
        await store.AddMessageAsync(id, "alice", "three", T0);

        Assert.Equal(["three"], await Texts(store, "carol", id));
    }

    [Fact]
    public async Task Unread_counts_skip_your_own_messages_and_move_only_forward()
    {
        ChatStore store = NewStore();
        string id = (await store.GetOrCreateDirectAsync("alice", "bob", T0)).Chat.Id;
        SentMessage first = (await store.AddMessageAsync(id, "bob", "1", T0))!;
        SentMessage second = (await store.AddMessageAsync(id, "bob", "2", T0))!;
        await store.AddMessageAsync(id, "bob", "3", T0);
        Assert.Equal(["alice"], first.Recipients);

        async Task<int> Unread() => Assert.Single(await store.ListChatsAsync("alice")).Unread;

        Assert.Equal(3, await Unread());
        await store.MarkReadAsync("alice", id, second.Message.Id);
        Assert.Equal(1, await Unread());
        await store.MarkReadAsync("alice", id, first.Message.Id);
        Assert.Equal(1, await Unread());

        // Sending counts as reading everything up to your own message, and your own message is never unread.
        await store.AddMessageAsync(id, "alice", "mine", T0);
        Assert.Equal(0, await Unread());
    }

    [Fact]
    public async Task Two_users_added_at_once_cannot_both_take_a_groups_last_place()
    {
        ChatStore store = NewStore();
        List<string> others = [];
        for (int i = 0; i < Rules.MaxGroupMembers - 2; i++)
        {
            string name = $"member{i:000}";
            Assert.True(await store.AddUserAsync(name, "x", T0));
            others.Add(name);
        }

        string id = (await store.CreateGroupAsync("alice", "full", others, T0)).Id; // one place left
        Task<bool> addBob = Task.Run(() => store.AddMemberAsync("alice", id, "bob", T0));
        Task<bool> addCarol = Task.Run(() => store.AddMemberAsync("alice", id, "carol", T0));
        Task both = Task.WhenAll(addBob, addCarol);
        await Assert.ThrowsAsync<ChatException>(() => both);

        Assert.Equal(1, new[] { addBob, addCarol }.Count(t => t.IsCompletedSuccessfully));
        Assert.Equal(Rules.MaxGroupMembers, (await store.GetChatInfoAsync(id))!.Members.Count);
    }

    [Fact]
    public async Task A_read_marker_past_the_newest_message_does_not_hide_later_ones()
    {
        ChatStore store = NewStore();
        string id = (await store.GetOrCreateDirectAsync("alice", "bob", T0)).Chat.Id;
        await store.AddMessageAsync(id, "bob", "1", T0);

        async Task<int> Unread() => Assert.Single(await store.ListChatsAsync("alice")).Unread;

        await store.MarkReadAsync("alice", id, long.MaxValue);
        Assert.Equal(0, await Unread());

        await store.AddMessageAsync(id, "bob", "2", T0);
        Assert.Equal(1, await Unread());
    }

    [Fact]
    public async Task History_pages_back_fifty_at_a_time()
    {
        ChatStore store = NewStore();
        string id = (await store.GetOrCreateDirectAsync("alice", "bob", T0)).Chat.Id;
        for (int i = 0; i < 55; i++)
        {
            await store.AddMessageAsync(id, "alice", $"m{i}", T0.AddSeconds(i));
        }

        IReadOnlyList<MessageDto> newest = await store.GetMessagesAsync("bob", id, null);
        Assert.Equal(50, newest.Count);
        Assert.Equal("m5", newest[0].Text);
        Assert.Equal("m54", newest[^1].Text);

        IReadOnlyList<MessageDto> older = await store.GetMessagesAsync("bob", id, newest[0].Id);
        Assert.Equal(["m0", "m1", "m2", "m3", "m4"], older.Select(m => m.Text));
    }

    [Fact]
    public async Task A_group_you_were_just_added_to_is_listed_by_when_you_joined()
    {
        ChatStore store = NewStore();
        string old = (await store.CreateGroupAsync("alice", "old", ["bob"], T0)).Id;
        string newer = (await store.CreateGroupAsync("carol", "newer", ["dave"], T0.AddMinutes(10))).Id;

        await store.AddMemberAsync("alice", old, "carol", T0.AddMinutes(20));

        Assert.Equal([old, newer], (await store.ListChatsAsync("carol")).Select(c => c.Id));
    }

    [Fact]
    public async Task Chats_are_listed_newest_activity_first()
    {
        ChatStore store = NewStore();
        string x = (await store.CreateGroupAsync("alice", "x", ["bob"], T0)).Id;
        string y = (await store.CreateGroupAsync("alice", "y", ["bob"], T0.AddMinutes(1))).Id;
        Assert.Equal([y, x], (await store.ListChatsAsync("alice")).Select(c => c.Id));

        await store.AddMessageAsync(x, "bob", "hi", T0.AddMinutes(2));
        Assert.Equal([x, y], (await store.ListChatsAsync("alice")).Select(c => c.Id));
    }

    [Fact]
    public async Task Adding_an_existing_member_does_nothing()
    {
        ChatStore store = NewStore();
        string id = await NewGroup(store, "bob");

        Assert.False(await store.AddMemberAsync("alice", id, "bob", T0));
    }

    [Theory]
    [InlineData(" Alice ", "alice")]
    [InlineData("bob_1.x-y", "bob_1.x-y")]
    [InlineData("ab", null)]
    [InlineData("a-name-that-is-25-chars-x", null)]
    [InlineData("bad name", null)]
    [InlineData("élan", null)]
    [InlineData(null, null)]
    public void Usernames_are_normalized_or_refused(string? raw, string? expected) =>
        Assert.Equal(expected, Rules.NormalizeUsername(raw));

    [Fact]
    public void Text_title_and_password_limits()
    {
        Assert.Equal("hi", Rules.CleanText("  hi \n"));
        Assert.Null(Rules.CleanText("   "));
        Assert.NotNull(Rules.CleanText(new string('x', 2000)));
        Assert.Null(Rules.CleanText(new string('x', 2001)));
        Assert.Null(Rules.CleanTitle(new string('x', 61)));
        Assert.False(Rules.IsValidPassword("1234567"));
        Assert.True(Rules.IsValidPassword("12345678"));
        Assert.Equal(Rules.DirectKey("bob", "alice"), Rules.DirectKey("alice", "bob"));
        Assert.Equal("alice:bob", Rules.DirectKey("bob", "alice"));
    }
}
