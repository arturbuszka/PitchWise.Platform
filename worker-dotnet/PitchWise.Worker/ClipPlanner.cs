using PitchWise.Data.Models;

namespace PitchWise.Worker;

/// <summary>Chooses the part of a video to cut for an event, keeping out of signal gaps.</summary>
public static class ClipPlanner
{
    /// <summary>
    /// How far a cut keeps from a gap. The last packet before a gap carries the gap's whole length as
    /// its own duration, and a cut just after one could seek back to the keyframe before the gap.
    /// </summary>
    public const double GapMargin = 0.25;

    /// <summary>
    /// Normally pre seconds before the event to post seconds after it. A gap can't be cut across,
    /// because a stream copy would hold one frame for its whole length, so the clip stops short of
    /// it. An event inside a gap, which can only come from marking it live, moves to the moment the
    /// picture returns.
    /// </summary>
    public static (double Start, double End) Plan(
        double timestamp, double pre, double post, IReadOnlyList<TimelineGap> gaps)
    {
        double start = Math.Max(0.0, timestamp - pre);
        double end = timestamp + post;

        foreach (TimelineGap gap in gaps)
        {
            if (timestamp >= gap.Start && timestamp < gap.End)
            {
                timestamp = gap.End + GapMargin;
                start = timestamp;
                end = timestamp + post;
            }
            else if (gap.End <= timestamp && gap.End + GapMargin > start)
            {
                start = Math.Min(gap.End + GapMargin, timestamp);
            }
            else if (gap.Start > timestamp && gap.Start - GapMargin < end)
            {
                end = Math.Max(gap.Start - GapMargin, timestamp);
            }
        }
        return (start, end);
    }
}
