namespace ChatSample.BlazorClient.Chat;

/// <summary>A message as the server sends it.</summary>
public sealed record MessageDto(long Id, string Sender, string Text, DateTimeOffset SentAt);

/// <summary>One line of the user's chat list. For a private chat, <paramref name="Title"/> is the other user's name.</summary>
public sealed record ChatSummary(
    string Id,
    string Kind,
    string Title,
    IReadOnlyList<string> Members,
    MessageDto? LastMessage,
    int Unread);

/// <summary>What the chat room shows when it is opened.</summary>
public sealed record ChatView(string Id, string Kind, string? Title, IReadOnlyList<string> Members);

/// <summary>The inbox room's snapshot. It carries nothing: the chat list comes from the API.</summary>
public sealed record InboxView;

/// <summary>The answer to a successful register or sign-in.</summary>
public sealed record SignedIn(string Token, string Username);

/// <summary>The data of an inbox event: the chat it is about, and the new message for <c>activity</c>.</summary>
public sealed record InboxEvent(string ChatId, MessageDto? Message);

/// <summary>The data of the chat room's <c>member-added</c> and <c>member-left</c> events.</summary>
public sealed record MemberEvent(string Username);

/// <summary>Body of register and sign-in.</summary>
public sealed record Credentials(string Username, string Password);

/// <summary>Body of "start a private chat".</summary>
public sealed record DirectRequest(string Username);

/// <summary>Body of "create a group".</summary>
public sealed record GroupRequest(string Title, string[] Usernames);

/// <summary>Body of "add a member".</summary>
public sealed record AddMemberRequest(string Username);

/// <summary>Body of "mark as read".</summary>
public sealed record ReadRequest(long MessageId);

/// <summary>Body of the chat room's <c>send</c> command.</summary>
public sealed record SendRequest(string Text);
