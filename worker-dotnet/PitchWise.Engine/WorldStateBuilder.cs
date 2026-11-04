namespace PitchWise.Engine;

/// <summary>
/// Folds a stream of per-frame observations into the running match state. Stateful: feed frames
/// in ascending timestamp order, exactly once each — same contract as the vision layer's
/// <c>TimeOnPitchTracker</c>.
///
/// This is the seam the whole architecture exists for. Above it everything is pixels and models;
/// below it everything is football. Replacing the detector changes nothing below this line.
/// </summary>
public sealed class WorldStateBuilder
{
    private readonly EngineConfig _cfg;
    private readonly BallFilter _ball;
    private readonly PossessionTracker _possession;

    public WorldStateBuilder(EngineConfig? cfg = null)
    {
        _cfg = cfg ?? new EngineConfig();
        _ball = new BallFilter(_cfg);
        _possession = new PossessionTracker(_cfg);
    }

    public WorldState Add(FrameObservation obs)
    {
        BallState ball = _ball.Step(obs.Ball, obs.TimestampSeconds);

        var players = new List<PlayerState>(obs.Players.Count);
        foreach (PlayerObservation p in obs.Players)
        {
            double dx = p.X - ball.X;
            double dy = p.Y - ball.Y;
            double distance = ball.Confidence > 0 ? Math.Sqrt(dx * dx + dy * dy) : double.PositiveInfinity;
            players.Add(new PlayerState(p.PlayerId, p.Team, p.Role, p.X, p.Y, distance));
        }

        // Without a homography, distances are pixel fractions and every threshold in
        // EngineConfig is nonsense. Run the filter (its state stays coherent in whatever units
        // it is fed) but do not pretend to know who has the ball.
        FootballContext context = obs.NormalizedCoords
            ? FootballContext.Empty
            : _possession.Step(ball, players, obs.TimestampSeconds);

        return new WorldState(
            obs.FrameIndex,
            obs.TimestampSeconds,
            ball,
            players,
            context,
            Reliable: !obs.NormalizedCoords);
    }
}
