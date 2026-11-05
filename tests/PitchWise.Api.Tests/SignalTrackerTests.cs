using PitchWise.Api.Services;
using PitchWise.Data.Models;

namespace PitchWise.Api.Tests;

public class SignalTrackerTests
{
    private const string Path = "live/a";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, Publisher> Rtmp(long bytes, string connection = "c1") => new()
    {
        [Path] = new Publisher(connection, "rtmp", T0.UtcDateTime, bytes, ["H264"]),
    };

    private static Dictionary<string, Publisher> Srt(long bytes, long received, long lost, long dropped = 0) => new()
    {
        [Path] = new Publisher("c1", "srt", T0.UtcDateTime, bytes, ["H264"], new SrtStats(received, lost, dropped, 12.34)),
    };

    [Fact]
    public void NoPublisher_MeansNoSignal()
    {
        var signal = new SignalTracker().Get(Path);

        Assert.Equal(SignalState.NoSignal, signal.State);
        Assert.Null(signal.BitrateKbps);
    }

    [Fact]
    public void FirstReading_HasNoBitrateYet()
    {
        var tracker = new SignalTracker();

        tracker.Update(Rtmp(1_000), T0);

        Assert.Equal(SignalState.Receiving, tracker.Get(Path).State);
        Assert.Null(tracker.Get(Path).BitrateKbps);
    }

    [Fact]
    public void Bitrate_ComesFromTheBytesBetweenReadings()
    {
        var tracker = new SignalTracker();

        tracker.Update(Rtmp(1_000_000), T0);
        tracker.Update(Rtmp(1_500_000), T0.AddSeconds(2));

        Assert.Equal(2000, tracker.Get(Path).BitrateKbps);
    }

    [Fact]
    public void Bitrate_ForgetsReadingsOutsideTheWindow()
    {
        var tracker = new SignalTracker();

        // a burst long ago, then a steady 1000 kbps
        tracker.Update(Rtmp(0), T0);
        tracker.Update(Rtmp(50_000_000), T0.AddSeconds(2));
        for (var s = 4; s <= 20; s += 2)
            tracker.Update(Rtmp(50_000_000 + (s - 2) * 125_000L), T0.AddSeconds(s));

        Assert.Equal(1000, tracker.Get(Path).BitrateKbps);
    }

    [Fact]
    public void ConnectedButSilent_IsStalled_UntilDataReturns()
    {
        var tracker = new SignalTracker();

        tracker.Update(Rtmp(1_000), T0);
        tracker.Update(Rtmp(2_000), T0.AddSeconds(2));
        tracker.Update(Rtmp(2_000), T0.AddSeconds(4));
        Assert.Equal(SignalState.Receiving, tracker.Get(Path).State);

        tracker.Update(Rtmp(2_000), T0.AddSeconds(7));
        Assert.Equal(SignalState.Stalled, tracker.Get(Path).State);
        Assert.Equal(0, tracker.Get(Path).BitrateKbps);

        tracker.Update(Rtmp(3_000), T0.AddSeconds(9));
        Assert.Equal(SignalState.Receiving, tracker.Get(Path).State);
    }

    [Fact]
    public void Reconnect_DoesNotCompareCountersAcrossConnections()
    {
        var tracker = new SignalTracker();

        tracker.Update(Rtmp(9_000_000), T0);
        tracker.Update(Rtmp(9_500_000), T0.AddSeconds(2));
        tracker.Update(Rtmp(1_000, "c2"), T0.AddSeconds(4));

        Assert.Null(tracker.Get(Path).BitrateKbps);
    }

    [Fact]
    public void Disconnect_GoesBackToNoSignal()
    {
        var tracker = new SignalTracker();

        tracker.Update(Rtmp(1_000), T0);
        tracker.Update(new Dictionary<string, Publisher>(), T0.AddSeconds(2));

        Assert.Equal(SignalState.NoSignal, tracker.Get(Path).State);
    }

    [Fact]
    public void Srt_ReportsLossOverTheWindow_AndRtmpDoesNot()
    {
        var tracker = new SignalTracker();

        tracker.Update(Srt(1_000, received: 1_000, lost: 500), T0);
        tracker.Update(Srt(2_000, received: 2_000, lost: 550), T0.AddSeconds(2));

        var signal = tracker.Get(Path);
        Assert.Equal(5, signal.LossPercent);
        Assert.Equal(550, signal.PacketsLost);
        Assert.Equal(12.3, signal.RttMs);

        var rtmp = new SignalTracker();
        rtmp.Update(Rtmp(1_000), T0);
        rtmp.Update(Rtmp(2_000), T0.AddSeconds(2));
        Assert.Null(rtmp.Get(Path).LossPercent);
    }
}
