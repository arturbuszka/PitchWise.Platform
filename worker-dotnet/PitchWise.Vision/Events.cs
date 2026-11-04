namespace PitchWise.Vision;

/// <summary>Event-detection tuning parameters.</summary>
public sealed class EventConfig
{
    // --- speed "spike" detection ---
    public double SpeedSpikeFactor { get; init; } = 3.0;
    public double MinSpeedPx { get; init; } = 25.0;
    public double CooldownSeconds { get; init; } = 8.0;

    // --- trajectory quality (noise reduction from wrong YOLO detections) ---
    public int MaxGapFrames { get; init; } = 2;
    public int SmoothWindow { get; init; } = 3;
    public double MinBallConfidence { get; init; } = 0.35;

    // --- frame-edge "goal" rule (goal proxy without homography) ---
    public int BallLostFrames { get; init; } = 4;
    public double EdgeZoneFrac { get; init; } = 0.15;
    public double DirectionConsistency { get; init; } = 0.6;
}

/// <summary>
/// Event detection heuristics (goal / shot) based on ball trajectory analysis.
/// Smoothed ball track, gap interpolation, moving-average speed, edge-zone + disappearance goal rule. Pure math, no ML.
/// </summary>
public static class Events
{
    private readonly record struct Sample(
        int Index, double TimestampSeconds, double Cx, double Cy, bool Interpolated = false);

    private static (double x, double y)? BallCenter(FrameResult fr, double minConf)
    {
        Detection? b = fr.Ball;
        if (b is null || b.Value.Confidence < minConf) return null;
        return (b.Value.CenterX, b.Value.CenterY);
    }

    private static (double w, double h) FrameSize(IReadOnlyList<FrameResult> frames)
    {
        double maxX = 0, maxY = 0;
        foreach (FrameResult fr in frames)
            foreach (Detection d in fr.Detections)
            {
                if (d.X2 > maxX) maxX = d.X2;
                if (d.Y2 > maxY) maxY = d.Y2;
            }
        return (maxX > 0 ? maxX : 1920.0, maxY > 0 ? maxY : 1080.0);
    }

    private static List<Sample> BuildTrack(IReadOnlyList<FrameResult> frames, EventConfig cfg)
    {
        var raw = new List<Sample>();
        for (int i = 0; i < frames.Count; i++)
        {
            (double x, double y)? c = BallCenter(frames[i], cfg.MinBallConfidence);
            if (c is not null)
                raw.Add(new Sample(i, frames[i].TimestampSeconds, c.Value.x, c.Value.y));
        }
        if (raw.Count < 2) return raw;

        var track = new List<Sample> { raw[0] };
        for (int k = 0; k < raw.Count - 1; k++)
        {
            Sample prev = raw[k], cur = raw[k + 1];
            int gap = cur.Index - prev.Index - 1;
            if (gap > 0 && gap <= cfg.MaxGapFrames)
            {
                for (int j = 1; j <= gap; j++)
                {
                    double t = j / (double)(gap + 1);
                    double cx = prev.Cx + (cur.Cx - prev.Cx) * t;
                    double cy = prev.Cy + (cur.Cy - prev.Cy) * t;
                    int idx = prev.Index + j;
                    track.Add(new Sample(idx, frames[idx].TimestampSeconds, cx, cy, true));
                }
            }
            track.Add(cur);
        }
        return track;
    }

    private static double[] SmoothedSpeeds(List<Sample> track, int window)
    {
        var raw = new double[track.Count];
        raw[0] = 0.0;
        for (int i = 1; i < track.Count; i++)
        {
            double dx = track[i].Cx - track[i - 1].Cx;
            double dy = track[i].Cy - track[i - 1].Cy;
            raw[i] = Math.Sqrt(dx * dx + dy * dy);
        }
        if (window <= 1) return raw;

        var smoothed = new double[raw.Length];
        int half = window / 2;
        for (int i = 0; i < raw.Length; i++)
        {
            int lo = Math.Max(0, i - half);
            int hi = Math.Min(raw.Length, i + half + 1);
            double sum = 0;
            for (int k = lo; k < hi; k++) sum += raw[k];
            smoothed[i] = sum / (hi - lo);
        }
        return smoothed;
    }

