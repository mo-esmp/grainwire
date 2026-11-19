using ChatSample.BlazorClient.Services;
using GrainWire.Client;

namespace ChatSample.BlazorClient.Chat;

/// <summary>
/// One open chat as this user sees it. It joins the chat room, keeps the members from the room's snapshot, loads the
/// messages through the API and adds the new ones that arrive as events. The page only draws what is here, and
/// disposes the session when it is done.
/// </summary>
public sealed class ChatSession(ChatConnection connection, ChatApi api, Inbox inbox) : IAsyncDisposable
{
    private const int PageSize = 50; // the server's history page

    private const string NoLongerMember = "You are no longer in this chat.";

    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private IReadOnlyList<MessageDto> _messages = [];
    private Room<ChatView>? _room;
    private string? _chatId;
    private long _markedRead;
    private bool _hasOlder;
    private bool _disposed;

    /// <summary>Called whenever something the page shows has changed. It can be called on any thread.</summary>
    public event Action? Changed;

    /// <summary>The chat, or null until the first snapshot has arrived.</summary>
    public ChatView? Chat { get; private set; }

    /// <summary>The messages loaded so far, oldest first.</summary>
    public IReadOnlyList<MessageDto> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages;
            }
        }
    }

    /// <summary>True when there may be older messages than the ones loaded.</summary>
    public bool HasOlder
    {
        get
        {
            lock (_gate)
            {
                return _hasOlder;
            }
        }
    }

    /// <summary>A short message for the user, such as why a message was refused. It clears itself after a few seconds.</summary>
    public string Notice { get; private set; } = "";

    /// <summary>Why the chat cannot be shown, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Joins the chat room. The chat and its messages arrive with the first snapshot.
    /// </summary>
    /// <returns>False when the chat could not be opened; <see cref="Error"/> then says why.</returns>
    public async Task<bool> OpenAsync(string chatId)
    {
        _chatId = chatId;

        // From now on the user sees this chat, so its new messages are not unread.
        inbox.OpenChatId = chatId;
        return await JoinAsync();
    }

    /// <summary>
    /// Sends a message. A message the server did not answer is never sent again: the newest messages are loaded
    /// instead, to see whether it arrived.
    /// </summary>
    public async Task SendAsync(string text)
    {
        if (_room is not { } room || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            MessageDto sent = await room.RequestAsync<MessageDto>("send", new SendRequest(text));
            Merge([sent], older: false);
            await MarkNewestReadAsync();
        }
        catch (OutcomeUnknownException)
        {
            Flash("No answer from the server. Checking…");
            await LoadNewestAsync();
        }
        catch (RejectedException error) when (error.Code == "not-member")
        {
            Fail(NoLongerMember);
        }
        catch (GrainWireClientException error)
        {
            Flash(Describe(error, "That did not work."));
        }
    }

    /// <summary>Loads the page of messages before the oldest one shown.</summary>
    public async Task LoadOlderAsync()
    {
        if (_chatId is not { } chatId)
        {
            return;
        }

        long? before;
        lock (_gate)
        {
            before = _messages.Count > 0 ? _messages[0].Id : null;
        }

        try
        {
            IReadOnlyList<MessageDto> page = await api.GetMessagesAsync(chatId, before);
            Merge(page, older: true);
        }
        catch (ApiException error)
        {
            OnApiError(error);
        }
    }

    /// <summary>Shows a message for a few seconds.</summary>
    public void Flash(string message)
    {
        Notice = message;
        Notify();
        _ = ClearNoticeLaterAsync(message);
    }

    /// <summary>Stops following the chat and leaves its room. The user stays a member of the chat.</summary>
    public async ValueTask DisposeAsync()
    {
        Room<ChatView>? room;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            room = DetachLocked();
        }

        if (inbox.OpenChatId == _chatId)
        {
            inbox.OpenChatId = null;
        }

        if (room is not null)
        {
            await LeaveAsync(room);
        }
    }

    private async Task<bool> JoinAsync()
    {
        Room<ChatView> room;
        try
        {
            GrainWireClient client = await connection.GetAsync();
            room = await client.JoinAsync<ChatView>("chat", _chatId!, new JoinOptions { Create = true });
        }
        catch (Exception error) when (error is GrainWireClientException or ArgumentException or InvalidOperationException)
        {
            Fail(error is RejectedException or RoomException ? NoLongerMember : Describe(error, "This chat could not be opened."));
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                // The user left the page while the chat was being joined.
                _ = LeaveAsync(room);
                return false;
            }

            _room = room;

            // A snapshot is "this is the chat now". It arrives after joining, after a reconnect and after catching up.
            // It may stand for messages that never came as events, so the newest page is loaded each time.
            _subscriptions.Add(room.OnSnapshot((view, info) =>
            {
                if (view is null)
                {
                    return;
                }

                // The list is copied, because the events that follow are applied to it.
                Chat = view with { Members = [.. view.Members] };
                Notify();
                _ = LoadNewestAsync();
            }));

            _subscriptions.Add(room.On<MessageDto>("message", message =>
            {
                Merge([message], older: false);
                _ = MarkNewestReadAsync();
            }));

            _subscriptions.Add(room.On<MemberEvent>("member-added", added =>
            {
                if (Chat is { } chat && !chat.Members.Contains(added.Username))
                {
                    Chat = chat with { Members = [.. chat.Members, added.Username] };
                    Notify();
                }
            }));

            _subscriptions.Add(room.On<MemberEvent>("member-left", left =>
            {
                if (Chat is { } chat && chat.Members.Contains(left.Username))
                {
                    Chat = chat with { Members = [.. chat.Members.Where(member => member != left.Username)] };
                    Notify();
                }
            }));

            _subscriptions.Add(room.OnClosed(OnClosed));
        }

        connection.StateChanged += Notify;
        return true;
    }

    private void OnClosed(ClosedInfo closed)
    {
        if (closed.Reason == RoomClosedReason.Lost)
        {
            // The room's state was lost on the server: join again, which creates the room again.
            _ = RejoinAsync();
        }
        else if (closed.Reason is RoomClosedReason.Kicked or RoomClosedReason.Removed)
        {
            Fail(NoLongerMember);
        }
    }

    private async Task RejoinAsync()
    {
        Room<ChatView>? old;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            old = DetachLocked();
        }

        if (old is not null)
        {
            await LeaveAsync(old);
        }

        await JoinAsync();
    }

    /// <summary>Drops the room and its subscriptions, and returns the room so that the caller can leave it.</summary>
    private Room<ChatView>? DetachLocked()
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        Room<ChatView>? room = _room;
        _room = null;
        connection.StateChanged -= Notify;
        return room;
    }

    private async Task LoadNewestAsync()
    {
        if (_chatId is not { } chatId)
        {
            return;
        }

        try
        {
            IReadOnlyList<MessageDto> page = await api.GetMessagesAsync(chatId, null);
            Merge(page, older: false);
            await MarkNewestReadAsync();
        }
        catch (ApiException error)
        {
            OnApiError(error);
        }
    }

    /// <summary>
    /// Adds messages to the list, which stays sorted by id, without duplicates. A page of the newest messages that
    /// reaches back before everything loaded so far also tells whether older ones remain.
    /// </summary>
    private void Merge(IReadOnlyList<MessageDto> incoming, bool older)
    {
        lock (_gate)
        {
            long oldest = _messages.Count > 0 ? _messages[0].Id : long.MaxValue;
            SortedDictionary<long, MessageDto> merged = new();
            foreach (MessageDto message in _messages)
            {
                merged[message.Id] = message;
            }

            foreach (MessageDto message in incoming)
            {
                merged[message.Id] = message;
            }

            _messages = [.. merged.Values];

            if (older)
            {
                _hasOlder = incoming.Count >= PageSize;
            }
            else if (incoming.Count >= PageSize && incoming[0].Id <= oldest)
            {
                // A full newest page that starts at or before what was loaded: there may be more before it.
                _hasOlder = true;
            }
            else if (oldest == long.MaxValue)
            {
                // The first page of a chat, and it is not full: this is all there is.
                _hasOlder = false;
            }
        }

        Notify();
    }

    /// <summary>Tells the server and the chat list that the user has read up to the newest message shown.</summary>
    private async Task MarkNewestReadAsync()
    {
        long newest;
        lock (_gate)
        {
            if (_messages.Count == 0 || _chatId is not { } || _messages[^1].Id <= _markedRead)
            {
                return;
            }

            newest = _messages[^1].Id;
            _markedRead = newest;
        }

        inbox.MarkReadLocally(_chatId!);
        try
        {
            await api.MarkReadAsync(_chatId!, newest);
        }
        catch (ApiException error)
        {
            OnApiError(error);
        }
    }

    private void OnApiError(ApiException error)
    {
        if (error.Status == 404)
        {
            Fail(NoLongerMember);
        }
        else if (error.Status != 401)
        {
            // A 401 signs the user out, which the app handles; anything else is worth a short message.
            Flash(error.Message);
        }
    }

    private void Fail(string message)
    {
        Error = message;
        Notify();
    }

    private static async Task LeaveAsync(Room room)
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

    private static string Describe(Exception error, string fallback) => error switch
    {
        RejectedException rejected => rejected.Reason ?? fallback,
        NotConnectedException => "You are offline. Reconnecting…",
        ConnectException => "The chat server cannot be reached. Is it running?",
        _ => string.IsNullOrEmpty(error.Message) ? fallback : error.Message,
    };

    private async Task ClearNoticeLaterAsync(string message)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (!_disposed && Notice == message)
        {
            Notice = "";
            Notify();
        }
    }

    private void Notify() => Changed?.Invoke();
}
