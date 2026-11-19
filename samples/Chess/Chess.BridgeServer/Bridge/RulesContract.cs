using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChessBridge.Server.Bridge;

// The contract between the bridge grain and an external rules service: one POST /decide per room operation.
// It is plain JSON (camelCase), so a rules service can be written in any language. See the README.

/// <summary>
/// What the grain sends to the rules service: the room's current state and the one thing that happened.
/// </summary>
/// <param name="RoomType">The room type, so one service can serve several games.</param>
/// <param name="RoomId">The room id.</param>
/// <param name="Now">The room's clock. Rules must use it instead of their own clock.</param>
/// <param name="State">The state the service returned last time, or null for a new room.</param>
/// <param name="Members">The members of the room with their roles and presence.</param>
/// <param name="Input">What happened.</param>
public sealed record RulesRequest(
    string RoomType,
    string RoomId,
    DateTimeOffset Now,
    JsonNode? State,
    IReadOnlyList<RulesMember> Members,
    RulesInput Input);

/// <summary>
/// A member of the room as the rules service sees it.
/// </summary>
/// <param name="Player">The player id.</param>
/// <param name="Role">The member's role, or null.</param>
/// <param name="Presence"><c>online</c>, <c>reconnecting</c> or <c>left</c>.</param>
public sealed record RulesMember(string Player, string? Role, string Presence);

/// <summary>
/// One thing that happened in the room.
/// </summary>
/// <param name="Kind"><c>create</c>, <c>join</c>, <c>rejoin</c>, <c>command</c>, <c>deadline</c>, <c>presence</c> or <c>left</c>.</param>
public sealed record RulesInput(string Kind)
{
    /// <summary>The player who did it (every kind except <c>deadline</c>).</summary>
    public string? Player { get; init; }

    /// <summary>The command name (<c>command</c>) or the deadline name (<c>deadline</c>).</summary>
    public string? Name { get; init; }

    /// <summary>The command payload, the join data, or the creation options.</summary>
    public JsonNode? Data { get; init; }

    /// <summary>For <c>join</c>: true when this player is creating the room.</summary>
    public bool? IsCreator { get; init; }

    /// <summary>For <c>presence</c>: <c>online</c> or <c>reconnecting</c>.</summary>
    public string? Status { get; init; }

    /// <summary>For <c>left</c>: <c>grace-expired</c> or <c>removed</c>.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// What the rules service answers: either a rejection, or the new state with what to send.
/// </summary>
public sealed record RulesDecision
{
    /// <summary>Set to refuse the input. Nothing else in the decision is used.</summary>
    public RulesRejection? Reject { get; init; }

    /// <summary>The room's new state. It is stored by the grain and sent back with the next request.</summary>
    public JsonNode? State { get; init; }

    /// <summary>For <c>join</c>: the role the new member gets.</summary>
    public string? Role { get; init; }

    /// <summary>
    /// What each player sees in a snapshot, keyed by player id, with <c>*</c> for everyone else.
    /// Null keeps the views from the previous decision.
    /// </summary>
    public Dictionary<string, JsonNode?>? Views { get; init; }

    /// <summary>What to send and change once the operation has committed, in order.</summary>
    public List<RulesEffect>? Effects { get; init; }
}

/// <summary>
/// A refusal, sent to the player as a rejected request.
/// </summary>
/// <param name="Reason">A message for the player.</param>
/// <param name="Code">An optional machine-readable code.</param>
public sealed record RulesRejection(string Reason, string? Code = null);

/// <summary>
/// One effect. Each kind maps to one <see cref="GrainWire.RoomGrain{TState}"/> method.
/// </summary>
/// <param name="Kind">
/// <c>broadcast</c>, <c>sendTo</c>, <c>setRole</c>, <c>kick</c>, <c>removeMember</c>, <c>schedule</c>, <c>cancelSchedule</c> or <c>close</c>.
/// </param>
public sealed record RulesEffect(string Kind)
{
    /// <summary>The event name (<c>broadcast</c>, <c>sendTo</c>) or the deadline name (<c>schedule</c>, <c>cancelSchedule</c>).</summary>
    public string? Name { get; init; }

    /// <summary>The event payload.</summary>
    public JsonNode? Data { get; init; }

    /// <summary>The player the effect is about (<c>sendTo</c>, <c>setRole</c>, <c>kick</c>, <c>removeMember</c>).</summary>
    public string? Player { get; init; }

    /// <summary>For <c>broadcast</c>: a player who does not get the event.</summary>
    public string? Except { get; init; }

    /// <summary>For <c>setRole</c>: the new role, or null for none.</summary>
    public string? Role { get; init; }

    /// <summary>For <c>kick</c> and <c>close</c>: why.</summary>
    public string? Reason { get; init; }

    /// <summary>For <c>schedule</c>: when the deadline is due.</summary>
    public DateTimeOffset? At { get; init; }
}

internal static class RulesJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
