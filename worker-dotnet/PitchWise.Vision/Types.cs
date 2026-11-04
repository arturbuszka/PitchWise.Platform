namespace PitchWise.Vision;

/// <summary>
/// Object classes we expect from a football-specific model (Roboflow sports /
/// football-players-detection). Mapping the model's class indices onto these names
/// happens in <see cref="Yolo11OnnxDetector"/>.
/// </summary>
public static class ObjectClass
{
    public const string Ball = "ball";
    public const string Player = "player";
    public const string Goalkeeper = "goalkeeper";
    public const string Referee = "referee";
    // Stage 2 hook: goal detection (posts/net). Inactive until the model returns it.
    public const string Goal = "goal";
}

/// <summary>A single detection in original-image pixel coordinates.</summary>
/// <param name="Cls">One of the <see cref="ObjectClass"/> names.</param>
/// <param name="X1">Left.</param>
/// <param name="Y1">Top.</param>
/// <param name="X2">Right.</param>
/// <param name="Y2">Bottom.</param>
/// <param name="Confidence">Detection score.</param>
/// <param name="TrackId">Assigned by the tracker; null before tracking. Volatile — a
/// single player may get a new TrackId after occlusion / leaving the frame.</param>
/// <param name="PlayerId">Stable match-long identity assigned by <see cref="PlayerReId"/>
/// on top of <paramref name="TrackId"/> (merges switched track ids); null before Re-ID.</param>
public readonly record struct Detection(
    string Cls,
    double X1,
    double Y1,
    double X2,
    double Y2,
    double Confidence,
    int? TrackId = null,
    int? PlayerId = null)
{
    public double CenterX => (X1 + X2) / 2.0;
    public double CenterY => (Y1 + Y2) / 2.0;
}

/// <summary>Detections for a single processed frame.</summary>
public sealed class FrameResult
{
    public int FrameIndex { get; init; }
    public double TimestampSeconds { get; init; }
    public IReadOnlyList<Detection> Detections { get; init; } = Array.Empty<Detection>();

    /// <summary>Highest-confidence ball detection in this frame, or null.</summary>
    public Detection? Ball
    {
        get
        {
            Detection? best = null;
            foreach (Detection d in Detections)
            {
                if (d.Cls != ObjectClass.Ball) continue;
                if (best is null || d.Confidence > best.Value.Confidence) best = d;
            }
            return best;
        }
    }
}

/// <summary>A detected match event (goal / shot). Port of DetectedEvent.</summary>
/// <param name="Type">"goal" | "shot".</param>
public readonly record struct DetectedEvent(
    string Type,
    double TimestampSeconds,
    double Confidence,
    string? Label = null);

/// <summary>Aggregated on-pitch presence for one stable <see cref="Detection.PlayerId"/>,
/// produced by <see cref="TimeOnPitchTracker"/>.</summary>
/// <param name="PlayerId">Stable player identity (from <see cref="PlayerReId"/>).</param>
/// <param name="SecondsOnPitch">Total presence in seconds (short gaps bridged).</param>
/// <param name="FirstSeenSeconds">Absolute video timestamp of first appearance.</param>
/// <param name="LastSeenSeconds">Absolute video timestamp of last appearance.</param>
/// <param name="FramesSeen">Number of processed frames the player appeared in.</param>
public readonly record struct PlayerTimeOnPitch(
    int PlayerId,
    double SecondsOnPitch,
    double FirstSeenSeconds,
    double LastSeenSeconds,
    int FramesSeen);

/// <summary>Per-team match aggregates, produced by <see cref="MatchStatsTracker"/>.</summary>
/// <param name="PossessionPct">Share of controlled time held by this team, 0..100. Computed over
/// time the ball was controlled by SOMEONE (loose/contested time is excluded from the split, so
/// A + B = 100).</param>
/// <param name="Passes">Completed passes to a team-mate.</param>
/// <param name="Turnovers">Passes that went to the opposition (a lost ball).</param>
/// <param name="PassAccuracyPct">Passes / (Passes + Turnovers) * 100, or 0 when neither.</param>
public readonly record struct TeamStats(
    double PossessionPct,
    int Passes,
    int Turnovers,
    double PassAccuracyPct);

/// <summary>Whole-video match statistics. Zero/empty until the engine runs with real pitch
/// coordinates (needs a homography); reported regardless so the UI can render an honest empty
/// state rather than nothing.</summary>
/// <param name="ControlledSeconds">Total time the ball was under someone's control (the
/// denominator behind the possession split).</param>
/// <param name="LooseSeconds">Time the ball was loose or contested (not attributed to a team).</param>
/// <param name="TeamA">Team A aggregates.</param>
/// <param name="TeamB">Team B aggregates.</param>
public readonly record struct MatchStatsReport(
    double ControlledSeconds,
    double LooseSeconds,
    TeamStats TeamA,
    TeamStats TeamB);
