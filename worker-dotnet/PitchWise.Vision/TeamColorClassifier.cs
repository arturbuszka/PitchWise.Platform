using OpenCvSharp;
using PitchWise.Engine;

namespace PitchWise.Vision;

/// <summary>
/// Assigns each player to one of two teams by jersey colour.
///
/// This lives in the vision layer, not the engine, for one reason: it needs pixels, and the
/// engine is deliberately pixel-free. The engine receives the resulting <see cref="TeamId"/>
/// already decided.
///
/// Method: for every player crop, take a torso ROI and reduce it to a mean CIE Lab colour.
/// Cluster those descriptors into two teams (k-means, k=2) over a short seed window, then
/// <b>freeze the centroids</b> — a match's lighting drifts, and letting the centroids follow it
/// eventually lets one team's cluster wander into the other's.
///
/// Two choices worth stating:
///
/// <list type="bullet">
/// <item><b>Lab, not HSV.</b> Euclidean distance in Lab approximates perceptual colour
///       difference, so k=2 splits on "these are different kits". Hue is an angle: it wraps, and
///       it is meaningless at low saturation — exactly where white and grey kits live.</item>
/// <item><b>Torso, not the whole box.</b> A player's bounding box is mostly grass, shorts and
///       socks. The upper-middle of the box is the shirt.</item>
/// </list>
///
/// <b>Known limit.</b> k=2 fails on similar kits (white vs light grey) and under a lighting
/// change severe enough to move a kit further than half the inter-centroid distance. The two
/// chosen centroids are written into the JSONL dump header so this can be diagnosed from the
/// file rather than by squinting at video.
/// </summary>
public sealed class TeamColorClassifier
{
    /// <summary>A mean Lab colour of one player's torso.</summary>
    private readonly record struct Descriptor(double L, double A, double B)
    {
        public double DistanceTo(Descriptor o)
        {
            double dl = L - o.L, da = A - o.A, db = B - o.B;
            return Math.Sqrt(dl * dl + da * da + db * db);
        }
    }

    private readonly int _minSeedSamples;
    private readonly int _voteWindow;

    private readonly List<Descriptor> _seed = new();
    private Descriptor _centroidA;
    private Descriptor _centroidB;
    private bool _frozen;

    /// <summary>Recent per-frame votes for each stable PlayerId. A team is a property of a
    /// player over time, not of one crop: motion blur, occlusion and a turned back all corrupt
    /// a single frame's colour, and a majority over a short window is stable where a per-frame
    /// decision flickers.</summary>
    private readonly Dictionary<int, Queue<TeamId>> _votes = new();

    /// <param name="minSeedSamples">Cluster and freeze once this many torso descriptors have been
    /// collected. Counted in descriptors, not frames: broadcast footage opens on studio shots and
    /// crowd close-ups where there are no players at all, and a frame-based threshold would freeze
    /// the centroids on whatever happened to be on screen — or, worse, on nothing.</param>
    /// <param name="voteWindow">How many recent frames vote on a player's team.</param>
    public TeamColorClassifier(int minSeedSamples = 120, int voteWindow = 15)
    {
        _minSeedSamples = minSeedSamples;
        _voteWindow = voteWindow;
    }

    /// <summary>True once the two team colours are fixed. Until then every player is
    /// <see cref="TeamId.Unknown"/> and the engine will not attribute possession.</summary>
    public bool Ready => _frozen;

    /// <summary>The frozen team colours as "#rrggbb", for the dump header. Null until ready.</summary>
    public string? ColorA => _frozen ? LabToHex(_centroidA) : null;
    public string? ColorB => _frozen ? LabToHex(_centroidB) : null;

    /// <summary>
    /// Classifies one frame's detections. Returns a list index-aligned with
    /// <paramref name="detections"/>: outfield players get A or B, everyone else Unknown.
    /// </summary>
    /// <remarks>Goalkeepers and referees are excluded from clustering AND from classification —
    /// their kits are deliberately distinct from both teams, so forcing them into k=2 would drag
    /// a centroid. A goalkeeper's team can be inferred later from which half they defend.</remarks>
    public IReadOnlyList<TeamId> Classify(IReadOnlyList<Detection> detections, Mat frameBgr)
    {
        var teams = new TeamId[detections.Count];

        for (int i = 0; i < detections.Count; i++)
        {
            Detection d = detections[i];
            if (d.Cls != ObjectClass.Player)
            {
                teams[i] = TeamId.Unknown;
                continue;
            }

            Descriptor? desc = Describe(d, frameBgr);
            if (desc is not Descriptor descriptor)
            {
                teams[i] = TeamId.Unknown;
                continue;
            }

            if (!_frozen)
            {
                // Every visible player feeds the seed, whether or not Re-ID has named them:
                // the two kit colours are a property of the match, not of any one identity.
                _seed.Add(descriptor);
                teams[i] = TeamId.Unknown;
                continue;
            }

            TeamId thisFrame = Nearest(descriptor);
            // With a stable identity we can smooth over motion blur and turned backs. Without
            // one there is nothing to smooth over, so take the frame at face value rather than
            // discarding the player entirely.
            teams[i] = d.PlayerId is int pid ? Vote(pid, thisFrame) : thisFrame;
        }

        if (!_frozen && _seed.Count >= _minSeedSamples) Freeze();

        return teams;
    }

