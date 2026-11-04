namespace PitchWise.Engine;

/// <summary>The two sides. <see cref="Unknown"/> covers referees, unresolved players, and
/// everyone before the team-colour clustering has settled.</summary>
public enum TeamId
{
    Unknown = 0,
    A = 1,
    B = 2,
}

public enum PlayerRole
{
    Player,
    Goalkeeper,
    Referee,
}

/// <summary>
/// Conventions for <see cref="PlayerObservation.PlayerId"/>.
///
/// The engine does not care where an identity comes from — only that the same number means the
/// same person from frame to frame. The vision layer may supply a match-long Re-ID identity, or,
/// where Re-ID has not named a player, a shorter-lived tracker identity. Those live in disjoint
/// number ranges so they can never be confused for one another.
/// </summary>
public static class PlayerIdentity
{
    /// <summary>No identity at all. Every anonymous detection would share this value, so the
    /// engine refuses to attribute possession to it.</summary>
    public const int None = -1;

    /// <summary>Wraps a short-lived tracker id into the negative range reserved for them, clear
    /// of any stable identity (which is always >= 0). Such an identity does not survive a
    /// re-track — which surfaces as a possession change, the honest reading, because we genuinely
    /// no longer know it is the same player.</summary>
    public static int FromTrack(int trackId) => -2 - trackId;

    /// <summary>True for identities that persist across occlusions and shot cuts.</summary>
    public static bool IsStable(int playerId) => playerId >= 0;
}

/// <summary>One tracked person on the pitch, already projected out of pixel space.</summary>
/// <param name="PlayerId">Who this is, per <see cref="PlayerIdentity"/>.</param>
/// <param name="Team">Resolved by the vision layer (jersey colour), which is the only place
/// pixels exist. <see cref="TeamId.Unknown"/> until clustering settles.</param>
/// <param name="X">Pitch coordinate. Metres when the frame carries a homography, otherwise
/// normalized to [0,1] — see <see cref="FrameObservation.NormalizedCoords"/>.</param>
public readonly record struct PlayerObservation(
    int PlayerId,
    PlayerRole Role,
    TeamId Team,
    double X,
    double Y,
    double Confidence);

/// <param name="Detected">False when no ball was found this frame. The filter then coasts on
/// its prediction rather than treating (0,0) as a measurement.</param>
public readonly record struct BallObservation(
    double X,
    double Y,
    double Confidence,
    bool Detected)
{
    public static BallObservation Missing => new(0, 0, 0, false);
}

/// <summary>Everything the engine gets to know about one processed frame. Built by the vision
/// layer; the engine never sees a pixel.</summary>
/// <param name="NormalizedCoords">True when no homography was available and positions are
/// pixel fractions in [0,1] rather than metres. Distance thresholds are meaningless then, so
/// the engine suppresses possession-derived events instead of emitting garbage.</param>
public sealed record FrameObservation(
    int FrameIndex,
    double TimestampSeconds,
    IReadOnlyList<PlayerObservation> Players,
    BallObservation Ball,
    bool NormalizedCoords);
