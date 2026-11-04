namespace PitchWise.Vision;

/// <summary>
/// Maps volatile ByteTrack track ids onto <b>stable, match-long player identities</b>
/// (<see cref="Detection.PlayerId"/>). ByteTrack.NET is purely motion-based: after an
/// occlusion, a collision, or a player leaving and re-entering the frame it drops the
/// track and re-issues a fresh <see cref="Detection.TrackId"/>. This component keeps an
/// appearance gallery (OSNet embeddings from <see cref="OsNetOnnxEmbedder"/>) and, when a
/// new track id appears, tries to recover the player it belongs to.
///
/// Football-specific caveat: team-mates wear near-identical kits, so appearance alone is
/// ambiguous. Recovery therefore requires BOTH appearance similarity above a threshold AND
/// spatial plausibility (the candidate identity must have last been seen close enough given
/// the elapsed gap) — this position gating is what stops two different players in the same
/// kit from being merged.
///
/// Stateful: call <see cref="Assign"/> once per processed frame, in ascending frame order.
/// </summary>
public sealed class PlayerReId
{
    /// <summary>Tuning knobs; defaults are a reasonable starting point, to be validated on
    /// real footage (see the plan's verification section).</summary>
    public sealed class Options
    {
        /// <summary>Min cosine similarity to accept an appearance match (vectors are L2-normed
        /// so cosine == dot product). Higher = stricter, fewer wrong merges.</summary>
        public double SimilarityThreshold { get; init; } = 0.60;

        /// <summary>Only identities lost within this many frames are recovery candidates.
        /// Beyond it we assume the player is genuinely gone and start a new identity.</summary>
        public int MaxLostFrames { get; init; } = 90;

        /// <summary>Max plausible travel speed (pixels per processed frame) used for the
        /// position gate. A candidate is spatially plausible if the distance from its last
        /// known center is within SpeedPxPerFrame * gapFrames (+ a slack radius).</summary>
        public double SpeedPxPerFrame { get; init; } = 60.0;

        /// <summary>Additive slack (pixels) on the position gate for detection jitter.</summary>
        public double PositionSlackPx { get; init; } = 80.0;

        /// <summary>EMA weight for the NEW embedding when updating a gallery identity
        /// (0..1). Lower = more stable/smoothed appearance model.</summary>
        public double EmaAlpha { get; init; } = 0.3;
    }

    /// <summary>One stable player identity in the gallery.</summary>
    private sealed class Identity
    {
        public int PlayerId;
        public float[] Embedding = Array.Empty<float>();   // EMA-smoothed, L2-normalized
        public double CenterX;
        public double CenterY;
        public int LastSeenFrame;
        public bool ActiveThisFrame;                        // has a live track this frame
    }

    private readonly Options _opts;
    private readonly List<Identity> _identities = new();
    // Live mapping: ByteTrack track id -> our stable player id (for ids currently running).
    private readonly Dictionary<int, int> _trackToPlayer = new();
    private int _nextPlayerId = 1;

    public PlayerReId(Options? options = null) => _opts = options ?? new Options();

    /// <summary>
    /// Assigns a stable <see cref="Detection.PlayerId"/> to every player-like detection in
    /// <paramref name="detections"/> and returns a new list with those ids filled in.
    /// <paramref name="embeddings"/> must be index-aligned with <paramref name="detections"/>
    /// (one L2-normalized vector per detection; an empty/zero vector means "no embedding" —
    /// e.g. a degenerate crop — and disables appearance recovery for that detection).
    /// Non-player detections (ball, referee) pass through unchanged.
    /// </summary>
    public IReadOnlyList<Detection> Assign(
        IReadOnlyList<Detection> detections,
        IReadOnlyList<float[]> embeddings,
        int frameIndex)
    {
        foreach (var id in _identities) id.ActiveThisFrame = false;

        var result = new List<Detection>(detections.Count);
        for (int i = 0; i < detections.Count; i++)
        {
            Detection d = detections[i];
            if (!IsPlayerLike(d.Cls) || d.TrackId is not int trackId)
            {
                result.Add(d);
                continue;
            }

            float[] emb = i < embeddings.Count ? embeddings[i] : Array.Empty<float>();
            int playerId = Resolve(trackId, emb, d.CenterX, d.CenterY, frameIndex);
            result.Add(d with { PlayerId = playerId });
        }

        return result;
    }

