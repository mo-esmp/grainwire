using System.Text.RegularExpressions;

namespace GrainWire.Client.Internal;

/// <summary>Names used on the wire. See docs/protocol.md.</summary>
internal static partial class Wire
{
    public const string Snapshot = "$snapshot";
    public const string Presence = "$presence";
    public const string Reply = "$reply";
    public const string Closed = "$closed";

    public const string Join = "sys/join";
    public const string Leave = "sys/leave";
    public const string Sync = "sys/sync";
    public const string CommandPrefix = "cmd/";

    public const string InSync = "in-sync";
    public const string RoomNotFound = "room-not-found";
    public const string NotMember = "not-member";
    public const string NotJoined = "not-joined";
    public const string StaleRoom = "stale-room";
    public const string Unavailable = "unavailable";

    /// <summary>The CONNACK user property that carries the player id the server derived from the credentials.</summary>
    public const string PlayerProperty = "gw-player";

    public static string InboxTopic(string clientId) => $"clients/{clientId}/inbox";

    public static string RoomTopic(string roomKey, string path) => $"rooms/{roomKey}/{path}";

    public static bool IsClientId(string value) => ClientIdRegex().IsMatch(value);

    /// <summary>Throws unless <paramref name="value"/> may be used as a room type, a room id or a command name.</summary>
    public static void EnsureName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!NameRegex().IsMatch(value))
        {
            throw new ArgumentException("Must be 1 to 128 characters of A-Z, a-z, 0-9, '_', '.', '~' or '-'.", parameterName);
        }
    }

    public static MemberStatus ParseMemberStatus(string? value) => value switch
    {
        "reconnecting" => MemberStatus.Reconnecting,
        "left" => MemberStatus.Left,
        _ => MemberStatus.Online,
    };

    public static RoomClosedReason ParseClosedReason(string? value) => value switch
    {
        "kicked" => RoomClosedReason.Kicked,
        "removed" => RoomClosedReason.Removed,
        "lost" => RoomClosedReason.Lost,
        _ => RoomClosedReason.Closed,
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{16,128}$")]
    private static partial Regex ClientIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_.~-]{1,128}$")]
    private static partial Regex NameRegex();
}