    private static (bool towardEdge, bool inZone) MovesTowardEdge(
        List<Sample> track, int pos, (double w, double h) frameSize, EventConfig cfg)
    {
        (double w, double h) = frameSize;
        Sample last = track[pos];
        double lx = last.Cx, ly = last.Cy;

        bool inZone =
            lx <= w * cfg.EdgeZoneFrac
            || lx >= w * (1 - cfg.EdgeZoneFrac)
            || ly <= h * cfg.EdgeZoneFrac
            || ly >= h * (1 - cfg.EdgeZoneFrac);

        int lo = Math.Max(0, pos - cfg.SmoothWindow - 1);
        int count = pos - lo + 1;
        if (count < 2) return (false, inZone);

        bool towardLeft = lx < w / 2;
        int closer = 0, steps = 0;
        for (int k = lo; k < pos; k++)
        {
            steps++;
            double moved = track[k + 1].Cx - track[k].Cx;
            if ((towardLeft && moved < 0) || (!towardLeft && moved > 0)) closer++;
        }
        bool consistent = steps > 0 && (closer / (double)steps) >= cfg.DirectionConsistency;
        return (consistent, inZone);
    }

    /// <summary>Detects goal/shot candidate events from a frame sequence.</summary>
    public static List<DetectedEvent> Detect(
        IReadOnlyList<FrameResult> frames, EventConfig? config = null)
    {
        EventConfig cfg = config ?? new EventConfig();
        var events = new List<DetectedEvent>();

        List<Sample> track = BuildTrack(frames, cfg);
        if (track.Count < 2) return events;

        (double w, double h) frameSize = FrameSize(frames);
        double[] speeds = SmoothedSpeeds(track, cfg.SmoothWindow);

        var sortedSpeeds = speeds.Where(s => s > 0).OrderBy(s => s).ToList();
        if (sortedSpeeds.Count == 0) return events;
        double median = sortedSpeeds[sortedSpeeds.Count / 2];
        double threshold = Math.Max(cfg.MinSpeedPx, median * cfg.SpeedSpikeFactor);

        var lastEventTs = new Dictionary<string, double>();

        void Emit(string type, double ts, double conf, string? label)
        {
            if (lastEventTs.TryGetValue(type, out double last) && ts - last < cfg.CooldownSeconds)
                return;
            events.Add(new DetectedEvent(type, ts, Math.Round(conf, 3), label));
            lastEventTs[type] = ts;
        }

        for (int pos = 0; pos < track.Count; pos++)
        {
            double speed = speeds[pos];
            if (speed < threshold) continue;
            double ts = track[pos].TimestampSeconds;

            int lastIndex = track[pos].Index;
            int lost = 0;
            int end = Math.Min(frames.Count, lastIndex + 1 + cfg.BallLostFrames + 2);
            for (int fi = lastIndex + 1; fi < end; fi++)
            {
                if (BallCenter(frames[fi], cfg.MinBallConfidence) is null) lost++;
                else break;
            }
            bool disappeared = lost >= cfg.BallLostFrames;

            (bool towardEdge, bool inZone) = MovesTowardEdge(track, pos, frameSize, cfg);

            if (disappeared && inZone)
            {
                double score = 0.4;
                if (towardEdge) score += 0.3;
                score += Math.Min(0.2, speed / (threshold * 4));
                Emit("goal", ts, Math.Min(0.95, score),
                    "candidate: goal (acceleration + motion toward edge + ball disappearance)");
            }
            else if (disappeared && !inZone)
            {
                // disappearance in frame center is most likely a detection error, not a goal.
                continue;
            }
            else
            {
                Emit("shot", ts, Math.Min(0.5, speed / (threshold * 3)),
                    "candidate: shot (ball acceleration)");
            }
        }

        return events;
    }
}
