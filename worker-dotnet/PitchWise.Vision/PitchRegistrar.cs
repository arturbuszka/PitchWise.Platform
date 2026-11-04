using PitchWise.Engine;

namespace PitchWise.Vision;

/// <summary>
/// Turns a frame's detected pitch keypoints into a <see cref="Homography"/>, with temporal
/// stabilisation.
///
/// The per-frame fit is noisy: a keypoint model jitters, and a broadcast camera occasionally
/// produces a frame with too few visible lines (a close-up, a replay wipe). Feeding that jitter
/// straight into the engine would make every player's metre position twitch and would wreck the
/// ball's velocity estimate in <c>BallFilter</c>. So this class:
///
/// <list type="number">
/// <item>keeps only keypoints above a confidence floor and pairs them with the fixed
///       <see cref="PitchModel"/> template;</item>
/// <item>fits a homography with <see cref="Homography.FromPoints"/> (which RANSACs internally)
///       only when at least 4 confident points survive;</item>
/// <item><b>rejects a fit that jumps</b> — if the new homography moves the projected pitch
///       corners more than a threshold from the last accepted one, it is treated as a bad frame
///       and the previous homography is kept. A real pan moves the corners smoothly; a spurious
///       fit teleports them.</item>
/// </list>
///
/// The last good homography is held and returned on frames that fail, so brief keypoint dropouts
/// coast on the previous camera pose rather than dropping the whole frame to normalized mode.
/// After too long with no accepted fit, it gives up and returns null (the camera has genuinely
/// cut away), and the engine falls back to normalized coordinates.
/// </summary>
public sealed class PitchRegistrar
{
    public sealed record Options
    {
        /// <summary>Keypoints below this visibility/confidence are not trusted as correspondences.</summary>
        public double MinKeypointConfidence { get; init; } = 0.5;

        /// <summary>Fewer than this many confident keypoints -> no fit this frame.</summary>
        public int MinPoints { get; init; } = 4;

        /// <summary>Max allowed movement (metres) of any projected image corner between two
        /// consecutive accepted homographies. A pan stays well under this per frame; a bad fit
        /// blows past it. Generous by default — this is a sanity gate, not a smoother.</summary>
        public double MaxCornerJumpMeters { get; init; } = 25.0;

        /// <summary>Give up on the held homography after this many consecutive rejected/empty
        /// frames; the camera has cut away and coasting is no longer honest.</summary>
        public int MaxHeldFrames { get; init; } = 8;
    }

    private readonly Options _opts;
    private readonly int _frameWidth;
    private readonly int _frameHeight;

    private Homography? _last;
    private (double x, double y)[]? _lastCorners;   // where _last projects the image corners
    private int _heldFor;

    public PitchRegistrar(int frameWidth, int frameHeight, Options? options = null)
    {
        _opts = options ?? new Options();
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
    }

    /// <summary>The homography accepted for the current frame, or null if none is available yet
    /// (start of stream) or the camera has cut away for too long.</summary>
    public Homography? Current => _last;

    /// <summary>Folds one frame's keypoints in and returns the homography to use for it.</summary>
    public Homography? Update(IReadOnlyList<PitchKeypointDetector.Keypoint> keypoints)
    {
        var pixel = new List<(double, double)>(keypoints.Count);
        var pitch = new List<(double, double)>(keypoints.Count);
        int n = Math.Min(keypoints.Count, PitchModel.Count);
        for (int i = 0; i < n; i++)
        {
            PitchKeypointDetector.Keypoint kp = keypoints[i];
            if (kp.Confidence < _opts.MinKeypointConfidence) continue;
            // A keypoint the model placed off-frame is a hallucination, not a correspondence.
            if (kp.X < 0 || kp.Y < 0 || kp.X > _frameWidth || kp.Y > _frameHeight) continue;
            pixel.Add((kp.X, kp.Y));
            pitch.Add(PitchModel.Keypoints[i]);
        }

        if (pixel.Count < _opts.MinPoints)
            return Hold();

        Homography candidate;
        try
        {
            candidate = Homography.FromPoints(pixel, pitch);
        }
        catch (ArgumentException)
        {
            // Degenerate correspondences (collinear points, RANSAC failure).
            return Hold();
        }

        (double x, double y)[] corners = ProjectCorners(candidate);

        // First accepted fit, or a fit that hasn't teleported the corners: accept it.
        if (_lastCorners is null || CornerJump(corners, _lastCorners) <= _opts.MaxCornerJumpMeters)
        {
            _last = candidate;
            _lastCorners = corners;
            _heldFor = 0;
            return _last;
        }

        // The fit jumped. Prefer the held homography — but if we've been holding too long, the
        // "jump" is really the camera having moved a lot while we had nothing; accept the new one.
        return Hold(fallbackCandidate: candidate, fallbackCorners: corners);
    }

    private Homography? Hold(Homography? fallbackCandidate = null, (double x, double y)[]? fallbackCorners = null)
    {
        _heldFor++;
        if (_heldFor <= _opts.MaxHeldFrames) return _last;

        // Held too long. If a fresh (if jumpy) candidate exists, re-acquire on it; otherwise the
        // camera has cut away and we honestly have no pitch pose.
        if (fallbackCandidate is not null)
        {
            _last = fallbackCandidate;
            _lastCorners = fallbackCorners;
            _heldFor = 0;
            return _last;
        }
        _last = null;
        _lastCorners = null;
        return null;
    }

    /// <summary>Projects the four image corners to pitch metres — the probe for how far a new
    /// homography has moved the world.</summary>
    private (double x, double y)[] ProjectCorners(Homography h)
    {
        double w = _frameWidth, ht = _frameHeight;
        // Homography.Project uses the box's bottom-centre (footX=(x1+x2)/2, footY=y2). To project
        // an exact point (px,py), pass a degenerate box (px, _, px, py) so footX=px, footY=py.
        return new[]
        {
            h.Project(0, 0, 0, 0),      // top-left
            h.Project(w, 0, w, 0),      // top-right
            h.Project(0, ht, 0, ht),    // bottom-left
            h.Project(w, ht, w, ht),    // bottom-right
        };
    }

    private static double CornerJump((double x, double y)[] a, (double x, double y)[] b)
    {
        double max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double dx = a[i].x - b[i].x, dy = a[i].y - b[i].y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > max) max = d;
        }
        return max;
    }
}
