using PitchWise.Engine;

namespace PitchWise.Vision;

/// <summary>
/// The bridge from vision to engine: turns a <see cref="FrameResult"/> of pixel boxes into a
/// <see cref="FrameObservation"/> of pitch positions. This is the last place pixels exist.
///
/// Lives in the vision layer because it needs the <see cref="Homography"/>, which is a property
/// of the camera, not of football.
/// </summary>
public static class ObservationMapper
{
    /// <param name="teams">Index-aligned with <c>frame.Detections</c>; from
    /// <see cref="TeamColorClassifier.Classify"/>. Pass an empty list to leave everyone
    /// <see cref="TeamId.Unknown"/>, which suppresses possession in the engine.</param>
    /// <param name="homography">Null falls back to normalized [0,1] coordinates and marks the
    /// observation unreliable — the engine will then refuse to emit distance-based events rather
    /// than apply metre thresholds to pixel fractions.</param>
    public static FrameObservation ToObservation(
        FrameResult frame,
        IReadOnlyList<TeamId> teams,
        Homography? homography,
        int frameWidth,
        int frameHeight)
    {
        var players = new List<PlayerObservation>(frame.Detections.Count);
        BallObservation ball = BallObservation.Missing;
        double bestBallConf = double.NegativeInfinity;

        for (int i = 0; i < frame.Detections.Count; i++)
        {
            Detection d = frame.Detections[i];

            if (d.Cls == ObjectClass.Ball)
            {
                if (d.Confidence <= bestBallConf) continue;
                bestBallConf = d.Confidence;
                // The ball's contact point is its centre, not the bottom of its box; projecting
                // the box bottom would place it a ball-radius further from the camera. Both are
                // wrong the moment it leaves the ground — a planar homography has no height.
                (double bx, double by) = Project(
                    homography, d.X1, d.Y1, d.X2, d.Y2, frameWidth, frameHeight, useCenter: true);
                ball = new BallObservation(bx, by, d.Confidence, Detected: true);
                continue;
            }

            PlayerRole? role = d.Cls switch
            {
                ObjectClass.Player => PlayerRole.Player,
                ObjectClass.Goalkeeper => PlayerRole.Goalkeeper,
                ObjectClass.Referee => PlayerRole.Referee,
                _ => null,   // goalposts and anything else are not people
            };
            if (role is not PlayerRole r) continue;

            (double px, double py) = Project(
                homography, d.X1, d.Y1, d.X2, d.Y2, frameWidth, frameHeight, useCenter: false);

            players.Add(new PlayerObservation(
                PlayerId: IdentityOf(d),
                Role: r,
                Team: i < teams.Count ? teams[i] : TeamId.Unknown,
                X: px,
                Y: py,
                Confidence: d.Confidence));
        }

        return new FrameObservation(
            frame.FrameIndex,
            frame.TimestampSeconds,
            players,
            ball,
            NormalizedCoords: homography is null);
    }

    /// <summary>
    /// The identity the engine will track a player by.
    ///
    /// Prefer Re-ID's stable <see cref="Detection.PlayerId"/>, which survives occlusions and
    /// shot cuts. But on broadcast footage Re-ID names only a small minority of detections
    /// (measured: ~8% on a 40s clip), while ByteTrack gives a <see cref="Detection.TrackId"/>
    /// to nearly all of them. Falling back to the track id keeps possession working through a
    /// single passage of play, which is all a pass or a duel spans.
    ///
    /// See <see cref="PlayerIdentity"/> for how the two kinds of identity are kept apart.
    /// </summary>
    private static int IdentityOf(Detection d) => d.PlayerId switch
    {
        int stable => stable,
        _ => d.TrackId is int track ? PlayerIdentity.FromTrack(track) : PlayerIdentity.None,
    };

    /// <param name="useCenter">Players stand on the pitch, so their foot point (bottom-centre of
    /// the box) is the point that lies on the ground plane. A ball's centre is the better
    /// estimate of where it touches.</param>
    private static (double x, double y) Project(
        Homography? h, double x1, double y1, double x2, double y2,
        int frameWidth, int frameHeight, bool useCenter)
    {
        // Homography.Project takes the foot point. For the ball, hand it a degenerate box whose
        // "foot" is the ball's centre.
        double by = useCenter ? (y1 + y2) / 2.0 : y2;

        if (h is not null) return h.Project(x1, by, x2, by);
        return Homography.PixelToNormalized(x1, by, x2, by, frameWidth, frameHeight);
    }
}
