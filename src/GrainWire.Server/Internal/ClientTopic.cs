namespace GrainWire.Server.Internal;

/// <summary>A parsed client-to-server topic: <c>rooms/{type}/{id}/cmd|sys/{name}</c>.</summary>
internal readonly record struct ClientTopic(RoomAddress Room, TopicKind Kind, string Name)
{
    public static bool TryParse(string? topic, out ClientTopic result)
    {
        result = default;
        if (topic is null)
        {
            return false;
        }

        string[] parts = topic.Split('/');
        if (parts.Length != 5 || parts[0] != "rooms")
        {
            return false;
        }

        TopicKind kind;
        switch (parts[3])
        {
            case "cmd":
                kind = TopicKind.Command;
                break;
            case "sys":
                kind = TopicKind.System;
                break;
            default:
                return false;
        }

        if (!RoomAddress.IsValidSegment(parts[1]) || !RoomAddress.IsValidSegment(parts[2]) || !RoomAddress.IsValidSegment(parts[4]))
        {
            return false;
        }

        result = new ClientTopic(new RoomAddress(parts[1], parts[2]), kind, parts[4]);
        return true;
    }
}
