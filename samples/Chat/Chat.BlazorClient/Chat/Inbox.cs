using ChatSample.BlazorClient.Services;
using GrainWire.Client;

namespace ChatSample.BlazorClient.Chat;

/// <summary>
/// The user's chat list, kept up to date. It joins the user's inbox room, loads the list from the API and applies
/// the inbox events that follow: a new message moves its chat to the top and counts as unread, and a chat that was
/// added or removed reloads the list. There is one for the whole app.
/// </summary>
public sealed class Inbox(ChatConnection connection, ChatApi api, Session session)
{
    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<InboxEvent> _pending = [];
    private IReadOnlyList<ChatSummary> _chats = [];
    private string? _listOwner; // the user the list belongs to
    private Room<InboxView>? _room;
    private int _loads; // loads in progress; events that arrive during one are applied to its result
    private int _generation; // changes on every start and stop, so a stale start or close can tell

    /// <summary>Called whenever the list or the error changes. It can be called on any thread.</summary>
    public event Action? Changed;

    /// <summary>The user's chats, the one with the newest message first.</summary>
    public IReadOnlyList<ChatSummary> Chats
    {
        get
        {
            lock (_gate)
            {
                return _chats;
            }
        }
    }

    /// <summary>Why the list could not be loaded, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// The chat the user has open. Its new messages do not count as unread, because the user sees them.
    /// </summary>
    public string? OpenChatId { get; set; }

    /// <summary>
    /// Joins the inbox room, then loads the chat list. The join comes first, so that nothing that happens while the
    /// list is loading is missed. Calling it again starts over.
    /// </summary>
    public async Task StartAsync()
    {
        string? me = session.Username;
        if (me is null)
        {
            return;
        }

        int generation = await StopCoreAsync();
        lock (_gate)
        {
            if (_listOwner != me)
            {
                // Another user than the one the list was loaded for: never show their chats.
                _chats = [];
                _pending.Clear();
                _listOwner = me;
            }
        }

        try
        {
            GrainWireClient client = await connection.GetAsync();
            Room<InboxView> room = await client.JoinAsync<InboxView>("inbox", me, new JoinOptions { Create = true });
            lock (_gate)
            {
                if (generation != _generation)
                {
                    // Stopped, or started again, while the join was under way.
                    _ = LeaveQuietlyAsync(room);
                    return;
                }

                _room = room;

                // A snapshot comes after joining and after every reconnect or catch-up, and events may have been missed
                // before it, so the list is loaded again each time.
                _subscriptions.Add(room.OnSnapshot((view, info) => { _ = ReloadAsync(); }));
                _subscriptions.Add(room.On<InboxEvent>("activity", OnActivity));
                _subscriptions.Add(room.On<InboxEvent>("added", added => { _ = ReloadAsync(); }));
                _subscriptions.Add(room.On<InboxEvent>("removed", removed => { _ = ReloadAsync(); }));
                _subscriptions.Add(room.OnClosed(closed => OnClosed(closed, generation)));
            }
        }
        catch (Exception error) when (error is GrainWireClientException or ArgumentException or InvalidOperationException)
        {
            Fail(error);
            return;
        }

        await ReloadAsync();
    }

    /// <summary>Leaves the inbox room and forgets the list. Call it when the user signs out.</summary>
    public async Task StopAsync()
    {
        await StopCoreAsync();
        lock (_gate)
        {
            _chats = [];
            _pending.Clear();
            _listOwner = null;
        }

        Error = null;
        Changed?.Invoke();
    }

    /// <summary>The user has read the chat, so its unread count goes to zero.</summary>
    public void MarkReadLocally(string chatId)
    {
        lock (_gate)
        {
            int index = _chats.ToList().FindIndex(chat => chat.Id == chatId);
            if (index < 0 || _chats[index].Unread == 0)
            {
                return;
            }

            List<ChatSummary> chats = [.. _chats];
            chats[index] = chats[index] with { Unread = 0 };
            _chats = chats;
        }

        Changed?.Invoke();
    }

    private async Task<int> StopCoreAsync()
    {
        Room<InboxView>? room;
        int generation;
        lock (_gate)
        {
            generation = ++_generation;
            room = _room;
            _room = null;
            foreach (IDisposable subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        if (room is not null)
        {
            await LeaveQuietlyAsync(room);
        }

        return generation;
    }

    private static async Task LeaveQuietlyAsync(Room room)
    {
        try
        {
            await room.LeaveAsync();
        }
        catch (GrainWireClientException)
        {
            // Offline, or the room is already closed: there is nothing left to leave.
        }
    }

    private void OnClosed(ClosedInfo closed, int generation)
    {
        bool current;
        lock (_gate)
        {
            current = generation == _generation;
        }

        // Only a room lost on the server is joined again. Left means this code left it on purpose.
        if (current && closed.Reason == RoomClosedReason.Lost)
        {
            _ = StartAsync();
        }
    }

    private void OnActivity(InboxEvent activity)
    {
        bool known;
        lock (_gate)
        {
            if (_loads > 0)
            {
                // A load is under way and its result may or may not include this message: apply it to the result.
                _pending.Add(activity);
            }

            known = TryApplyActivityLocked(activity);
        }

        if (!known)
        {
            // A chat this list has not seen yet.
            _ = ReloadAsync();
            return;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Moves the chat to the top with its new message. Returns false when the list has no such chat.
    /// A message the list already shows is not counted twice.
    /// </summary>
    private bool TryApplyActivityLocked(InboxEvent activity)
    {
        if (activity.Message is not { } message)
        {
            return true;
        }

        int index = _chats.ToList().FindIndex(chat => chat.Id == activity.ChatId);
        if (index < 0)
        {
            return false;
        }

        ChatSummary chat = _chats[index];
        if (chat.LastMessage is { } last && last.Id >= message.Id)
        {
            return true;
        }

        bool unread = message.Sender != session.Username && activity.ChatId != OpenChatId;
        ChatSummary updated = chat with { LastMessage = message, Unread = chat.Unread + (unread ? 1 : 0) };
        List<ChatSummary> chats = [.. _chats];
        chats.RemoveAt(index);
        chats.Insert(0, updated);
        _chats = chats;
        return true;
    }

    private async Task ReloadAsync()
    {
        lock (_gate)
        {
            _loads++;
        }

        try
        {
            IReadOnlyList<ChatSummary> loaded = await api.ListChatsAsync();
            lock (_gate)
            {
                // The loaded list replaces the old one. Events that came in while it loaded are applied on top;
                // those it already includes are skipped.
                _chats = loaded;
                foreach (InboxEvent activity in _pending)
                {
                    TryApplyActivityLocked(activity);
                }
            }

            Error = null;
        }
        catch (ApiException error)
        {
            Error = error.Message;
        }
        finally
        {
            lock (_gate)
            {
                if (--_loads == 0)
                {
                    _pending.Clear();
                }
            }
        }

        Changed?.Invoke();
    }

    private void Fail(Exception error)
    {
        Error = error switch
        {
            NotConnectedException => "You are offline. Reconnecting…",
            ConnectException => "The chat server cannot be reached. Is it running?",
            _ => string.IsNullOrEmpty(error.Message) ? "The chat list could not be opened." : error.Message,
        };
        Changed?.Invoke();
    }
}
