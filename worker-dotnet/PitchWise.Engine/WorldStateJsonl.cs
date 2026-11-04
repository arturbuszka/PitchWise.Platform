using System.Globalization;
using System.Text;

namespace PitchWise.Engine;

/// <summary>
/// The offline feedback loop. One JSON object per line: a header, then one
/// <see cref="FrameObservation"/> per processed frame.
///
/// <b>Observations, not world states.</b> The dump records what the engine was <i>fed</i>, not
/// what it concluded. Recording the conclusions would bake the possession thresholds and the
/// Kalman noise into the file, and those are exactly the numbers that need tuning. Feeding
/// observations back through a fresh <see cref="WorldStateBuilder"/> means every parameter in
/// <see cref="EngineConfig"/> stays adjustable without decoding a single video frame.
///
/// Written next to <c>time_on_pitch.json</c>, and like it, best-effort: a failed dump must never
/// take down an analysis run.
/// </summary>
public sealed class WorldStateJsonl : IDisposable
{
    /// <summary>First line of the file. Records the run's context — especially the two team
    /// colours, so a bad k=2 clustering can be diagnosed straight from the dump rather than by
    /// squinting at the annotated video.</summary>
    /// <param name="TeamColors">Hex RGB of the two jersey-colour centroids, or null when the
    /// vision layer resolved no teams.</param>
    public readonly record struct Header(
        double Fps,
        int FrameStride,
        double PitchLength,
        double PitchWidth,
        bool NormalizedCoords,
        string? TeamColorA = null,
        string? TeamColorB = null);

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;

    public WorldStateJsonl(TextWriter writer, Header header, bool ownsWriter = false)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
        WriteHeader(header);
    }

    /// <summary>Opens <paramref name="path"/> for writing. Returns null if it cannot be created —
    /// callers treat the dump as optional diagnostics.</summary>
    public static WorldStateJsonl? TryCreate(string path, Header header)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sw = new StreamWriter(path, append: false);
            return new WorldStateJsonl(sw, header, ownsWriter: true);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void WriteHeader(Header h)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"type\":\"header\",\"fps\":").Append(F(h.Fps))
          .Append(",\"stride\":").Append(h.FrameStride)
          .Append(",\"pitchLength\":").Append(F(h.PitchLength))
          .Append(",\"pitchWidth\":").Append(F(h.PitchWidth))
          .Append(",\"normalizedCoords\":").Append(h.NormalizedCoords ? "true" : "false");
        if (h.TeamColorA is not null) sb.Append(",\"teamColorA\":\"").Append(h.TeamColorA).Append('"');
        if (h.TeamColorB is not null) sb.Append(",\"teamColorB\":\"").Append(h.TeamColorB).Append('"');
        sb.Append('}');
        _writer.WriteLine(sb.ToString());
    }

    /// <summary>Appends one frame. Compact by hand: at 25 fps a full match is ~135k lines, and
    /// pretty-printing costs real disk.</summary>
    public void Append(FrameObservation obs)
    {
        var sb = new StringBuilder(64 + obs.Players.Count * 48);
        sb.Append("{\"f\":").Append(obs.FrameIndex)
          .Append(",\"t\":").Append(F(obs.TimestampSeconds))
          .Append(",\"n\":").Append(obs.NormalizedCoords ? "true" : "false")
          .Append(",\"ball\":");
        if (obs.Ball.Detected)
            sb.Append("{\"x\":").Append(F(obs.Ball.X))
              .Append(",\"y\":").Append(F(obs.Ball.Y))
              .Append(",\"c\":").Append(F(obs.Ball.Confidence)).Append('}');
        else
            sb.Append("null");

        sb.Append(",\"p\":[");
        for (int i = 0; i < obs.Players.Count; i++)
        {
            PlayerObservation p = obs.Players[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(p.PlayerId)
              .Append(",\"r\":").Append((int)p.Role)
              .Append(",\"tm\":").Append((int)p.Team)
              .Append(",\"x\":").Append(F(p.X))
              .Append(",\"y\":").Append(F(p.Y))
              .Append(",\"c\":").Append(F(p.Confidence)).Append('}');
        }
        sb.Append("]}");
        _writer.WriteLine(sb.ToString());
    }

    /// <summary>Three decimals: a millimetre on the pitch, well below the homography's error.</summary>
    private static string F(double v) =>
        Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        try { _writer.Flush(); } catch (IOException) { }
        if (_ownsWriter) _writer.Dispose();
    }
}
