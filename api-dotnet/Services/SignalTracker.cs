using PitchWise.Api.Dtos;
using PitchWise.Data.Models;

namespace PitchWise.Api.Services;

/// <summary>
/// Turns the running counters MediaMTX reports into what an operator wants to know: the bitrate and
/// loss over the last few seconds, and whether data is still arriving. A connected encoder that has
/// stopped sending still looks ready to MediaMTX, so this is the only place that can tell.
///
/// Kept in memory because it changes every poll and is worthless after a restart. Each API instance
/// polls for itself, so instances need nothing from each other. Update has one caller at a time.
/// </summary>
public class SignalTracker
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(5);

    private static readonly SignalOut NoSignal =
        new(SignalState.NoSignal, null, null, null, null, null, null, null, []);

    private readonly Dictionary<string, History> _histories = [];
    private volatile IReadOnlyDictionary<string, SignalOut> _signals = new Dictionary<string, SignalOut>();

    public SignalOut Get(string path) => _signals.GetValueOrDefault(path, NoSignal);

    public void Update(IReadOnlyDictionary<string, Publisher> publishers, DateTimeOffset now)
    {
        foreach (var gone in _histories.Keys.Except(publishers.Keys).ToList())
            _histories.Remove(gone);

        var signals = new Dictionary<string, SignalOut>();
        foreach (var (path, publisher) in publishers)
        {
            // A new connection restarts the counters, so nothing from the old one can be compared.
            if (!_histories.TryGetValue(path, out var history) || history.ConnectionId != publisher.ConnectionId)
                _histories[path] = history = new History(publisher.ConnectionId, now);

            signals[path] = history.Add(publisher, now);
        }
        _signals = signals;
    }

    private sealed class History(string connectionId, DateTimeOffset connectedAt)
    {
        private readonly Queue<(DateTimeOffset At, long Bytes, SrtStats? Srt)> _samples = new();
        private DateTimeOffset _lastData = connectedAt;
        private long _lastBytes = -1;

        public string ConnectionId { get; } = connectionId;

        public SignalOut Add(Publisher publisher, DateTimeOffset now)
        {
            if (publisher.BytesReceived != _lastBytes)
            {
                _lastBytes = publisher.BytesReceived;
                _lastData = now;
            }

            _samples.Enqueue((now, publisher.BytesReceived, publisher.Srt));
            while (now - _samples.Peek().At > Window) _samples.Dequeue();

            var oldest = _samples.Peek();
            var stalled = now - _lastData >= StallAfter;
            var seconds = (now - oldest.At).TotalSeconds;

            double? bitrate = null;
            if (stalled) bitrate = 0;
            else if (seconds > 0) bitrate = Math.Round((publisher.BytesReceived - oldest.Bytes) * 8 / 1000.0 / seconds);

            return new SignalOut(
                stalled ? SignalState.Stalled : SignalState.Receiving,
                publisher.Protocol,
                publisher.ConnectedSince,
                bitrate,
                LossPercent(oldest.Srt, publisher.Srt),
                publisher.Srt?.PacketsLost,
                publisher.Srt?.PacketsDropped,
                publisher.Srt is null ? null : Math.Round(publisher.Srt.RttMs, 1),
                publisher.Tracks);
        }

        // Packets found missing on the way, most of which SRT then retransmits in time. Dropped
        // packets are the ones it gave up on, which viewers do see.
        private static double? LossPercent(SrtStats? from, SrtStats? to)
        {
            if (from is null || to is null) return null;
            var lost = to.PacketsLost - from.PacketsLost;
            var total = to.PacketsReceived - from.PacketsReceived + (to.PacketsDropped - from.PacketsDropped);
            return total <= 0 ? 0 : Math.Round(100.0 * lost / total, 2);
        }
    }
}
