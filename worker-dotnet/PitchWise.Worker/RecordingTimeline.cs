using System.Globalization;
using PitchWise.Data.Models;

namespace PitchWise.Worker;

/// <summary>One file MediaMTX wrote: when it started, and how much media it holds.</summary>
public sealed record RecordedSegment(string Path, DateTime StartUtc, double Length)
{
    /// <summary>MediaMTX names files {unix seconds}-{microseconds}.mp4, which no time zone can change.</summary>
    public static bool TryParseStart(string fileName, out DateTime startUtc)
    {
        startUtc = default;
        string[] parts = System.IO.Path.GetFileNameWithoutExtension(fileName).Split('-');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long micros))
            return false;
        startUtc = DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(micros * 10);
        return true;
    }
}

/// <summary>One line of an ffmpeg concat list. Until says when the next entry starts.</summary>
public sealed record ConcatEntry(string Path, double? InPoint, double? OutPoint, double? Until);

public sealed record RecordingPlan(IReadOnlyList<ConcatEntry> Entries, IReadOnlyList<TimelineGap> Gaps, double End);

/// <summary>
/// Places an input's segments on the match timeline, where a position T is T seconds after the
/// match started by the clock, with signal gaps counted. Everything that later uses a recording
/// (players, highlights, analysis) then shares one meaning of T, and no one has to convert anything.
/// A gap stays in the file as a hole in the timestamps, which costs no re-encoding.
/// </summary>
public static class RecordingTimeline
{
    /// <summary>Consecutive segments overlap this much anyway, by how they split audio and video.</summary>
    public const double OverlapTolerance = 0.5;
    /// <summary>A shorter hole between segments isn't worth telling a viewer about.</summary>
    public const double MinGap = 1.0;
    /// <summary>A stretch of the first segment that stands in for a late input until its real start.</summary>
    public const double StubLength = 0.1;
    /// <summary>A start this close to zero is treated as zero, rather than as something to fix.</summary>
    public const double StartTolerance = 0.2;

    public static RecordingPlan Plan(
        IReadOnlyList<RecordedSegment> segments, DateTime zero, Func<string, double, double?> keyframeAtOrAfter)
    {
        var placed = new List<(RecordedSegment Segment, double Start, double? InPoint)>();
        foreach (RecordedSegment segment in segments.OrderBy(s => s.StartUtc))
        {
            double start = (segment.StartUtc - zero).TotalSeconds;
            double? inPoint = null;

            // Started before the match did, as a camera switched on early does.
            if (placed.Count == 0 && start < -StartTolerance)
            {
                if (start + segment.Length <= 0) continue;
                // Only a cut at a keyframe keeps its place on the timeline. Anywhere else, ffmpeg
                // starts from the keyframe before it and shifts the whole file back by the difference.
                inPoint = keyframeAtOrAfter(segment.Path, -start);
                if (inPoint is null) continue;
                start += inPoint.Value;
            }

            if (placed.Count > 0)
            {
                var (previous, previousStart, previousIn) = placed[^1];
                double previousEnd = previousStart + previous.Length - (previousIn ?? 0);
                // After a stalled encoder catches up, its media runs ahead of the clock for a while.
                // Timestamps must not go backwards, so the timeline gives way until the clock passes.
                if (previousEnd - start > OverlapTolerance) start = previousEnd;
            }
            placed.Add((segment, start, inPoint));
        }

        if (placed.Count == 0) return new RecordingPlan([], [], 0);

        var entries = new List<ConcatEntry>();
        var gaps = new List<TimelineGap>();

        var first = placed[0];
        double firstStart = first.Start <= StartTolerance ? 0 : first.Start;
        if (firstStart > 0)
        {
            // An input that started late still has to start at position zero. An edit list or an
            // offset can't do it, since Firefox ignores one and its positions would be off by that much.
            double from = first.InPoint ?? 0;
            entries.Add(new ConcatEntry(first.Segment.Path, first.InPoint, from + StubLength, firstStart));
            if (firstStart >= MinGap) gaps.Add(new TimelineGap { Start = 0, End = firstStart });
        }

        double end = 0;
        for (int i = 0; i < placed.Count; i++)
        {
            var (segment, start, inPoint) = placed[i];
            if (i == 0) start = firstStart;
            double segmentEnd = start + segment.Length - (inPoint ?? 0);
            double? next = i + 1 < placed.Count ? placed[i + 1].Start : null;

            entries.Add(new ConcatEntry(segment.Path, inPoint, null, next is null ? null : next - start));
            if (next is double n && n - segmentEnd >= MinGap)
                gaps.Add(new TimelineGap { Start = segmentEnd, End = n });

            end = segmentEnd;
        }
        return new RecordingPlan(entries, gaps, end);
    }

    public static string ToConcatList(IEnumerable<ConcatEntry> entries)
    {
        var list = new System.Text.StringBuilder("ffconcat version 1.0\n");
        foreach (ConcatEntry e in entries)
        {
            list.Append("file '").Append(System.IO.Path.GetFullPath(e.Path).Replace("'", "'\\''")).Append("'\n");
            if (e.InPoint is double i) list.Append(CultureInfo.InvariantCulture, $"inpoint {i:F6}\n");
            if (e.OutPoint is double o) list.Append(CultureInfo.InvariantCulture, $"outpoint {o:F6}\n");
            if (e.Until is double u) list.Append(CultureInfo.InvariantCulture, $"duration {u:F6}\n");
        }
        return list.ToString();
    }
}
