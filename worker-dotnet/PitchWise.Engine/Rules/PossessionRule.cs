namespace PitchWise.Engine.Rules;

/// <summary>
/// Emits an internal <c>possession_change</c> whenever the ball changes owner.
///
/// It persists nothing. Its purpose is diagnostic: replayed against a recorded
/// <c>world_state.jsonl</c>, the density of these events tells you immediately whether
/// <see cref="PossessionTracker"/>'s hysteresis is doing its job. A real match yields a few
/// hundred; a flickering tracker yields thousands, and every rule built on possession is then
/// worthless. Tune <see cref="EngineConfig.CaptureDwell"/> / <see cref="EngineConfig.ReleaseGrace"/>
/// until this number looks like football.
/// </summary>
public sealed class PossessionRule : IGameRule
{
    public string Name => nameof(PossessionRule);

    private int? _lastOwner;
    private bool _seenFirstFrame;

    public IEnumerable<GameEvent> OnFrame(WorldState state)
    {
        if (!state.Reliable) yield break;

        int? owner = state.Context.PossessingPlayerId;

        // The opening frame establishes a baseline; it is not a change.
        if (!_seenFirstFrame)
        {
            _seenFirstFrame = true;
            _lastOwner = owner;
            yield break;
        }

        if (owner == _lastOwner) yield break;
        _lastOwner = owner;

        // Losing the ball to nobody (loose / contested) is not a change of possession.
        if (owner is not int newOwner) yield break;

        yield return new GameEvent(
            GameEventType.PossessionChange,
            state.TimestampSeconds,
            Confidence: state.Ball.Confidence,
            Label: $"possession -> player {newOwner} ({state.Context.PossessingTeam})",
            PlayerId: newOwner,
            Team: state.Context.PossessingTeam);
    }
}
