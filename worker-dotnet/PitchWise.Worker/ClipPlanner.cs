using PitchWise.Data.Models;

namespace PitchWise.Worker;

/// <summary>Chooses the part of a video to cut for an event, keeping out of signal gaps.</summary>
public static class ClipPlanner
{
    /// <summary>
    /// Normally pre seconds before the event to post seconds after it. A gap can't be cut around,
    /// because a stream copy would hold one frame for its whole length, so the clip stops at the
    /// gap. An event inside a gap, which can only come from marking it live, moves to the moment the
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
                timestamp = gap.End;
                start = gap.End;
                end = gap.End + post;
            }
            else if (gap.End <= timestamp && gap.End > start)
            {
                start = gap.End;
            }
            else if (gap.Start > timestamp && gap.Start < end)
            {
                end = gap.Start;
            }
        }
        return (start, end);
    }
}
