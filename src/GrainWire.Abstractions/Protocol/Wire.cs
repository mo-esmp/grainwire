namespace GrainWire.Protocol;

/// <summary>Names used on the wire. See docs/protocol.md.</summary>
public static class Wire
{
    /// <summary>Sender id the gateway uses for its own messages. Client ids may not start with '$'.</summary>
    public const string GatewaySender = "$grainwire";

    public static string InboxTopic(string clientId) => $"clients/{clientId}/inbox";

    /// <summary>Reserved envelope types (the <c>t</c> field). App event names may not start with '$'.</summary>
    public static class Types
    {
        public const string Snapshot = "$snapshot";
        public const string Presence = "$presence";
        public const string Reply = "$reply";
        public const string Closed = "$closed";
    }

    /// <summary>Framework commands under <c>rooms/{type}/{id}/sys/</c>.</summary>
    public static class Sys
    {
        public const string Join = "join";
        public const string Leave = "leave";
        public const string Sync = "sync";
    }

    /// <summary>Values of the <c>status</c> field of a reply.</summary>
    public static class Status
    {
        public const string Ok = "ok";
        public const string Rejected = "rejected";
        public const string Error = "error";
    }

    /// <summary>Values of the <c>code</c> field of a reply.</summary>
    public static class Codes
    {
        public const string Joined = "joined";
        public const string Created = "created";
        public const string InSync = "in-sync";
        public const string Resynced = "resynced";
        public const string Left = "left";

        public const string Rejected = "rejected";
        public const string CreateDenied = "create-denied";
        public const string JoinRejected = "join-rejected";
        public const string RoomFull = "room-full";
        public const string Forbidden = "forbidden";
        public const string BadPayload = "bad-payload";
        public const string RateLimited = "rate-limited";
        public const string TooManyRooms = "too-many-rooms";
        public const string TooManyRequests = "too-many-requests";

        public const string RoomNotFound = "room-not-found";
        public const string UnknownRoomType = "unknown-room-type";
        public const string NotMember = "not-member";
        public const string NotJoined = "not-joined";
        public const string StaleRoom = "stale-room";
        public const string UnknownCommand = "unknown-command";
        public const string HandlerError = "handler-error";
        public const string NotCommitted = "not-committed";
        public const string Unavailable = "unavailable";
    }

    /// <summary>Values of <c>d.reason</c> in a <c>$closed</c> notice.</summary>
    public static class ClosedReasons
    {
        public const string Closed = "closed";
        public const string Kicked = "kicked";
        public const string Removed = "removed";
        public const string Lost = "lost";
    }
}
