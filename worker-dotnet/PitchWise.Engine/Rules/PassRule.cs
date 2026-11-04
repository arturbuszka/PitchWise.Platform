namespace PitchWise.Engine.Rules;

/// <summary>
/// Reads a change of possession as a football action: the previous owner released the ball, it
/// travelled, and someone received it.
///
/// <code>
///   owner A holds  ->  owner changes to B  ->  ball moved >= PassMinDistance
///        within PassMaxFlightSeconds
///            B on A's team  =>  pass
///            B on the other =>  wayward_pass  (a turnover)
/// </code>
///
/// This is the rule the whole engine was built to make trivial, and it is trivial only because
/// <see cref="PossessionTracker"/> already answered the hard question — <i>who had the ball</i>,
/// as opposed to who happened to be nearest to it on one frame.
///
/// Only <c>wayward_pass</c> is persistable (see <see cref="GameEventType.PersistableTypes"/>);
/// a completed pass has no database counterpart and exists as an internal signal.
///
/// <b>Known limit.</b> At a 5-frame stride (0.2 s per sample) a one-touch pass can fall between
/// samples: the ball never registers inside the receiver's control radius on any sampled frame,
/// and no possession change is seen. Pass recall is bounded by the stride, not by this rule.
/// </summary>
public sealed class PassRule : IGameRule
{
    private readonly EngineConfig _cfg;

    public PassRule(EngineConfig? cfg = null) => _cfg = cfg ?? new EngineConfig();

    public string Name => nameof(PassRule);

    /// <summary>Who last held the ball, and where the ball was on the last frame they still
    /// controlled it. Null until the first possession of the match.</summary>
    private int? _lastOwnerId;
    private TeamId _lastOwnerTeam;
    private double _releaseX, _releaseY;
    private double _releaseTime;

    public IEnumerable<GameEvent> OnFrame(WorldState state)
    {
        if (!state.Reliable) yield break;

        int? owner = state.Context.PossessingPlayerId;

        // Loose or contested: the ball is in flight. The release point was already captured on
        // the last frame the previous owner held it — deliberately NOT updated here, since the
        // ball keeps moving and the release point must stay where control was lost.
        if (owner is not int currentOwner) yield break;

        // Same player still on the ball. Pin the release point to the last frame the ball was
        // actually AT THEIR FEET — not merely the last frame they nominally owned it. Possession
        // is sticky by design (ReleaseGrace keeps the owner for 0.6s after the ball leaves), so
        // tracking the ball through the grace window would drag the origin along with the pass
        // and shrink its measured length to nothing.
        if (currentOwner == _lastOwnerId)
        {
            if (BallAtOwnersFeet(state))
            {
                _releaseX = state.Ball.X;
                _releaseY = state.Ball.Y;
                _releaseTime = state.TimestampSeconds;
            }
            yield break;
        }

        int? previousOwner = _lastOwnerId;
        TeamId previousTeam = _lastOwnerTeam;
        double releaseTime = _releaseTime;
        double releaseX = _releaseX, releaseY = _releaseY;

        _lastOwnerId = currentOwner;
        _lastOwnerTeam = state.Context.PossessingTeam;
        _releaseX = state.Ball.X;
        _releaseY = state.Ball.Y;
        _releaseTime = state.TimestampSeconds;

        // First possession of the match: no prior owner, so no pass.
        if (previousOwner is null) yield break;

        double flight = state.TimestampSeconds - releaseTime;
        if (flight > _cfg.PassMaxFlightSeconds) yield break;

        // The ball has to have gone somewhere. A short hop is a tackle, a deflection, or two
        // players scrapping over it at their feet — not a pass. This distance test, not the
        // presence of a "loose" frame, is what separates the two: whether the engine happens to
        // sample a loose frame between two owners depends on the stride and on ReleaseGrace,
        // and must never decide whether an event exists.
        double dx = state.Ball.X - releaseX;
        double dy = state.Ball.Y - releaseY;
        double travelled = Math.Sqrt(dx * dx + dy * dy);
        if (travelled < _cfg.PassMinDistance) yield break;

        TeamId receiverTeam = state.Context.PossessingTeam;
        bool sameTeam = previousTeam != TeamId.Unknown && receiverTeam == previousTeam;

        // Confidence tracks how well we actually saw the ball across the flight. A trajectory
        // the filter mostly coasted through is a guess, and says so.
        double confidence = Math.Clamp(state.Ball.Confidence, 0.0, 1.0);

        yield return sameTeam
            ? new GameEvent(
                GameEventType.Pass,
                state.TimestampSeconds,
                confidence,
                $"pass: player {previousOwner} -> {currentOwner}, {travelled:F1} m in {flight:F2} s",
                previousOwner,
                previousTeam)
            : new GameEvent(
                GameEventType.WaywardPass,
                state.TimestampSeconds,
                confidence,
                $"turnover: player {previousOwner} ({previousTeam}) -> {currentOwner} ({receiverTeam}), {travelled:F1} m",
                previousOwner,
                previousTeam);
    }

    /// <summary>True when the ball is close enough to the owner to be under their control, as
    /// opposed to already travelling while the grace period still credits them with possession.</summary>
    private bool BallAtOwnersFeet(WorldState state) =>
        state.Owner is PlayerState o && o.DistanceToBall <= _cfg.ControlRadius;
}
