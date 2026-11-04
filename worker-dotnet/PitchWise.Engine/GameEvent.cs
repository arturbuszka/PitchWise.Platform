namespace PitchWise.Engine;

/// <summary>Canonical event type strings. The subset that the API database understands is
/// listed in <c>EventTypeMap.FromDb</c>; anything else it silently coerces to "manual", which
/// would mislabel engine output as a coach's hand entry. <see cref="PersistableTypes"/> is the
/// gate — the vision layer must not forward an event outside it.</summary>
public static class GameEventType
{
    public const string Goal = "goal";
    public const string Shot = "shot";
    /// <summary>Possession handed to the opposition. The API renders this as "Turnover".</summary>
    public const string WaywardPass = "wayward_pass";

    /// <summary>A completed pass between team-mates. Has no database counterpart — it exists as
    /// an internal signal for statistics and for rules built on top of it. Do not persist.</summary>
    public const string Pass = "pass";

    /// <summary>Possession changed hands, whatever the mechanism. Internal, not persisted.</summary>
    public const string PossessionChange = "possession_change";

    /// <summary>Types the API's EventTypeMap.FromDb maps to a real EventType.</summary>
    public static readonly IReadOnlySet<string> PersistableTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Goal, Shot, WaywardPass,
            "foul", "free_kick", "offside", "substitution", "set_piece",
        };
}

/// <param name="Type">One of <see cref="GameEventType"/>.</param>
/// <param name="PlayerId">Who caused it, when the rule can attribute it.</param>
public readonly record struct GameEvent(
    string Type,
    double TimestampSeconds,
    double Confidence,
    string? Label = null,
    int? PlayerId = null,
    TeamId Team = TeamId.Unknown);

/// <summary>
/// One piece of football knowledge, reading only <see cref="WorldState"/> — never pixels.
///
/// Rules are <b>stateful</b>. A pass spans the frames between one player releasing the ball and
/// another receiving it; a rule must remember what it saw, refuse to re-emit, and hold its own
/// cooldown. Hence <see cref="OnFrame"/> per frame rather than a pure function over a single
/// state: a stateless <c>Evaluate(WorldState)</c> cannot express any event that takes time.
/// </summary>
public interface IGameRule
{
    string Name { get; }

    /// <summary>Called once per frame in ascending timestamp order. Returns events finalised by
    /// this frame — usually none.</summary>
    IEnumerable<GameEvent> OnFrame(WorldState state);

    /// <summary>End of stream: emit anything still awaiting confirmation.</summary>
    IEnumerable<GameEvent> Flush() => Array.Empty<GameEvent>();
}
