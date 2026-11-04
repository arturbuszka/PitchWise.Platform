using System.Diagnostics;

namespace PitchWise.Vision;

/// <summary>Rolling FPS / inference-time stats for live sessions.</summary>
public sealed class StatsTracker
{
    private readonly int _window;
    private readonly Queue<double> _inferTimes = new();
    private readonly Queue<double> _frameTimes = new();
    private long _lastFrameTicks;
    private bool _hasLast;

    public IReadOnlyDictionary<string, int> LastCounts { get; private set; } =
        new Dictionary<string, int>();

    public StatsTracker(int window = 30) => _window = window;

    public void Record(double inferMs, IReadOnlyDictionary<string, int> counts)
    {
        Push(_inferTimes, inferMs);
        LastCounts = counts;
        long now = Stopwatch.GetTimestamp();
        if (_hasLast)
            Push(_frameTimes, Stopwatch.GetElapsedTime(_lastFrameTicks, now).TotalSeconds);
        _lastFrameTicks = now;
        _hasLast = true;
    }

    public double Fps()
    {
        if (_frameTimes.Count < 2) return 0.0;
        double avg = _frameTimes.Average();
        return avg > 0 ? 1.0 / avg : 0.0;
    }

    public double AvgInferMs() => _inferTimes.Count == 0 ? 0.0 : _inferTimes.Average();

    private void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > _window) q.Dequeue();
    }
}
