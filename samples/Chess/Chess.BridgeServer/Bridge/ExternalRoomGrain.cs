using System.Text.Json.Nodes;
using GrainWire;

namespace ChessBridge.Server.Bridge;

/// <summary>
/// A room whose rules live in another process. The grain keeps everything GrainWire does (one command at a time,
/// commit then publish, saving, recovery); the rules service only decides, through <see cref="RulesClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every hook and command becomes one call to the rules service with the current state. The answer is applied
/// with the ordinary <see cref="RoomGrain{TState}"/> methods, so it is held back until the operation commits. If the
/// service fails or times out, the exception undoes the operation and nothing is sent.
/// </para>
/// <para>
/// GrainWire finds commands through <see cref="OnMessageAttribute"/>, so a derived class declares one short
/// method per command that calls <see cref="Forward"/>. That is also where each command's roles are set.
/// </para>
/// </remarks>
public abstract class ExternalRoomGrain(RulesClient rules) : RoomGrain<ExternalRoomState>
{
    /// <summary>The key of the view sent to players who have no view of their own.</summary>
    public const string EveryoneElse = "*";

    /// <summary>
    /// True to send presence changes (online, reconnecting) to the rules service. Off by default: each one is a call.
    /// </summary>
    protected virtual bool ForwardPresence => false;

    /// <summary>
    /// Sends a command to the rules service. Call it from a method marked with <see cref="OnMessageAttribute"/>.
    /// </summary>
    protected Task Forward(string command, PlayerId player, JsonNode? data) =>
        DecideAsync(new RulesInput("command") { Name = command, Player = player.Value, Data = data });

    protected override async Task<CreateResult> OnCreate(CreateContext context)
    {
        RulesDecision decision = await DecideAsync(
            new RulesInput("create") { Player = context.Creator.Value, Data = context.Options.As<JsonNode>() },
            throwOnReject: false);
        return decision.Reject is { } reject ? CreateResult.Deny(reject.Reason) : CreateResult.Allow();
    }

    protected override Task<JoinResult> OnJoin(JoinContext context) =>
        JoinAsync(new RulesInput("join") { Player = context.Player.Value, Data = context.Data.As<JsonNode>(), IsCreator = context.IsCreator });

    protected override Task<JoinResult> OnRejoin(JoinContext context) =>
        JoinAsync(new RulesInput("rejoin") { Player = context.Player.Value, Data = context.Data.As<JsonNode>() });

    protected override Task OnPresenceChanged(PlayerId player, PresenceStatus status) =>
        ForwardPresence
            ? DecideAsync(new RulesInput("presence") { Player = player.Value, Status = Presence(status) })
            : Task.CompletedTask;

    protected override Task OnPlayerLeft(PlayerId player, LeaveReason reason) =>
        DecideAsync(new RulesInput("left")
        {
            Player = player.Value,
            Reason = reason == LeaveReason.GraceExpired ? "grace-expired" : "removed",
        });

    protected override Task OnDeadline(string name) => DecideAsync(new RulesInput("deadline") { Name = name });

    /// <summary>
    /// Sends the view the rules service made for this viewer (or for everyone else). <see cref="GetSnapshot"/> cannot
    /// call the service, so views come with each decision. A top-level <c>serverNow</c> field, if the view has one,
    /// is set to the time the snapshot is sent, so clients can correct their clocks.
    /// </summary>
    protected override object? GetSnapshot(PlayerId viewer)
    {
        if (!State.Views.TryGetValue(viewer.Value, out string? json) && !State.Views.TryGetValue(EveryoneElse, out json))
        {
            return null;
        }

        JsonNode? view = JsonNode.Parse(json);
        if (view is JsonObject obj && obj.ContainsKey("serverNow"))
        {
            obj["serverNow"] = Now;
        }

        return view;
    }

    private async Task<JoinResult> JoinAsync(RulesInput input)
    {
        RulesDecision decision = await DecideAsync(input, throwOnReject: false);
        return decision.Reject is { } reject ? JoinResult.Reject(reject.Reason) : JoinResult.Accept(decision.Role);
    }

    private async Task<RulesDecision> DecideAsync(RulesInput input, bool throwOnReject = true)
    {
        RulesDecision decision = await rules.DecideAsync(new RulesRequest(
            RoomType,
            RoomId,
            Now,
            State.Game is null ? null : JsonNode.Parse(State.Game),
            Members.Select(m => new RulesMember(m.Player.Value, m.Role, Presence(m.Presence))).ToList(),
            input));

        if (decision.Reject is { } reject)
        {
            if (throwOnReject)
            {
                Reject(reject.Reason, reject.Code);
            }

            return decision;
        }

        Apply(decision);
        return decision;
    }

    private void Apply(RulesDecision decision)
    {
        State.Game = decision.State?.ToJsonString();
        if (decision.Views is not null)
        {
            State.Views = decision.Views
                .Where(v => v.Value is not null)
                .ToDictionary(v => v.Key, v => v.Value!.ToJsonString());
        }

        foreach (RulesEffect effect in decision.Effects ?? [])
        {
            switch (effect.Kind)
            {
                case "broadcast":
                    Broadcast(Required(effect.Name, effect), effect.Data, effect.Except is null ? null : new PlayerId(effect.Except));
                    break;
                case "sendTo":
                    SendTo(new PlayerId(Required(effect.Player, effect)), Required(effect.Name, effect), effect.Data);
                    break;
                case "setRole":
                    SetRole(new PlayerId(Required(effect.Player, effect)), effect.Role);
                    break;
                case "kick":
                    Kick(new PlayerId(Required(effect.Player, effect)), effect.Reason);
                    break;
                case "removeMember":
                    RemoveMember(new PlayerId(Required(effect.Player, effect)));
                    break;
                case "schedule":
                    Schedule(Required(effect.Name, effect), effect.At ?? throw Invalid(effect));
                    break;
                case "cancelSchedule":
                    CancelSchedule(Required(effect.Name, effect));
                    break;
                case "close":
                    CloseRoom(effect.Reason);
                    break;
                default:
                    // Throwing undoes the whole operation: better than applying half of a decision.
                    throw Invalid(effect);
            }
        }
    }

    private static string Required(string? value, RulesEffect effect) => value ?? throw Invalid(effect);

    private static RulesServiceException Invalid(RulesEffect effect) =>
        new($"The rules service sent an invalid effect: {effect}");

    private static string Presence(PresenceStatus status) => status switch
    {
        PresenceStatus.Online => "online",
        PresenceStatus.Reconnecting => "reconnecting",
        _ => "left",
    };
}

/// <summary>
/// What the grain stores for an external room: the rules service's state and views, kept as JSON text.
/// </summary>
[GenerateSerializer]
public sealed class ExternalRoomState
{
    /// <summary>The state the rules service returned last, or null before the room was created.</summary>
    [Id(0)] public string? Game { get; set; }

    /// <summary>The snapshot views, keyed by player id, with <see cref="ExternalRoomGrain.EveryoneElse"/> for everyone else.</summary>
    [Id(1)] public Dictionary<string, string> Views { get; set; } = [];
}