    // Only players and goalkeepers carry a stable identity for time-on-pitch.
    private static bool IsPlayerLike(string cls) =>
        cls == ObjectClass.Player || cls == ObjectClass.Goalkeeper;

    /// <summary>Resolves the stable player id for one live track and updates the gallery.</summary>
    private int Resolve(int trackId, float[] emb, double cx, double cy, int frameIndex)
    {
        // Case 1: this track id is already bound to an identity (ByteTrack still leading it).
        if (_trackToPlayer.TryGetValue(trackId, out int boundPlayerId))
        {
            Identity? bound = FindIdentity(boundPlayerId);
            if (bound is not null)
            {
                UpdateIdentity(bound, emb, cx, cy, frameIndex);
                return boundPlayerId;
            }
            // Identity vanished (shouldn't happen) — fall through and re-create.
            _trackToPlayer.Remove(trackId);
        }

        // Case 2: a NEW track id. Try to recover a lost identity by appearance + position.
        Identity? best = null;
        double bestSim = _opts.SimilarityThreshold;
        if (emb.Length > 0)
        {
            foreach (Identity id in _identities)
            {
                if (id.ActiveThisFrame) continue;                 // already claimed by a live track
                if (id.Embedding.Length == 0) continue;
                int gap = frameIndex - id.LastSeenFrame;
                if (gap <= 0 || gap > _opts.MaxLostFrames) continue;

                // Position gate: reject if the player couldn't have travelled this far.
                double maxDist = _opts.SpeedPxPerFrame * gap + _opts.PositionSlackPx;
                if (Distance(cx, cy, id.CenterX, id.CenterY) > maxDist) continue;

                double sim = Cosine(emb, id.Embedding);
                if (sim > bestSim) { bestSim = sim; best = id; }
            }
        }

        if (best is not null)
        {
            _trackToPlayer[trackId] = best.PlayerId;
            UpdateIdentity(best, emb, cx, cy, frameIndex);
            return best.PlayerId;
        }

        // Case 3: genuinely new player.
        var created = new Identity
        {
            PlayerId = _nextPlayerId++,
            Embedding = emb.Length > 0 ? (float[])emb.Clone() : Array.Empty<float>(),
            CenterX = cx,
            CenterY = cy,
            LastSeenFrame = frameIndex,
            ActiveThisFrame = true,
        };
        _identities.Add(created);
        _trackToPlayer[trackId] = created.PlayerId;
        return created.PlayerId;
    }

    private Identity? FindIdentity(int playerId)
    {
        foreach (Identity id in _identities)
            if (id.PlayerId == playerId) return id;
        return null;
    }

    private void UpdateIdentity(Identity id, float[] emb, double cx, double cy, int frameIndex)
    {
        id.CenterX = cx;
        id.CenterY = cy;
        id.LastSeenFrame = frameIndex;
        id.ActiveThisFrame = true;
        if (emb.Length == 0) return;

        if (id.Embedding.Length != emb.Length)
        {
            id.Embedding = (float[])emb.Clone();
            return;
        }
        // EMA blend then re-normalize so cosine stays a dot product.
        double a = _opts.EmaAlpha;
        for (int c = 0; c < emb.Length; c++)
            id.Embedding[c] = (float)((1 - a) * id.Embedding[c] + a * emb[c]);
        Normalize(id.Embedding);
    }

    private static double Cosine(float[] a, float[] b)
    {
        // Both are L2-normalized, so cosine == dot product.
        double dot = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) dot += (double)a[i] * b[i];
        return dot;
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx, dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void Normalize(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        double norm = Math.Sqrt(sum);
        if (norm <= 1e-12) return;
        float inv = (float)(1.0 / norm);
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }
}