    /// <summary>Mean Lab colour of the shirt: the middle of the torso, kept deliberately tight.
    /// A bounding box is mostly grass — arms spread, legs apart, and the box corners are pitch.
    /// The tighter this window, the less the descriptor is a measure of how much grass a player
    /// is standing on. It is never fully clean, which is why k=2 splits on the difference between
    /// the two kits rather than on their absolute colours.</summary>
    private static Descriptor? Describe(Detection d, Mat frameBgr)
    {
        double w = d.X2 - d.X1, h = d.Y2 - d.Y1;
        if (w < 4 || h < 8) return null;   // too small to hold a readable shirt

        int x1 = (int)Math.Round(d.X1 + w * 0.30);
        int x2 = (int)Math.Round(d.X2 - w * 0.30);
        int y1 = (int)Math.Round(d.Y1 + h * 0.20);
        int y2 = (int)Math.Round(d.Y1 + h * 0.45);

        x1 = Math.Clamp(x1, 0, frameBgr.Width - 1);
        y1 = Math.Clamp(y1, 0, frameBgr.Height - 1);
        x2 = Math.Clamp(x2, 0, frameBgr.Width);
        y2 = Math.Clamp(y2, 0, frameBgr.Height);
        if (x2 - x1 < 2 || y2 - y1 < 2) return null;

        using var torso = new Mat(frameBgr, new Rect(x1, y1, x2 - x1, y2 - y1));
        using var lab = new Mat();
        Cv2.CvtColor(torso, lab, ColorConversionCodes.BGR2Lab);
        Scalar mean = Cv2.Mean(lab);
        return new Descriptor(mean.Val0, mean.Val1, mean.Val2);
    }

    /// <summary>k-means, k=2, on the seeded descriptors. Seeded with the two most distant
    /// samples so the split starts on the real colour axis rather than an arbitrary one.</summary>
    private void Freeze()
    {
        (_centroidA, _centroidB) = FarthestPair(_seed);

        for (int iter = 0; iter < 20; iter++)
        {
            double la = 0, aa = 0, ba = 0; int na = 0;
            double lb = 0, ab = 0, bb = 0; int nb = 0;

            foreach (Descriptor d in _seed)
            {
                if (d.DistanceTo(_centroidA) <= d.DistanceTo(_centroidB))
                { la += d.L; aa += d.A; ba += d.B; na++; }
                else
                { lb += d.L; ab += d.A; bb += d.B; nb++; }
            }
            // A degenerate split means the two kits are not separable in Lab. Keep the previous
            // centroids and stop; Nearest() will still assign, but the caller should treat the
            // colours in the dump header as suspect.
            if (na == 0 || nb == 0) break;

            var newA = new Descriptor(la / na, aa / na, ba / na);
            var newB = new Descriptor(lb / nb, ab / nb, bb / nb);
            bool converged = newA.DistanceTo(_centroidA) < 0.5 && newB.DistanceTo(_centroidB) < 0.5;
            _centroidA = newA;
            _centroidB = newB;
            if (converged) break;
        }

        // Deterministic labelling: the darker kit is always team A, so a re-run of the same
        // video yields the same team ids.
        if (_centroidA.L > _centroidB.L) (_centroidA, _centroidB) = (_centroidB, _centroidA);

        _frozen = true;
        _seed.Clear();
        _seed.TrimExcess();
    }

    private static (Descriptor, Descriptor) FarthestPair(List<Descriptor> xs)
    {
        // O(n^2) over the seed window only (a few hundred descriptors), once per run.
        Descriptor a = xs[0], b = xs[^1];
        double best = -1;
        for (int i = 0; i < xs.Count; i++)
            for (int j = i + 1; j < xs.Count; j++)
            {
                double d = xs[i].DistanceTo(xs[j]);
                if (d > best) { best = d; a = xs[i]; b = xs[j]; }
            }
        return (a, b);
    }

    private TeamId Nearest(Descriptor d) =>
        d.DistanceTo(_centroidA) <= d.DistanceTo(_centroidB) ? TeamId.A : TeamId.B;

    /// <summary>Majority vote over this player's recent frames.</summary>
    private TeamId Vote(int playerId, TeamId thisFrame)
    {
        if (!_votes.TryGetValue(playerId, out Queue<TeamId>? q))
            _votes[playerId] = q = new Queue<TeamId>(_voteWindow);

        q.Enqueue(thisFrame);
        while (q.Count > _voteWindow) q.Dequeue();

        int a = 0, b = 0;
        foreach (TeamId t in q) { if (t == TeamId.A) a++; else if (t == TeamId.B) b++; }
        return a == b ? thisFrame : (a > b ? TeamId.A : TeamId.B);
    }

    /// <summary>Lab (OpenCV 8-bit encoding) back to an sRGB hex string, for the dump header.</summary>
    private static string LabToHex(Descriptor d)
    {
        using var lab = new Mat(1, 1, MatType.CV_8UC3,
            new Scalar(Math.Clamp(d.L, 0, 255), Math.Clamp(d.A, 0, 255), Math.Clamp(d.B, 0, 255)));
        using var bgr = new Mat();
        Cv2.CvtColor(lab, bgr, ColorConversionCodes.Lab2BGR);
        Vec3b p = bgr.At<Vec3b>(0, 0);
        return $"#{p.Item2:x2}{p.Item1:x2}{p.Item0:x2}";
    }
}
