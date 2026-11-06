using PitchWise.Data.Models;
using PitchWise.Worker;

namespace PitchWise.Worker.Tests;

public class RecordingTimelineTests
{
    private static readonly DateTime Zero = new(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc);

    private static RecordedSegment Segment(string path, double start, double length) =>
        new(path, Zero.AddSeconds(start), length);

    private static double? NoKeyframeNeeded(string path, double position) =>
        throw new InvalidOperationException("nothing here needs trimming");

    [Fact]
    public void Consecutive_Segments_HaveNoGap_AndKeepTheirClockPositions()
    {
        var plan = RecordingTimeline.Plan([Segment("a", 0.001, 60.2), Segment("b", 60.05, 60.1)], Zero, NoKeyframeNeeded);

        Assert.Empty(plan.Gaps);
        Assert.Equal(["a", "b"], plan.Entries.Select(e => e.Path));
        Assert.Equal(60.05, plan.Entries[0].Until!.Value, 3);
        Assert.Null(plan.Entries[1].Until);
        Assert.Equal(120.15, plan.End, 3);
    }

    [Fact]
    public void LostSignal_LeavesAGap_AtItsTimeOnTheClock()
    {
        var plan = RecordingTimeline.Plan([Segment("a", 0, 49), Segment("b", 65, 11)], Zero, NoKeyframeNeeded);

        var gap = Assert.Single(plan.Gaps);
        Assert.Equal(49, gap.Start, 3);
        Assert.Equal(65, gap.End, 3);
        Assert.Equal(65, plan.Entries[0].Until!.Value, 3);
        Assert.Equal(76, plan.End, 3);
    }

    [Fact]
    public void ShortHole_IsNotReportedAsAGap_ButStaysOnTheClock()
    {
        var plan = RecordingTimeline.Plan([Segment("a", 0, 60), Segment("b", 60.5, 60)], Zero, NoKeyframeNeeded);

        Assert.Empty(plan.Gaps);
        Assert.Equal(60.5, plan.Entries[0].Until!.Value, 3);
    }

    // Measured after a stalled encoder: file names overlapped the media by 1.27 s, and letting
    // timestamps go backwards made ffmpeg squeeze frames together.
    [Fact]
    public void Overlap_AfterAStall_GivesWay_SoTimestampsNeverGoBack()
    {
        var plan = RecordingTimeline.Plan([Segment("a", 0, 7.6), Segment("b", 6.3, 6)], Zero, NoKeyframeNeeded);

        Assert.Empty(plan.Gaps);
        Assert.Equal(7.6, plan.Entries[0].Until!.Value, 3);
        Assert.Equal(13.6, plan.End, 3);
    }

    [Fact]
    public void Overlap_FromHowSegmentsSplit_DoesNotMoveThem()
    {
        var plan = RecordingTimeline.Plan([Segment("a", 0, 60.2), Segment("b", 59.9, 60)], Zero, NoKeyframeNeeded);

        Assert.Equal(59.9, plan.Entries[0].Until!.Value, 3);
    }

    [Fact]
    public void Overlaps_DoNotAddUp_OverAMatch()
    {
        // Each file looks 0.2 s longer than the clock says, as they do in practice.
        var segments = Enumerable.Range(0, 120).Select(i => Segment($"s{i}", i * 60.0, 60.2)).ToList();

        var plan = RecordingTimeline.Plan(segments, Zero, NoKeyframeNeeded);

        Assert.Equal(119 * 60 + 60.2, plan.End, 3);
    }

    [Fact]
    public void LateInput_StillStartsAtPositionZero_WithAGapUntilItsSignal()
    {
        var plan = RecordingTimeline.Plan([Segment("a", 30, 60)], Zero, NoKeyframeNeeded);

        var gap = Assert.Single(plan.Gaps);
        Assert.Equal((0.0, 30.0), (gap.Start, gap.End));
        // a sliver of its first picture stands in until the real start
        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(RecordingTimeline.StubLength, plan.Entries[0].OutPoint);
        Assert.Equal(30, plan.Entries[0].Until!.Value, 3);
        Assert.Equal(90, plan.End, 3);
    }

