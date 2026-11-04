namespace PitchWise.Engine;

/// <summary>
/// The ball after filtering: position and velocity, never a raw detection.
/// </summary>
/// <param name="Interpolated">True when this frame had no accepted measurement and the state
/// is pure prediction (ball occluded, missed, or the detection was gated out as impossible).</param>
/// <param name="Confidence">Decays while coasting; reaches 0 once the ball has been unmeasured
/// for longer than <see cref="EngineConfig.MaxCoastSeconds"/>. Rules must not trust a 0.</param>
/// <remarks>There is no Z. A planar homography maps ground plane to ground plane, so an
/// airborne ball is projected as if it were on the grass. Height, spin and landing point are
/// not recoverable from a single camera and are deliberately absent rather than faked.</remarks>
public readonly record struct BallState(
    double X,
    double Y,
    double Vx,
    double Vy,
    double Speed,
    bool Interpolated,
    double Confidence);

/// <param name="DistanceToBall">Precomputed for the rules; same units as X/Y.</param>
public readonly record struct PlayerState(
    int PlayerId,
    TeamId Team,
    PlayerRole Role,
    double X,
    double Y,
    double DistanceToBall);

public enum PossessionState
{
    /// <summary>Nobody is in control — the ball is travelling, or genuinely lost.</summary>
    Loose,
    TeamAControlled,
    TeamBControlled,
    /// <summary>Two opponents equally close: a duel, with no single owner.</summary>
    Contested,
}

/// <param name="PossessingPlayerId">Null when <see cref="Possession"/> is Loose or Contested.</param>
/// <param name="PossessionDwellSeconds">How long the possessing TEAM has held continuous control.
/// A pass between team-mates does not reset it; a turnover does.</param>
public sealed record FootballContext(
    PossessionState Possession,
    int? PossessingPlayerId,
    TeamId PossessingTeam,
    double PossessionDwellSeconds)
{
    public static readonly FootballContext Empty =
        new(PossessionState.Loose, null, TeamId.Unknown, 0.0);
}

/// <summary>
/// The whole match, as of one frame. This — not the pixels — is what football rules read,
/// which is why swapping the detector leaves the rules untouched.
/// </summary>
/// <param name="Reliable">False when positions are normalized (no homography), so metre-based
/// thresholds do not apply. Rules that depend on distance must return nothing.</param>
public sealed record WorldState(
    int FrameIndex,
    double TimestampSeconds,
    BallState Ball,
    IReadOnlyList<PlayerState> Players,
    FootballContext Context,
    bool Reliable)
{
    /// <summary>The player currently in possession, or null.</summary>
    public PlayerState? Owner
    {
        get
        {
            if (Context.PossessingPlayerId is not int pid) return null;
            foreach (PlayerState p in Players)
                if (p.PlayerId == pid) return p;
            return null;
        }
    }
}
