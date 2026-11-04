namespace PitchWise.Vision;

/// <summary>
/// Aggregates on-pitch presence time per stable <see cref="Detection.PlayerId"/> from a
/// stream of <see cref="FrameResult"/>s (which must already carry PlayerIds assigned by
/// <see cref="PlayerReId"/>). Feed frames in ascending timestamp order via <see cref="Add"/>;
/// call <see cref="Report"/> at the end of the run.
///
/// Time model: each frame a player appears in contributes the interval since that player's
/// previous appearance, but only if the gap is short enough to be one continuous presence
/// (<see cref="_maxGapSeconds"/>). A longer gap (player off-screen / lost for a while) is
/// NOT counted as on-pitch time — it starts a fresh presence interval. This bridges brief
/// occlusions without inventing time across genuine absences.
///
/// Pure bookkeeping over FrameResult, so it works identically in the worker (recorded) and,
/// later, in the live path.
/// </summary>
public sealed class TimeOnPitchTracker
{
    private sealed class Acc
    {
        public double Seconds;
        public double FirstSeen;
        public double LastSeen;
        public int Frames;
    }

    private readonly Dictionary<int, Acc> _byPlayer = new();
    private readonly double _maxGapSeconds;

    /// <param name="maxGapSeconds">Max gap between two appearances still counted as one
    /// continuous presence. Should comfortably exceed the frame stride period so normal
    /// stride sampling isn't treated as a gap. Default 2s.</param>
    public TimeOnPitchTracker(double maxGapSeconds = 2.0) => _maxGapSeconds = maxGapSeconds;

    /// <summary>Folds one processed frame into the per-player accumulators.</summary>
    public void Add(FrameResult frame)
    {
        double ts = frame.TimestampSeconds;
        // A player may appear once per frame; guard against duplicate ids within a frame.
        var seen = new HashSet<int>();
        foreach (Detection d in frame.Detections)
        {
            if (d.PlayerId is not int pid) continue;
            if (!seen.Add(pid)) continue;

            if (!_byPlayer.TryGetValue(pid, out Acc? acc))
            {
                acc = new Acc { FirstSeen = ts, LastSeen = ts, Frames = 0, Seconds = 0 };
                _byPlayer[pid] = acc;
            }
            else
            {
                double gap = ts - acc.LastSeen;
                if (gap > 0 && gap <= _maxGapSeconds) acc.Seconds += gap;
                // else: genuine absence — don't count the gap, just resume from here.
            }
            acc.LastSeen = ts;
            acc.Frames++;
        }
    }

    /// <summary>Final per-player time-on-pitch report, ordered by descending time.</summary>
    public IReadOnlyList<PlayerTimeOnPitch> Report()
    {
        var list = new List<PlayerTimeOnPitch>(_byPlayer.Count);
        foreach ((int pid, Acc a) in _byPlayer)
            list.Add(new PlayerTimeOnPitch(pid, a.Seconds, a.FirstSeen, a.LastSeen, a.Frames));
        list.Sort((x, y) => y.SecondsOnPitch.CompareTo(x.SecondsOnPitch));
        return list;
    }
}
