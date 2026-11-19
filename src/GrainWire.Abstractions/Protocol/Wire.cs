namespace GrainWire.Protocol;

/// <summary>
/// Names used on the wire. See docs/protocol.md.
/// </summary>
public static class Wire
{
    /// <summary>
    /// Sender id the gateway uses for its own messages. Client ids may not start with '$'.
    /// </summary>
    public const string GatewaySender = "$grainwire";

    /// <summary>
    /// The topic a client receives its messages on.
    /// </summary>
    /// <param name="clientId">The MQTT client id.</param>
    /// <returns>The inbox topic of that client.</returns>
    public static string InboxTopic(string clientId) => $"clients/{clientId}/inbox";

    /// <summary>
    /// Reserved envelope types (the <c>t</c> field). App event names may not start with '$'.
    /// </summary>
    public static class Types
    {
        /// <summary>
        /// A baseline of room state for one viewer.
        /// </summary>
        public const string Snapshot = "$snapshot";

        /// <summary>
        /// A member's presence changed.
        /// </summary>
        public const string Presence = "$presence";

        /// <summary>
        /// The answer to a request the client sent.
        /// </summary>
        public const string Reply = "$reply";

        /// <summary>
        /// The connection's membership in the room ended.
        /// </summary>
        public const string Closed = "$closed";
    }

    /// <summary>
    /// Framework commands under <c>rooms/{type}/{id}/sys/</c>.
    /// </summary>
    public static class Sys
    {
        /// <summary>
        /// Join (or create) a room.
        /// </summary>
        public const string Join = "join";

        /// <summary>
        /// Leave a room.
        /// </summary>
        public const string Leave = "leave";

        /// <summary>
        /// Catch up with the room's event stream.
        /// </summary>
        public const string Sync = "sync";
    }

    /// <summary>
    /// Values of the <c>status</c> field of a reply.
    /// </summary>
    public static class Status
    {
        /// <summary>
        /// The request succeeded.
        /// </summary>
        public const string Ok = "ok";

        /// <summary>
        /// The request was refused and changed nothing.
        /// </summary>
        public const string Rejected = "rejected";

        /// <summary>
        /// The request failed.
        /// </summary>
        public const string Error = "error";
    }

    /// <summary>
    /// Values of the <c>code</c> field of a reply.
    /// </summary>
    public static class Codes
    {
        /// <summary>
        /// The player joined an existing room.
        /// </summary>
        public const string Joined = "joined";

        /// <summary>
        /// The player created the room and joined it.
        /// </summary>
        public const string Created = "created";

        /// <summary>
        /// The client was already up to date.
        /// </summary>
        public const string InSync = "in-sync";

        /// <summary>
        /// The client missed events and received a fresh snapshot.
        /// </summary>
        public const string Resynced = "resynced";

        /// <summary>
        /// The player left the room.
        /// </summary>
        public const string Left = "left";

        /// <summary>
        /// A handler rejected the command.
        /// </summary>
        public const string Rejected = "rejected";

        /// <summary>
        /// The room refused to be created.
        /// </summary>
        public const string CreateDenied = "create-denied";

        /// <summary>
        /// The room refused the join.
        /// </summary>
        public const string JoinRejected = "join-rejected";

        /// <summary>
        /// The room has reached its member limit.
        /// </summary>
        public const string RoomFull = "room-full";

        /// <summary>
        /// The player's role does not allow the command.
        /// </summary>
        public const string Forbidden = "forbidden";

        /// <summary>
        /// The payload could not be read.
        /// </summary>
        public const string BadPayload = "bad-payload";

        /// <summary>
        /// The client sent too many messages.
        /// </summary>
        public const string RateLimited = "rate-limited";

        /// <summary>
        /// The client is in too many rooms.
        /// </summary>
        public const string TooManyRooms = "too-many-rooms";

        /// <summary>
        /// The client has too many requests in flight.
        /// </summary>
        public const string TooManyRequests = "too-many-requests";

        /// <summary>
        /// The room does not exist.
        /// </summary>
        public const string RoomNotFound = "room-not-found";

        /// <summary>
        /// No room grain is registered for the room type.
        /// </summary>
        public const string UnknownRoomType = "unknown-room-type";

        /// <summary>
        /// The player is not a member of the room.
        /// </summary>
        public const string NotMember = "not-member";

        /// <summary>
        /// The connection has not joined the room.
        /// </summary>
        public const string NotJoined = "not-joined";

        /// <summary>
        /// The client refers to an older incarnation of the room.
        /// </summary>
        public const string StaleRoom = "stale-room";

        /// <summary>
        /// The room has no handler for the command.
        /// </summary>
        public const string UnknownCommand = "unknown-command";

        /// <summary>
        /// A handler threw an unexpected exception.
        /// </summary>
        public const string HandlerError = "handler-error";

        /// <summary>
        /// The change could not be saved and was rolled back.
        /// </summary>
        public const string NotCommitted = "not-committed";

        /// <summary>
        /// The room could not be reached.
        /// </summary>
        public const string Unavailable = "unavailable";
    }

    /// <summary>
    /// Values of <c>d.reason</c> in a <c>$closed</c> notice.
    /// </summary>
    public static class ClosedReasons
    {
        /// <summary>
        /// The room was closed.
        /// </summary>
        public const string Closed = "closed";

        /// <summary>
        /// The member was kicked out.
        /// </summary>
        public const string Kicked = "kicked";

        /// <summary>
        /// The member was removed (for example when the grace period expired).
        /// </summary>
        public const string Removed = "removed";

        /// <summary>
        /// The room was lost, for example because it no longer exists.
        /// </summary>
        public const string Lost = "lost";
    }
}