    [Fact]
    public void EarlyInput_IsCutAtAKeyframe_SoItKeepsItsPlaceOnTheClock()
    {
        var plan = RecordingTimeline.Plan(
            [Segment("before", -200, 60), Segment("a", -7, 60), Segment("b", 53, 60)], Zero,
            (path, position) =>
            {
                Assert.Equal("a", path);
                Assert.Equal(7, position, 3);
                return 8;
            });

        // the next keyframe comes a second after zero, so the first second is the stand-in
        Assert.Equal(["a", "a", "b"], plan.Entries.Select(e => e.Path));
        Assert.Equal(8, plan.Entries[0].InPoint);
        Assert.Equal(8 + RecordingTimeline.StubLength, plan.Entries[0].OutPoint);
        Assert.Equal(1, plan.Entries[0].Until!.Value, 3);
        Assert.Equal(8, plan.Entries[1].InPoint);
        Assert.Equal(52, plan.Entries[1].Until!.Value, 3);
        Assert.Equal(113, plan.End, 3);
    }

    [Fact]
    public void InputThatStoppedBeforeTheMatchStarted_HasNothingToPlace()
    {
        var plan = RecordingTimeline.Plan([Segment("a", -200, 60)], Zero, NoKeyframeNeeded);

        Assert.Empty(plan.Entries);
    }

    [Fact]
    public void ConcatList_IsWhatFfmpegExpects_WhateverTheCulture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pl-PL");
        try
        {
            var list = RecordingTimeline.ToConcatList([new ConcatEntry("/r/it's.mp4", 1.5, 2.5, 3.5)]);
            Assert.Equal("ffconcat version 1.0\nfile '/r/it'\\''s.mp4'\ninpoint 1.500000\noutpoint 2.500000\nduration 3.500000\n", list);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("1793919913-493818.mp4", true)]
    [InlineData("/x/live/id/1793919913-000001.mp4", true)]
    [InlineData("2026-11-05_22-22-58-659972.mp4", false)]
    [InlineData("nonsense.mp4", false)]
    public void SegmentNames_AreUnixTime(string name, bool valid)
    {
        Assert.Equal(valid, RecordedSegment.TryParseStart(name, out var start));
        if (valid) Assert.Equal(1793919913L, new DateTimeOffset(start).ToUnixTimeSeconds());
    }

    [Fact]
    public void SegmentName_KeepsItsMicroseconds()
    {
        RecordedSegment.TryParseStart("100-000250.mp4", out var start);

        Assert.Equal(DateTime.UnixEpoch.AddSeconds(100).AddTicks(2500), start);
    }
}

public class ClipPlannerTests
{
    private static List<TimelineGap> Gap(double start, double end) => [new TimelineGap { Start = start, End = end }];

    [Fact]
    public void WithoutGaps_CutsAroundTheEvent_NeverBeforeZero()
    {
        Assert.Equal((94.0, 104.0), ClipPlanner.Plan(100, 6, 4, []));
        Assert.Equal((0.0, 7.0), ClipPlanner.Plan(3, 6, 4, []));
    }

    [Fact]
    public void StopsAtAGapAfterTheEvent()
    {
        Assert.Equal((42.0, 48.75), ClipPlanner.Plan(48, 6, 4, Gap(49, 65)));
    }

    [Fact]
    public void StartsAfterAGapBeforeTheEvent()
    {
        Assert.Equal((65.25, 70.0), ClipPlanner.Plan(66, 6, 4, Gap(49, 65)));
    }

    [Fact]
    public void EventInsideAGap_MovesToWhenThePictureReturns()
    {
        Assert.Equal((65.25, 69.25), ClipPlanner.Plan(55, 6, 4, Gap(49, 65)));
    }

    [Fact]
    public void EventRightBesideAGap_StaysInsideItsClip()
    {
        Assert.Equal((43.0, 49.0), ClipPlanner.Plan(49, 6, 4, Gap(49.1, 65)));
        Assert.Equal((65.1, 69.1), ClipPlanner.Plan(65.1, 6, 4, Gap(49, 65)));
    }

    [Fact]
    public void GapElsewhere_ChangesNothing()
    {
        Assert.Equal((94.0, 104.0), ClipPlanner.Plan(100, 6, 4, Gap(10, 20)));
    }
}
